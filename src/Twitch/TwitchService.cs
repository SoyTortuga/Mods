using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MoonSweepersTwitch.Config;
using MoonSweepersTwitch.Game;

namespace MoonSweepersTwitch.Twitch
{
    /// <summary>
    /// Wires everything together: authorize → resolve broadcaster → connect EventSub →
    /// subscribe to channel points / bits → translate each event into an effect using
    /// the configured mappings.
    /// </summary>
    public sealed class TwitchService : IDisposable
    {
        private const string RedemptionType = "channel.channel_points_custom_reward_redemption.add";
        private const string CheerType = "channel.cheer";
        // Fires for cheers, Twitch's own Power-ups and custom Power-ups; only custom
        // Power-ups are handled here (cheers arrive on channel.cheer).
        private const string BitsUseType = "channel.bits.use";

        private readonly PluginConfig _config;
        private readonly EffectRegistry _effects;
        private readonly HttpClient _http;
        private readonly TwitchAuth _auth;
        private readonly HelixApi _helix;
        private readonly EventSubClient _eventSub;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly Dictionary<string, string> _rewardByTitle = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _rewardById = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(int threshold, string key)> _bitRules = new();
        private readonly Dictionary<string, string> _powerUpByTitle = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _powerUpById = new(StringComparer.OrdinalIgnoreCase);

        private string _broadcasterId = "";

        public TwitchService(PluginConfig config, EffectRegistry effects)
        {
            _config = config;
            _effects = effects;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            var scopes = new List<string>();
            if (config.EnableChannelPoints.Value) scopes.Add("channel:read:redemptions");
            if (config.EnableBits.Value || config.EnablePowerUps.Value) scopes.Add("bits:read");
            if (scopes.Count == 0)
            {
                // Nothing enabled — still authorize with a harmless read scope so the
                // flow works; no subscriptions will be created.
                scopes.Add("user:read:email");
            }

            _auth = new TwitchAuth(config.ClientId.Value.Trim(), scopes.ToArray(), _http);
            _helix = new HelixApi(_http, _auth.ClientId);
            _eventSub = new EventSubClient
            {
                DebugLogging = config.DebugLogging.Value,
                OnSessionReady = CreateSubscriptionsAsync,
                OnNotification = HandleNotification,
            };

            ParseMappings();
        }

        public async Task StartAsync()
        {
            var ct = _cts.Token;

            string token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var (id, login) = await _helix.GetCurrentUserAsync(token, ct).ConfigureAwait(false);
            _broadcasterId = id;
            Plugin.Log.LogInfo($"Twitch authorized as '{login}' (id {id}).");

            if (_config.EnablePowerUps.Value)
                await CheckPowerUpsAsync(token, ct).ConfigureAwait(false);

            await _eventSub.RunAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Compare the channel's real custom Power-ups with PowerUpMappings and log what
        /// matches (with price), which mapped titles don't exist on Twitch, and which
        /// Power-ups nothing is mapped to. Twitch has no API for creating Power-ups, so this
        /// is how a typo is caught before a viewer pays for a Power-up that does nothing.
        /// Never fatal.
        /// </summary>
        private async Task CheckPowerUpsAsync(string token, CancellationToken ct)
        {
            if (_powerUpByTitle.Count == 0 && _powerUpById.Count == 0) return;

            List<HelixApi.CustomPowerUp> live;
            try
            {
                live = await _helix.GetCustomPowerUpsAsync(token, _broadcasterId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Plugin.Log.LogWarning("Power-up check skipped: couldn't list the channel's custom Power-ups: " + ex.Message);
                return;
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            int matched = 0, missing = 0;

            void Report(HelixApi.CustomPowerUp p, string key)
            {
                matched++;
                used.Add(p.Id);
                string state = !p.IsEnabled ? " (DISABLED on Twitch)" : p.IsPaused ? " (PAUSED on Twitch)" : "";
                string line = $"Power-up check: '{p.Title}' {p.Bits} Bits -> {key}{state}";
                if (state.Length > 0) Plugin.Log.LogWarning(line); else Plugin.Log.LogInfo(line);
            }

            foreach (var (title, key) in _powerUpByTitle)
            {
                var p = live.Find(x => string.Equals(x.Title.Trim(), title, StringComparison.OrdinalIgnoreCase));
                if (p != null) { Report(p, key); continue; }

                missing++;
                var near = live.Find(x => Squash(x.Title) == Squash(title));
                Plugin.Log.LogWarning(near != null
                    ? $"Power-up check: '{title}' -> {key} has no exact match on Twitch. Did you mean '{near.Title}'? Fix one so they match."
                    : $"Power-up check: '{title}' -> {key} doesn't exist on Twitch. Create it, or fix the title in PowerUpMappings.");
            }

            foreach (var (rewardId, key) in _powerUpById)
            {
                var p = live.Find(x => string.Equals(x.Id, rewardId, StringComparison.OrdinalIgnoreCase));
                if (p != null) { Report(p, key); continue; }
                missing++;
                Plugin.Log.LogWarning($"Power-up check: id '{rewardId}' -> {key} doesn't exist on Twitch.");
            }

            int unmapped = 0;
            foreach (var p in live)
            {
                if (used.Contains(p.Id)) continue;
                unmapped++;
                Plugin.Log.LogInfo($"Power-up check: '{p.Title}' ({p.Bits} Bits) is on Twitch but no effect is mapped to it.");
            }

            string summary = $"Power-up check: {matched} matched, {missing} missing on Twitch, {unmapped} not mapped.";
            if (missing > 0) Plugin.Log.LogWarning(summary); else Plugin.Log.LogInfo(summary);
        }

        /// <summary>Lower-case letters and digits only, to spot titles that differ by spacing or punctuation.</summary>
        private static string Squash(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private async Task CreateSubscriptionsAsync(string sessionId, CancellationToken ct)
        {
            string condition = $"{{\"broadcaster_user_id\":\"{_broadcasterId}\"}}";

            if (_config.EnableChannelPoints.Value)
                await SubscribeWithRetryAsync(RedemptionType, "1", condition, sessionId, ct).ConfigureAwait(false);

            if (_config.EnableBits.Value)
                await SubscribeWithRetryAsync(CheerType, "1", condition, sessionId, ct).ConfigureAwait(false);

            if (_config.EnablePowerUps.Value)
            {
                // Last, and non-fatal: a channel without custom Power-ups keeps the others.
                try
                {
                    await SubscribeWithRetryAsync(BitsUseType, "1", condition, sessionId, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Plugin.Log.LogWarning("Couldn't listen for custom Power-ups: " + ex.Message);
                }
            }
        }

        private async Task SubscribeWithRetryAsync(
            string type, string version, string condition, string sessionId, CancellationToken ct)
        {
            string token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            try
            {
                await _helix.CreateWebSocketSubscriptionAsync(token, type, version, condition, sessionId, ct)
                    .ConfigureAwait(false);
            }
            catch (TwitchUnauthorizedException)
            {
                Plugin.Log.LogWarning("Token rejected while subscribing; refreshing and retrying.");
                token = await _auth.ForceRefreshAsync(ct).ConfigureAwait(false);
                await _helix.CreateWebSocketSubscriptionAsync(token, type, version, condition, sessionId, ct)
                    .ConfigureAwait(false);
            }
        }

        private void HandleNotification(string subType, JsonElement evt)
        {
            switch (subType)
            {
                case RedemptionType:
                    HandleRedemption(evt);
                    break;
                case CheerType:
                    HandleCheer(evt);
                    break;
                case BitsUseType:
                    HandleBitsUse(evt);
                    break;
            }
        }

        private void HandleBitsUse(JsonElement evt)
        {
            if (GetString(evt, "type") != "custom_power_up") return;
            if (!evt.TryGetProperty("custom_power_up", out var power) || power.ValueKind != JsonValueKind.Object) return;

            string user = GetString(evt, "user_name");
            string title = GetString(power, "title");
            string rewardId = GetString(power, "reward_id");
            int bits = evt.TryGetProperty("bits", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0;
            string message = evt.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                ? GetString(m, "text") : "";

            if (_config.DebugLogging.Value)
                Plugin.Log.LogDebug($"Custom Power-up: title='{title}' id='{rewardId}' bits={bits} by {user}");

            string? key = null;
            if (!_powerUpById.TryGetValue(rewardId, out key))
                _powerUpByTitle.TryGetValue(title.Trim(), out key);

            if (key == null)
            {
                Plugin.Log.LogInfo($"No mapping for Power-up '{title}'. Add '{title}=>someEffect' to PowerUpMappings.");
                return;
            }

            _effects.Trigger(key, new EffectContext
            {
                Source = EffectSource.PowerUp,
                UserName = user,
                Amount = bits,
                RewardTitle = title,
                Message = message,
            });
        }

        private void HandleRedemption(JsonElement evt)
        {
            string user = GetString(evt, "user_name");
            string input = GetString(evt, "user_input");
            var reward = evt.GetProperty("reward");
            string title = GetString(reward, "title");
            string rewardId = GetString(reward, "id");
            int cost = reward.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32() : 0;

            if (_config.DebugLogging.Value)
                Plugin.Log.LogDebug($"Redemption: title='{title}' id='{rewardId}' by {user}");

            string? key = null;
            if (!_rewardById.TryGetValue(rewardId, out key))
                _rewardByTitle.TryGetValue(title.Trim(), out key);

            if (key == null)
            {
                if (_config.DebugLogging.Value)
                    Plugin.Log.LogDebug($"No mapping for reward '{title}'. Add '{title}=>someEffect' to RewardMappings.");
                return;
            }

            _effects.Trigger(key, new EffectContext
            {
                Source = EffectSource.ChannelPoints,
                UserName = user,
                Amount = cost,
                RewardTitle = title,
                Message = input,
            });
        }

        private void HandleCheer(JsonElement evt)
        {
            bool anon = evt.TryGetProperty("is_anonymous", out var a) && a.ValueKind == JsonValueKind.True;
            string user = anon ? "Anonymous" : GetString(evt, "user_name");
            string message = GetString(evt, "message");
            int bits = evt.TryGetProperty("bits", out var b) && b.ValueKind == JsonValueKind.Number
                ? b.GetInt32() : 0;

            string? key = null;
            foreach (var (threshold, effectKey) in _bitRules) // sorted high -> low
            {
                if (bits >= threshold) { key = effectKey; break; }
            }

            if (key == null) return;

            _effects.Trigger(key, new EffectContext
            {
                Source = EffectSource.Bits,
                UserName = user,
                Amount = bits,
                Message = message,
            });
        }

        private void ParseMappings()
        {
            foreach (var (left, right) in ParsePairs(_config.RewardMappings.Value))
            {
                if (!_effects.Contains(right))
                    Plugin.Log.LogWarning($"RewardMappings references unknown effect '{right}'.");

                if (left.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                    _rewardById[left.Substring(3).Trim()] = right;
                else
                    _rewardByTitle[left] = right;
            }

            foreach (var (left, right) in ParsePairs(_config.BitMappings.Value))
            {
                if (!_effects.Contains(right))
                    Plugin.Log.LogWarning($"BitMappings references unknown effect '{right}'.");
                if (int.TryParse(left, out int threshold))
                    _bitRules.Add((threshold, right));
                else
                    Plugin.Log.LogWarning($"BitMappings threshold '{left}' is not a number; ignored.");
            }

            _bitRules.Sort((x, y) => y.threshold.CompareTo(x.threshold)); // highest first

            foreach (var (left, right) in ParsePairs(_config.PowerUpMappings.Value))
            {
                if (!_effects.Contains(right))
                    Plugin.Log.LogWarning($"PowerUpMappings references unknown effect '{right}'.");

                if (left.StartsWith("id:", StringComparison.OrdinalIgnoreCase))
                    _powerUpById[left.Substring(3).Trim()] = right;
                else
                    _powerUpByTitle[left] = right;
            }
        }

        private static IEnumerable<(string left, string right)> ParsePairs(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) yield break;
            foreach (var part in raw.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int idx = part.IndexOf("=>", StringComparison.Ordinal);
                if (idx < 0)
                {
                    Plugin.Log.LogWarning($"Ignoring malformed mapping (no '=>'): '{part.Trim()}'");
                    continue;
                }
                string left = part.Substring(0, idx).Trim();
                string right = part.Substring(idx + 2).Trim();
                if (left.Length > 0 && right.Length > 0)
                    yield return (left, right);
            }
        }

        private static string GetString(JsonElement element, string property) =>
            element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            _eventSub.Dispose();
            _http.Dispose();
            _cts.Dispose();
        }
    }
}

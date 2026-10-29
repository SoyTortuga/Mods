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

        private string _broadcasterId = "";

        public TwitchService(PluginConfig config, EffectRegistry effects)
        {
            _config = config;
            _effects = effects;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            var scopes = new List<string>();
            if (config.EnableChannelPoints.Value) scopes.Add("channel:read:redemptions");
            if (config.EnableBits.Value) scopes.Add("bits:read");
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

            await _eventSub.RunAsync(ct).ConfigureAwait(false);
        }

        private async Task CreateSubscriptionsAsync(string sessionId, CancellationToken ct)
        {
            string condition = $"{{\"broadcaster_user_id\":\"{_broadcasterId}\"}}";

            if (_config.EnableChannelPoints.Value)
                await SubscribeWithRetryAsync(RedemptionType, "1", condition, sessionId, ct).ConfigureAwait(false);

            if (_config.EnableBits.Value)
                await SubscribeWithRetryAsync(CheerType, "1", condition, sessionId, ct).ConfigureAwait(false);
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
            }
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

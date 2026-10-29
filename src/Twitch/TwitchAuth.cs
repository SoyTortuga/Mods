using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using MoonSweepersTwitch.Util;

namespace MoonSweepersTwitch.Twitch
{
    /// <summary>
    /// Twitch OAuth using the Device Code Grant flow — the right flow for a desktop
    /// app with no backend and no client secret. The streamer opens a URL once and
    /// types a short code; after that the refresh token keeps the session alive.
    ///
    /// Tokens are cached in BepInEx\config\&lt;guid&gt;.tokens.json (git-ignored).
    /// </summary>
    public sealed class TwitchAuth
    {
        private const string DeviceEndpoint = "https://id.twitch.tv/oauth2/device";
        private const string TokenEndpoint = "https://id.twitch.tv/oauth2/token";

        private readonly string _clientId;
        private readonly string[] _scopes;
        private readonly string _tokenPath;
        private readonly HttpClient _http;

        private StoredToken? _token;

        public TwitchAuth(string clientId, string[] scopes, HttpClient http)
        {
            _clientId = clientId;
            _scopes = scopes;
            _http = http;
            _tokenPath = Path.Combine(Paths.ConfigPath, PluginInfo.Guid + ".tokens.json");
            _token = LoadToken();
        }

        public string ClientId => _clientId;

        /// <summary>
        /// Returns a valid access token, refreshing or running the device flow as
        /// needed. Blocks (awaits) while the user authorizes for the first time.
        /// </summary>
        public async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            if (_token != null && !_token.IsExpiringWithin(TimeSpan.FromMinutes(5)))
                return _token.AccessToken;

            if (_token?.RefreshToken is { Length: > 0 })
            {
                var refreshed = await TryRefreshAsync(_token.RefreshToken, ct).ConfigureAwait(false);
                if (refreshed != null)
                {
                    SaveToken(refreshed);
                    return refreshed.AccessToken;
                }
                Plugin.Log.LogWarning("Twitch token refresh failed; starting device authorization again.");
            }

            var fresh = await RunDeviceFlowAsync(ct).ConfigureAwait(false);
            SaveToken(fresh);
            return fresh.AccessToken;
        }

        /// <summary>Force a refresh (used when Helix reports a 401).</summary>
        public async Task<string> ForceRefreshAsync(CancellationToken ct)
        {
            _token = null;
            return await GetAccessTokenAsync(ct).ConfigureAwait(false);
        }

        private async Task<StoredToken> RunDeviceFlowAsync(CancellationToken ct)
        {
            using var request = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("scopes", string.Join(" ", _scopes)),
            });

            using var resp = await _http.PostAsync(DeviceEndpoint, request, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Device code request failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string deviceCode = root.GetProperty("device_code").GetString()!;
            string userCode = root.GetProperty("user_code").GetString()!;
            string verifyUri = root.GetProperty("verification_uri").GetString()!;
            int interval = root.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;
            int expiresIn = root.TryGetProperty("expires_in", out var ev) ? ev.GetInt32() : 1800;

            string prompt = $"TWITCH SETUP: open {verifyUri} and enter code  {userCode}";
            Plugin.Log.LogMessage("================================================================");
            Plugin.Log.LogMessage(prompt);
            Plugin.Log.LogMessage("================================================================");
            MainThreadDispatcher.Enqueue(() => OnScreenMessages.Show(prompt, Math.Min(expiresIn, 120)));

            var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);

                using var pollReq = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", _clientId),
                    new KeyValuePair<string, string>("device_code", deviceCode),
                    new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                });

                using var pollResp = await _http.PostAsync(TokenEndpoint, pollReq, ct).ConfigureAwait(false);
                var pollBody = await pollResp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (pollResp.IsSuccessStatusCode)
                {
                    Plugin.Log.LogMessage("Twitch authorization complete.");
                    MainThreadDispatcher.Enqueue(() => OnScreenMessages.Show("✅ Twitch connected!", 5f));
                    return StoredToken.FromTokenResponse(pollBody);
                }

                // While the user hasn't finished, Twitch returns 400 authorization_pending.
                if (pollBody.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (pollBody.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
                {
                    interval += 2;
                    continue;
                }

                throw new InvalidOperationException($"Device authorization failed: {pollBody}");
            }

            throw new TimeoutException("Twitch device authorization timed out. Reload the mod to try again.");
        }

        private async Task<StoredToken?> TryRefreshAsync(string refreshToken, CancellationToken ct)
        {
            try
            {
                using var req = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", _clientId),
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("refresh_token", refreshToken),
                });

                using var resp = await _http.PostAsync(TokenEndpoint, req, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Plugin.Log.LogDebug($"Refresh failed ({(int)resp.StatusCode}): {body}");
                    return null;
                }
                return StoredToken.FromTokenResponse(body);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Plugin.Log.LogDebug("Refresh exception: " + ex.Message);
                return null;
            }
        }

        private StoredToken? LoadToken()
        {
            try
            {
                if (!File.Exists(_tokenPath)) return null;
                return JsonSerializer.Deserialize<StoredToken>(File.ReadAllText(_tokenPath));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not read cached Twitch token: " + ex.Message);
                return null;
            }
        }

        private void SaveToken(StoredToken token)
        {
            _token = token;
            try
            {
                File.WriteAllText(_tokenPath, JsonSerializer.Serialize(token));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not cache Twitch token: " + ex.Message);
            }
        }
    }
}

using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MoonSweepersTwitch.Twitch
{
    /// <summary>Thrown when Helix rejects the token so the caller can refresh + retry.</summary>
    public sealed class TwitchUnauthorizedException : Exception
    {
        public TwitchUnauthorizedException(string message) : base(message) { }
    }

    /// <summary>Thin wrapper over the few Helix endpoints this mod needs.</summary>
    public sealed class HelixApi
    {
        private const string UsersEndpoint = "https://api.twitch.tv/helix/users";
        private const string SubscriptionsEndpoint = "https://api.twitch.tv/helix/eventsub/subscriptions";
        private const string CustomPowerUpsEndpoint = "https://api.twitch.tv/helix/bits/custom_power_ups";

        /// <summary>One of the broadcaster's custom Power-ups, as Twitch lists it.</summary>
        public sealed class CustomPowerUp
        {
            public string Id { get; init; } = "";
            public string Title { get; init; } = "";
            public int Bits { get; init; }
            public bool IsEnabled { get; init; }
            public bool IsPaused { get; init; }
        }

        private readonly HttpClient _http;
        private readonly string _clientId;

        public HelixApi(HttpClient http, string clientId)
        {
            _http = http;
            _clientId = clientId;
        }

        /// <summary>Resolve the authorized user's numeric id (the broadcaster).</summary>
        public async Task<(string id, string login)> GetCurrentUserAsync(string accessToken, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsersEndpoint);
            AddAuth(req, accessToken);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            ThrowIfUnauthorized(resp, body);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"helix/users failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
                throw new InvalidOperationException("helix/users returned no user for this token.");

            var user = data[0];
            return (user.GetProperty("id").GetString()!, user.GetProperty("login").GetString()!);
        }

        /// <summary>
        /// Create one EventSub subscription bound to a WebSocket session.
        /// Returns true on success (including 409 "already exists").
        /// </summary>
        public async Task<bool> CreateWebSocketSubscriptionAsync(
            string accessToken, string type, string version,
            string conditionJson, string sessionId, CancellationToken ct)
        {
            string payload =
                $"{{\"type\":\"{type}\",\"version\":\"{version}\"," +
                $"\"condition\":{conditionJson}," +
                $"\"transport\":{{\"method\":\"websocket\",\"session_id\":\"{sessionId}\"}}}}";

            using var req = new HttpRequestMessage(HttpMethod.Post, SubscriptionsEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            AddAuth(req, accessToken);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            ThrowIfUnauthorized(resp, body);

            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Conflict)
            {
                Plugin.Log.LogInfo($"Subscribed to '{type}'.");
                return true;
            }

            // 403 is the usual "your token lacks the scope" or "not affiliate" case.
            Plugin.Log.LogError($"Failed to subscribe to '{type}' ({(int)resp.StatusCode}): {body}");
            return false;
        }

        /// <summary>
        /// The broadcaster's custom Power-ups (at most 50, so no paging). Read-only: Twitch
        /// has no endpoint for creating them, so this is for checking the mappings.
        /// </summary>
        public async Task<System.Collections.Generic.List<CustomPowerUp>> GetCustomPowerUpsAsync(
            string accessToken, string broadcasterId, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                CustomPowerUpsEndpoint + "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId));
            AddAuth(req, accessToken);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            ThrowIfUnauthorized(resp, body);
            var list = new System.Collections.Generic.List<CustomPowerUp>();
            if (resp.StatusCode == HttpStatusCode.NotFound) return list; // no Power-ups on the channel
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"helix/bits/custom_power_ups failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            foreach (var p in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                list.Add(new CustomPowerUp
                {
                    Id = p.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Bits = p.TryGetProperty("bits", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : 0,
                    IsEnabled = p.TryGetProperty("is_enabled", out var e) && e.ValueKind == JsonValueKind.True,
                    IsPaused = p.TryGetProperty("is_paused", out var ps) && ps.ValueKind == JsonValueKind.True,
                });
            }
            return list;
        }

        private void AddAuth(HttpRequestMessage req, string accessToken)
        {
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            req.Headers.TryAddWithoutValidation("Client-Id", _clientId);
        }

        private static void ThrowIfUnauthorized(HttpResponseMessage resp, string body)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new TwitchUnauthorizedException(body);
        }
    }
}

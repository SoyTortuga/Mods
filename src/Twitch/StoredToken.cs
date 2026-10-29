using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoonSweepersTwitch.Twitch
{
    /// <summary>Persisted OAuth token with a computed expiry.</summary>
    public sealed class StoredToken
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_at_utc")] public DateTime ExpiresAtUtc { get; set; }

        public bool IsExpiringWithin(TimeSpan window) => DateTime.UtcNow + window >= ExpiresAtUtc;

        /// <summary>Build from a Twitch token endpoint JSON response.</summary>
        public static StoredToken FromTokenResponse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int expiresIn = root.TryGetProperty("expires_in", out var ev) ? ev.GetInt32() : 3600;
            string access = root.GetProperty("access_token").GetString()!;
            string refresh = root.TryGetProperty("refresh_token", out var rv)
                ? rv.GetString() ?? "" : "";

            return new StoredToken
            {
                AccessToken = access,
                RefreshToken = refresh,
                // Shave a minute off so we refresh a touch early rather than mid-request.
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)),
            };
        }
    }
}

using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MoonSweepersTwitch.Twitch
{
    /// <summary>
    /// Twitch EventSub over WebSocket. This is the key choice that makes a desktop
    /// mod possible: unlike webhooks, it needs no public callback URL or server —
    /// the client connects out, receives a session id, and subscribes over Helix.
    ///
    /// Handles the welcome/keepalive/reconnect/revocation lifecycle and auto-reconnects
    /// with backoff. Subscriptions are (re)created via <see cref="OnSessionReady"/>.
    /// </summary>
    public sealed class EventSubClient : IDisposable
    {
        private const string DefaultUrl = "wss://eventsub.wss.twitch.tv/ws";

        /// <summary>Called on a FRESH session with the session id; create subscriptions here.</summary>
        public Func<string, CancellationToken, Task>? OnSessionReady;

        /// <summary>Called for each event notification: (subscriptionType, eventElement).</summary>
        public Action<string, JsonElement>? OnNotification;

        public bool DebugLogging { get; set; }

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public async Task RunAsync(CancellationToken external)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, _cts.Token);
            var ct = linked.Token;

            string url = DefaultUrl;
            bool isReconnectContinuation = false;
            int backoffSeconds = 1;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                    Plugin.Log.LogInfo("EventSub WebSocket connected.");
                    backoffSeconds = 1;

                    // Default; replaced by the value in session_welcome.
                    var keepalive = TimeSpan.FromSeconds(20);

                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        string? message = await ReceiveAsync(ws, keepalive + TimeSpan.FromSeconds(10), ct)
                            .ConfigureAwait(false);

                        if (message == null)
                        {
                            // Timed out waiting for keepalive — Twitch says reconnect.
                            Plugin.Log.LogWarning("EventSub keepalive timeout; reconnecting.");
                            isReconnectContinuation = false;
                            url = DefaultUrl;
                            break;
                        }

                        if (DebugLogging) Plugin.Log.LogDebug("EventSub <- " + message);

                        var action = HandleMessage(message, isReconnectContinuation, ct,
                            out string? reconnectUrl, out TimeSpan? newKeepalive);

                        if (newKeepalive.HasValue) keepalive = newKeepalive.Value;

                        if (action == MessageAction.Reconnect && reconnectUrl != null)
                        {
                            url = reconnectUrl;
                            isReconnectContinuation = true;
                            break; // reconnect to the provided URL without resubscribing
                        }
                        if (action == MessageAction.FreshWelcomeHandled)
                        {
                            // We just (re)subscribed on a fresh connection; future drops
                            // should reconnect to the default URL and resubscribe.
                            isReconnectContinuation = false;
                            url = DefaultUrl;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("EventSub connection error: " + ex.Message);
                    isReconnectContinuation = false;
                    url = DefaultUrl;
                }

                if (ct.IsCancellationRequested) break;

                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct).ConfigureAwait(false);
                backoffSeconds = Math.Min(backoffSeconds * 2, 60);
            }

            Plugin.Log.LogInfo("EventSub loop stopped.");
        }

        private enum MessageAction { None, FreshWelcomeHandled, Reconnect }

        private MessageAction HandleMessage(
            string message, bool isReconnectContinuation, CancellationToken ct,
            out string? reconnectUrl, out TimeSpan? keepalive)
        {
            reconnectUrl = null;
            keepalive = null;

            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("metadata", out var metadata)) return MessageAction.None;

            string type = metadata.GetProperty("message_type").GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var p) ? p : default;

            switch (type)
            {
                case "session_welcome":
                {
                    var session = payload.GetProperty("session");
                    string sessionId = session.GetProperty("id").GetString()!;
                    if (session.TryGetProperty("keepalive_timeout_seconds", out var k) &&
                        k.ValueKind == JsonValueKind.Number)
                        keepalive = TimeSpan.FromSeconds(k.GetInt32());

                    if (isReconnectContinuation)
                    {
                        Plugin.Log.LogInfo("EventSub resumed after reconnect.");
                        return MessageAction.FreshWelcomeHandled;
                    }

                    // Fire-and-await the subscription creation synchronously enough to
                    // report errors, but don't block the read loop forever.
                    try
                    {
                        var subscribeTask = OnSessionReady?.Invoke(sessionId, ct);
                        subscribeTask?.GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError("Creating EventSub subscriptions failed: " + ex.Message);
                    }
                    return MessageAction.FreshWelcomeHandled;
                }

                case "session_keepalive":
                    return MessageAction.None;

                case "session_reconnect":
                {
                    var session = payload.GetProperty("session");
                    reconnectUrl = session.GetProperty("reconnect_url").GetString();
                    Plugin.Log.LogInfo("EventSub asked us to reconnect.");
                    return MessageAction.Reconnect;
                }

                case "notification":
                {
                    string subType = payload.GetProperty("subscription").GetProperty("type").GetString() ?? "";
                    var evt = payload.GetProperty("event");
                    try
                    {
                        OnNotification?.Invoke(subType, evt);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError("Notification handler threw: " + ex);
                    }
                    return MessageAction.None;
                }

                case "revocation":
                {
                    string subType = payload.TryGetProperty("subscription", out var s) &&
                                     s.TryGetProperty("type", out var st)
                        ? st.GetString() ?? "?" : "?";
                    Plugin.Log.LogWarning($"EventSub subscription '{subType}' was revoked by Twitch " +
                                          "(token expired, scope removed, or reward deleted).");
                    return MessageAction.None;
                }

                default:
                    return MessageAction.None;
            }
        }

        private static async Task<string?> ReceiveAsync(ClientWebSocket ws, TimeSpan timeout, CancellationToken ct)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var buffer = new byte[8192];
            var sb = new StringBuilder();
            try
            {
                while (true)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;

                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (result.EndOfMessage) break;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return null; // keepalive timeout
            }

            return sb.ToString();
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}

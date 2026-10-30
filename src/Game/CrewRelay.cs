using System;
using System.Collections.Generic;
using System.Globalization;
using MoonCleaners;
using Steamworks;
using UnityEngine;

namespace MoonSweepersTwitch.Game
{
    /// <summary>
    /// Shares triggered effects with the rest of the crew through Steam lobby member data,
    /// so every player who runs the mod is hit, not just the one whose game heard the cheer.
    ///
    /// Each game publishes the effects it triggered under its own lobby member data
    /// (<c>mstwitch.fx</c>): a short list of entries with an id, the effect key, the Steam
    /// server time it started, its duration and an optional detail (refilldust's sector).
    /// Every game reads the other members' lists about once a second and runs each new
    /// entry locally: a timed effect for the time it has left, an instant one only while it
    /// is under a minute old, so a late joiner catches up on running effects without
    /// replaying old one-offs. World effects (money, robots) are run by the host only.
    /// "endall" also cancels every entry, from any member, that started before it.
    ///
    /// Each game also publishes <c>mstwitch.ver</c>, so the log shows who has the mod.
    /// Outside a lobby nothing is shared, and a player without the mod is never affected.
    /// </summary>
    internal static class CrewRelay
    {
        private const string FxKey = "mstwitch.fx";
        private const string VersionKey = "mstwitch.ver";
        private const string Protocol = "1";
        private const string EndAllKey = "endall";
        private const uint InstantMaxAgeSeconds = 60;
        private const int MaxEntries = 32;
        private const float PollSeconds = 1f;

        private sealed class Entry
        {
            public string Id = "";
            public string Key = "";
            public uint Start;
            public uint Duration;
            public string Arg = "";
        }

        private static EffectRegistry? _registry;
        private static Func<bool>? _enabled;

        private static readonly List<Entry> _mine = new List<Entry>();
        private static readonly HashSet<string> _seen = new HashSet<string>();
        private static readonly Dictionary<ulong, string> _lastRaw = new Dictionary<ulong, string>();
        private static readonly HashSet<ulong> _withMod = new HashSet<ulong>();
        private static ulong _lobby;
        private static uint _endAllAt;
        private static int _counter;
        private static float _nextPoll;
        private static string? _lastError;

        public static void Init(EffectRegistry registry, Func<bool> enabled)
        {
            _registry = registry;
            _enabled = enabled;
        }

        private static bool Enabled => _enabled?.Invoke() ?? false;

        /// <summary>
        /// Whether this game runs world effects itself: always with the relay off or outside
        /// a lobby, otherwise only when hosting (the host's game owns money and robots).
        /// </summary>
        public static bool RunsWorldEffects
        {
            get
            {
                if (!Enabled) return true;
                try { return !MoonSteamLobby.InLobby || MoonSteamLobby.IsHost; }
                catch { return true; }
            }
        }

        /// <summary>Share an effect this game triggered (not one relayed to it).</summary>
        public static void Publish(Effect effect, EffectContext ctx)
        {
            if (!Enabled || !TryLobby(out ulong lobby)) return;
            try
            {
                uint now = SteamUtils.GetServerRealTime();
                ulong self = SteamUser.GetSteamID().m_SteamID;

                if (IsEndAll(effect.Key)) CancelBefore(now, republishFrom: 0);

                var entry = new Entry
                {
                    Id = self.ToString(CultureInfo.InvariantCulture) + ":" + now.ToString(CultureInfo.InvariantCulture) +
                         ":" + (++_counter).ToString(CultureInfo.InvariantCulture),
                    Key = effect.Key,
                    Start = now,
                    Duration = (uint)Mathf.Max(0f, ctx.Seconds ?? effect.Duration),
                    Arg = Clean(ctx.Arg),
                };
                _seen.Add(entry.Id);
                _mine.Add(entry);
                Prune(now);
                Write(lobby);
                Plugin.Log.LogInfo($"[CrewRelay] Shared '{effect.Key}' with the crew.");
            }
            catch (Exception ex)
            {
                LogOnce("share", ex);
            }
        }

        /// <summary>Called every frame; reads the crew about once a second.</summary>
        public static void Tick()
        {
            if (Time.realtimeSinceStartup < _nextPoll) return;
            _nextPoll = Time.realtimeSinceStartup + PollSeconds;
            if (!Enabled || _registry == null) return;

            if (!TryLobby(out ulong lobby))
            {
                if (_lobby != 0) Left();
                return;
            }

            try
            {
                var lobbyId = new CSteamID(lobby);
                ulong self = SteamUser.GetSteamID().m_SteamID;
                uint now = SteamUtils.GetServerRealTime();
                if (lobby != _lobby) Joined(lobby, lobbyId);

                int count = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
                for (int i = 0; i < count; i++)
                {
                    var member = SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i);
                    ulong id = member.m_SteamID;
                    if (id == self || id == 0) continue;

                    NoteVersion(lobbyId, member, id);

                    string raw = SteamMatchmaking.GetLobbyMemberData(lobbyId, member, FxKey) ?? "";
                    if (_lastRaw.TryGetValue(id, out var last) && last == raw) continue;
                    _lastRaw[id] = raw;

                    Apply(member, Parse(raw), now);
                }
            }
            catch (Exception ex)
            {
                LogOnce("read", ex);
            }
        }

        private static void Apply(CSteamID member, List<Entry> entries, uint now)
        {
            // An endall cancels everything from anyone that started before it.
            foreach (var e in entries)
                if (IsEndAll(e.Key) && e.Start > _endAllAt && !_seen.Contains(e.Id))
                    CancelBefore(e.Start, republishFrom: _lobby);

            entries.Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach (var e in entries)
            {
                if (!_seen.Add(e.Id)) continue;
                if (!IsEndAll(e.Key) && e.Start <= _endAllAt) continue; // ended by an endall
                if (!_registry!.TryGet(e.Key, out var effect)) continue;
                if (effect.Scope == EffectScope.World && !RunsWorldEffects) continue; // the host runs it

                float? seconds = null;
                if (e.Duration == 0)
                {
                    if (now > e.Start + InstantMaxAgeSeconds) continue; // an old one-off
                }
                else
                {
                    long left = (long)e.Start + e.Duration - now;
                    if (left <= 0) continue;
                    seconds = left;
                }

                _registry.Trigger(e.Key, new EffectContext
                {
                    Source = EffectSource.Crew,
                    UserName = NameOf(member),
                    Seconds = seconds,
                    Arg = string.IsNullOrEmpty(e.Arg) ? null : e.Arg,
                });
            }
        }

        /// <summary>
        /// Note an endall at <paramref name="at"/>: drop this game's own timed entries that
        /// started before it, so a late joiner doesn't pick them back up.
        /// </summary>
        private static void CancelBefore(uint at, ulong republishFrom)
        {
            _endAllAt = Math.Max(_endAllAt, at);
            int removed = _mine.RemoveAll(e => e.Duration > 0 && e.Start <= at);
            if (removed > 0 && republishFrom != 0) Write(republishFrom);
        }

        private static void Joined(ulong lobby, CSteamID lobbyId)
        {
            _lobby = lobby;
            _lastRaw.Clear();
            _withMod.Clear();
            _mine.Clear();
            SteamMatchmaking.SetLobbyMemberData(lobbyId, VersionKey, Protocol + "/" + PluginInfo.Version);
            SteamMatchmaking.SetLobbyMemberData(lobbyId, FxKey, "");
            Plugin.Log.LogInfo(
                $"[CrewRelay] In a lobby ({(MoonSteamLobby.IsHost ? "hosting" : "joined")}); sharing effects with crew members who have the mod.");
        }

        private static void Left()
        {
            _lobby = 0;
            _lastRaw.Clear();
            _withMod.Clear();
            _mine.Clear();
            Plugin.Log.LogInfo("[CrewRelay] Left the lobby.");
        }

        private static void NoteVersion(CSteamID lobbyId, CSteamID member, ulong id)
        {
            if (_withMod.Contains(id)) return;
            string version = SteamMatchmaking.GetLobbyMemberData(lobbyId, member, VersionKey) ?? "";
            if (version.Length == 0) return;

            _withMod.Add(id);
            if (!version.StartsWith(Protocol + "/", StringComparison.Ordinal))
                Plugin.Log.LogWarning($"[CrewRelay] {NameOf(member)} runs an incompatible mod version ({version}); their effects are ignored.");
            else
                Plugin.Log.LogInfo($"[CrewRelay] {NameOf(member)} has the mod ({version}). Crew with the mod: {_withMod.Count}.");
        }

        private static void Prune(uint now)
        {
            _mine.RemoveAll(e => (long)e.Start + Math.Max(e.Duration, InstantMaxAgeSeconds) < now);
            if (_mine.Count > MaxEntries) _mine.RemoveRange(0, _mine.Count - MaxEntries);
        }

        private static void Write(ulong lobby)
        {
            var parts = new List<string>(_mine.Count);
            foreach (var e in _mine)
                parts.Add(string.Join(",", e.Id, e.Key,
                    e.Start.ToString(CultureInfo.InvariantCulture), e.Duration.ToString(CultureInfo.InvariantCulture), e.Arg));
            SteamMatchmaking.SetLobbyMemberData(new CSteamID(lobby), FxKey, Protocol + "|" + string.Join(";", parts));
        }

        private static List<Entry> Parse(string raw)
        {
            var list = new List<Entry>();
            if (!raw.StartsWith(Protocol + "|", StringComparison.Ordinal)) return list;
            foreach (var item in raw.Substring(Protocol.Length + 1).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var f = item.Split(',');
                if (f.Length < 5 ||
                    !uint.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint start) ||
                    !uint.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint duration)) continue;
                list.Add(new Entry { Id = f[0], Key = f[1], Start = start, Duration = duration, Arg = f[4] });
            }
            return list;
        }

        private static bool TryLobby(out ulong lobby)
        {
            lobby = 0;
            try
            {
                if (!MoonSteamLobby.InLobby) return false;
                lobby = MoonSteamLobby.LobbyId;
                return lobby != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsEndAll(string key) => string.Equals(key, EndAllKey, StringComparison.OrdinalIgnoreCase);

        private static string Clean(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s!.Replace(",", "").Replace(";", "").Replace("|", "");
        }

        private static string NameOf(CSteamID member)
        {
            try
            {
                string name = SteamFriends.GetFriendPersonaName(member);
                return string.IsNullOrEmpty(name) ? member.m_SteamID.ToString(CultureInfo.InvariantCulture) : name;
            }
            catch
            {
                return member.m_SteamID.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static void LogOnce(string what, Exception ex)
        {
            string key = what + ":" + ex.Message;
            if (key == _lastError) return;
            _lastError = key;
            Plugin.Log.LogWarning($"[CrewRelay] Couldn't {what} effects (repeats suppressed): {ex.Message}");
        }
    }
}

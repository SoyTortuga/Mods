using BepInEx.Configuration;
using MoonSweepersTwitch.Game;
using UnityEngine;

namespace MoonSweepersTwitch.Config
{
    /// <summary>
    /// Typed wrapper over the BepInEx config file. Written to
    /// <c>BepInEx\config\com.soytortuga.moonsweepers.twitch.cfg</c> on first run.
    /// </summary>
    public sealed class PluginConfig
    {
        /// <summary>The mod's own public Twitch app, so most streamers needn't register one.</summary>
        public const string DefaultClientId = "14cjcbeu8dxso3ac838q3fw0t4me8g";

        public readonly ConfigEntry<bool> TwitchEnabled;
        public readonly ConfigEntry<string> ClientId;
        public readonly ConfigEntry<bool> EnableChannelPoints;
        public readonly ConfigEntry<bool> EnableBits;

        public readonly ConfigEntry<string> RewardMappings;
        public readonly ConfigEntry<string> BitMappings;

        public readonly ConfigEntry<bool> ShowOnScreenFeedback;
        public readonly ConfigEntry<bool> DebugLogging;
        public readonly ConfigEntry<KeyCode> TestEffectKey;
        public readonly ConfigEntry<string> TestEffectKeyName;
        public readonly ConfigEntry<KeyCode> TestEffectKey2;
        public readonly ConfigEntry<string> TestEffectKey2Name;
        public readonly ConfigEntry<string> TestHotkeys;
        public readonly ConfigEntry<bool> CrewRelayEnabled;

        public PluginConfig(ConfigFile config)
        {
            TwitchEnabled = config.Bind(
                "Twitch", "Enabled", true,
                "Connect to Twitch. Streamers leave this on. Crew members who only want to be hit by " +
                "the streamer's effects set it to false: those reach them through the game lobby.");

            ClientId = config.Bind(
                "Twitch", "ClientId", DefaultClientId,
                "Twitch application Client ID. The default is the mod's shared public app, so most " +
                "streamers can leave it as-is. To use your own, create a free app with Client Type " +
                "'Public' at https://dev.twitch.tv/console/apps (OAuth Redirect URL http://localhost) " +
                "and paste its Client ID here. The account you authorize must be the streamer's.");

            EnableChannelPoints = config.Bind(
                "Twitch", "EnableChannelPoints", true,
                "Listen for Channel Point reward redemptions (needs the " +
                "channel:read:redemptions scope — requested automatically). " +
                "Note: Affiliate/Partner status is required to create custom rewards.");

            EnableBits = config.Bind(
                "Twitch", "EnableBits", true,
                "Listen for Bits/cheers (needs the bits:read scope — requested automatically).");

            RewardMappings = config.Bind(
                "ChannelPoints", "RewardMappings", "",
                "Map Channel Point rewards to effects. Pairs are separated by '||' and " +
                "each pair is 'RewardName=>effectKey'. Match is by reward title " +
                "(case-insensitive). To match by reward id instead, use 'id:<reward-id>=>effectKey'. " +
                "Run with DebugLogging to see redemption titles/ids in the log. " +
                "Available built-in effect keys: " + Effects.EffectKeyList + ".");

            BitMappings = config.Bind(
                "Bits", "BitMappings", "",
                "Map cheer amounts to effects. Pairs separated by '||', each 'threshold=>effectKey' " +
                "(e.g. 100=>tunnelvision || 500=>emptydust). Empty means cheers trigger nothing. " +
                "When someone cheers N bits, the effect for the HIGHEST threshold <= N fires. " +
                "Available built-in effect keys: " + Effects.EffectKeyList + ".");

            ShowOnScreenFeedback = config.Bind(
                "General", "ShowOnScreenFeedback", true,
                "Show a small on-screen toast when an effect triggers.");

            DebugLogging = config.Bind(
                "General", "DebugLogging", false,
                "Verbose logging to the BepInEx console (raw EventSub messages, reward ids, etc.).");

            TestEffectKey = config.Bind(
                "General", "TestEffectKey", KeyCode.None,
                "A key that fires TestEffectKeyEffect in-game, to try an effect without Twitch " +
                "(e.g. F8). None disables it; leave it off while streaming.");

            TestEffectKeyName = config.Bind(
                "General", "TestEffectKeyEffect", "",
                "Effect key the test hotkey fires (e.g. tunnelvision).");

            TestEffectKey2 = config.Bind(
                "General", "TestEffectKey2", KeyCode.None,
                "A second test hotkey, so two effects can be tried in one session. None disables it.");

            TestEffectKey2Name = config.Bind(
                "General", "TestEffectKey2Effect", "",
                "Effect key the second test hotkey fires.");

            TestHotkeys = config.Bind(
                "General", "TestHotkeys", "",
                "More test hotkeys, for trying many effects in one session. Pairs separated by '||', " +
                "each 'Key=effectKey', using Unity KeyCode names (e.g. F1=joonasoff || F2=bankrupt || Insert=endall). " +
                "Leave empty to disable. Available effect keys: " + Effects.EffectKeyList + ".");

            CrewRelayEnabled = config.Bind(
                "Crew", "Enabled", true,
                "Share triggered effects with everyone in your lobby who also runs this mod, and " +
                "receive theirs. Players without the mod are never affected.");
        }
    }
}

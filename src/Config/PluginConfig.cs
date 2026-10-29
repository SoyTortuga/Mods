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

        public PluginConfig(ConfigFile config)
        {
            ClientId = config.Bind(
                "Twitch", "ClientId", "",
                "Your Twitch application Client ID. Create a FREE app at " +
                "https://dev.twitch.tv/console/apps (OAuth Redirect URL can be " +
                "http://localhost — Device Code Flow does not actually use it). " +
                "The account you authorize must be the broadcaster/streamer.");

            EnableChannelPoints = config.Bind(
                "Twitch", "EnableChannelPoints", true,
                "Listen for Channel Point reward redemptions (needs the " +
                "channel:read:redemptions scope — requested automatically). " +
                "Note: Affiliate/Partner status is required to create custom rewards.");

            EnableBits = config.Bind(
                "Twitch", "EnableBits", true,
                "Listen for Bits/cheers (needs the bits:read scope — requested automatically).");

            RewardMappings = config.Bind(
                "ChannelPoints", "RewardMappings",
                "Make it Foggy=>fog || Dump Dust=>dropdust || Shake the Moon=>earthquake",
                "Map Channel Point rewards to effects. Pairs are separated by '||' and " +
                "each pair is 'RewardName=>effectKey'. Match is by reward title " +
                "(case-insensitive). To match by reward id instead, use 'id:<reward-id>=>effectKey'. " +
                "Run with DebugLogging to see redemption titles/ids in the log. " +
                "Available built-in effect keys: " + Effects.EffectKeyList + ".");

            BitMappings = config.Bind(
                "Bits", "BitMappings",
                "1=>confetti || 100=>fog || 1000=>earthquake",
                "Map cheer amounts to effects. Pairs separated by '||', each 'threshold=>effectKey'. " +
                "When someone cheers N bits, the effect for the HIGHEST threshold <= N fires. " +
                "Available built-in effect keys: " + Effects.EffectKeyList + ".");

            ShowOnScreenFeedback = config.Bind(
                "General", "ShowOnScreenFeedback", true,
                "Show a small on-screen toast when an effect triggers.");

            DebugLogging = config.Bind(
                "General", "DebugLogging", false,
                "Verbose logging to the BepInEx console (raw EventSub messages, reward ids, etc.).");

            TestEffectKey = config.Bind(
                "General", "TestEffectKey", KeyCode.F8,
                "Press this key in-game to fire a test effect (verifies the mod works without Twitch). " +
                "Set to None to disable.");

            // Which effect the test key fires. Defaults to the first configured bit effect.
            TestEffectKeyName = config.Bind(
                "General", "TestEffectKeyEffect", "confetti",
                "Effect key the test hotkey fires.");

            TestEffectKey2 = config.Bind(
                "General", "TestEffectKey2", KeyCode.F9,
                "A second test hotkey, so two effects can be tried in one session. " +
                "Set to None to disable.");

            TestEffectKey2Name = config.Bind(
                "General", "TestEffectKey2Effect", "refilldust",
                "Effect key the second test hotkey fires.");
        }
    }
}

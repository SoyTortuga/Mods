using MoonSweepersTwitch.Config;
using MoonSweepersTwitch.Util;

namespace MoonSweepersTwitch.Game
{
    /// <summary>
    /// The built-in effect catalogue. Each effect shows feedback and then calls into
    /// <see cref="GameBridge"/>, which is the single place that touches the actual
    /// game. Add a new effect by registering it here and implementing the matching
    /// <see cref="GameBridge"/> method.
    /// </summary>
    public static class Effects
    {
        /// <summary>Keys usable in RewardMappings / BitMappings, for config docs.</summary>
        public const string EffectKeyList =
            "confetti, fog, dropdust, earthquake, speedboost, slowmo, payout";

        public static void RegisterBuiltIns(EffectRegistry registry, PluginConfig config)
        {
            void Feedback(EffectContext ctx, string label)
            {
                if (config.ShowOnScreenFeedback.Value)
                    OnScreenMessages.Show($"{label}\n({ctx.Describe()})", 5f);
            }

            registry.Register(new Effect("confetti", "Confetti burst", ctx =>
            {
                Feedback(ctx, "🎉 Confetti!");
                GameBridge.SpawnConfetti(ctx);
            }));

            registry.Register(new Effect("fog", "Dust fog", ctx =>
            {
                Feedback(ctx, "🌫 Fog rolls in");
                GameBridge.SetFog(ctx, enabled: true, seconds: 20f);
            }));

            registry.Register(new Effect("dropdust", "Dump a pile of dust", ctx =>
            {
                Feedback(ctx, "🪣 A fresh pile of moon dust appears");
                GameBridge.DropDust(ctx, amount: 50);
            }));

            registry.Register(new Effect("earthquake", "Moonquake", ctx =>
            {
                Feedback(ctx, "🌋 Moonquake!");
                GameBridge.ShakeWorld(ctx, intensity: 1f, seconds: 6f);
            }));

            registry.Register(new Effect("speedboost", "Speed boost", ctx =>
            {
                Feedback(ctx, "⚡ Speed boost");
                GameBridge.SetPlayerSpeedMultiplier(ctx, multiplier: 1.6f, seconds: 30f);
            }));

            registry.Register(new Effect("slowmo", "Slow motion", ctx =>
            {
                Feedback(ctx, "🐌 Slow motion");
                GameBridge.SetPlayerSpeedMultiplier(ctx, multiplier: 0.5f, seconds: 20f);
            }));

            registry.Register(new Effect("payout", "Cash tip", ctx =>
            {
                Feedback(ctx, "💰 A tip from the crowd");
                GameBridge.GiveMoney(ctx, amount: 100);
            }));
        }
    }
}

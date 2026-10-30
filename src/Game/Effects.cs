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
            "breakdown, toothbrush, emptydust, refilldust, joonasoff, roombaoff, bankrupt, " +
            "gravity2x, gravity4x, gravity8x, lightsout, tunnelvision, fisheye, potato, paycut, " +
            "tinypockets, butterfingers, reversecontrols, australianmoon, moonstorm, endall, " +
            "confetti, fog, dropdust, earthquake, speedboost, slowmo, payout";

        public static void RegisterBuiltIns(EffectRegistry registry, PluginConfig config)
        {
            void Feedback(EffectContext ctx, string label)
            {
                if (config.ShowOnScreenFeedback.Value)
                    OnScreenMessages.Show($"{label}\n({ctx.Describe()})", 5f);
            }

            // --- Chat sabotage effects (host-local, implemented) ---

            registry.Register(new Effect("breakdown", "Vehicle breakdown", ctx =>
            {
                Feedback(ctx, "🚗💨 Vehicles broke down!");
                GameBridge.BreakVehicles(ctx, seconds: 900f); // 15 minutes
            }));

            registry.Register(new Effect("toothbrush", "Toothbrush only", ctx =>
            {
                Feedback(ctx, "🪥 Toothbrush only!");
                GameBridge.ToothbrushOnly(ctx, seconds: 300f); // 5 minutes
            }));

            registry.Register(new Effect("emptydust", "Drop all dust", ctx =>
            {
                Feedback(ctx, "🫙 Your dust spills out!");
                GameBridge.DropDustCanisters(ctx); // drops 1 canister per kg held
            }));

            registry.Register(new Effect("refilldust", "Re-dust a sector", ctx =>
            {
                Feedback(ctx, "🌑 A sector gets dusty again!");
                GameBridge.RefillRandomSector(ctx);
            }));

            // --- Robots and money (instant; shared world state) ---

            registry.Register(new Effect("joonasoff", "J.O.O.N.A.S. breakdown", ctx =>
            {
                Feedback(ctx, "🤖 J.O.O.N.A.S. switched off!");
                GameBridge.RobotsOff(ctx, "J.O.O.N.A.S. breakdown", MoonCleaners.MoonWorld.MapRobotKind.Joonas);
            }));

            registry.Register(new Effect("roombaoff", "Roomba breakdown", ctx =>
            {
                Feedback(ctx, "🧹 The robot vacuums went home!");
                GameBridge.RobotsOff(ctx, "Roomba breakdown",
                    MoonCleaners.MoonWorld.MapRobotKind.MkI, MoonCleaners.MoonWorld.MapRobotKind.MkII);
            }));

            registry.Register(new Effect("bankrupt", "Bankrupt", ctx =>
            {
                Feedback(ctx, "💸 Bankrupt!");
                GameBridge.Bankrupt(ctx);
            }));

            // --- Timed effects (host-local: they change the streamer's own game) ---

            registry.Register(new Effect("gravity2x", "2x gravity", ctx =>
            {
                Feedback(ctx, "🪐 2x gravity!");
                TimedEffects.Gravity.StartLevel(2, seconds: 900f); // 15 minutes
            }));

            registry.Register(new Effect("gravity4x", "4x gravity", ctx =>
            {
                Feedback(ctx, "🪐 4x gravity!");
                TimedEffects.Gravity.StartLevel(4, seconds: 900f);
            }));

            registry.Register(new Effect("gravity8x", "8x gravity", ctx =>
            {
                Feedback(ctx, "🪐 8x gravity!");
                TimedEffects.Gravity.StartLevel(8, seconds: 900f);
            }));

            registry.Register(new Effect("lightsout", "Lights out", ctx =>
            {
                Feedback(ctx, "🔦 Lights out!");
                TimedEffects.LightsOut.Start(seconds: 900f); // 15 minutes
            }));

            registry.Register(new Effect("tunnelvision", "Tunnel vision", ctx =>
            {
                Feedback(ctx, "🔭 Tunnel vision!");
                TimedEffects.Fov.StartMode("Tunnel vision", fov: 30f, seconds: 900f);
            }));

            registry.Register(new Effect("fisheye", "Fisheye", ctx =>
            {
                Feedback(ctx, "🐟 Fisheye!");
                TimedEffects.Fov.StartMode("Fisheye", fov: 140f, seconds: 900f);
            }));

            registry.Register(new Effect("potato", "Potato mode", ctx =>
            {
                Feedback(ctx, "🥔 Potato mode!");
                TimedEffects.Potato.Start(seconds: 900f); // 15 minutes
            }));

            registry.Register(new Effect("paycut", "Paycut", ctx =>
            {
                Feedback(ctx, "📉 Paycut: half pay!");
                TimedEffects.Paycut.Start(seconds: 1800f); // 30 minutes
            }));

            registry.Register(new Effect("tinypockets", "Tiny pockets", ctx =>
            {
                Feedback(ctx, "👖 Tiny pockets!");
                TimedEffects.TinyPockets.Start(seconds: 900f); // 15 minutes
            }));

            registry.Register(new Effect("butterfingers", "Butterfingers", ctx =>
            {
                Feedback(ctx, "🧈 Butterfingers!");
                TimedEffects.Butterfingers.Start(seconds: 600f); // 10 minutes
            }));

            registry.Register(new Effect("reversecontrols", "Reverse controls", ctx =>
            {
                Feedback(ctx, "🔄 Controls reversed!");
                TimedEffects.Reverse.Start(seconds: 600f); // 10 minutes
            }));

            registry.Register(new Effect("australianmoon", "Australian moon", ctx =>
            {
                Feedback(ctx, "🙃 Australian moon!");
                TimedEffects.UpsideDown.Start(seconds: 600f); // 10 minutes
            }));

            registry.Register(new Effect("moonstorm", "Moon storm", ctx =>
            {
                Feedback(ctx, "🌪 Moon storm!");
                TimedEffects.Storm.Start(seconds: 600f); // 10 minutes
            }));

            registry.Register(new Effect("endall", "End all timed effects", ctx =>
            {
                Feedback(ctx, "✅ All effects ended.");
                GameBridge.EndAllTimed(ctx);
            }));

            // --- Original template effects (still stubbed in GameBridge) ---

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

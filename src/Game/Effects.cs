using MoonSweepersTwitch.Config;
using MoonSweepersTwitch.Util;
using static MoonSweepersTwitch.Game.EffectScope;

namespace MoonSweepersTwitch.Game
{
    /// <summary>
    /// The built-in effect catalogue. Each effect declares where it runs (each player, or
    /// the host's shared world) and its full duration (0 for instant), then calls into
    /// <see cref="GameBridge"/> or <see cref="TimedEffects"/>. A timed effect runs for the
    /// seconds it is given: the full duration when triggered here, or the time the
    /// original has left when relayed from another player (<see cref="CrewRelay"/>).
    /// </summary>
    public static class Effects
    {
        /// <summary>Keys usable in RewardMappings / BitMappings, for config docs.</summary>
        public const string EffectKeyList =
            "breakdown, toothbrush, emptydust, refilldust, joonasoff, roombaoff, bankrupt, " +
            "gravity2x, gravity4x, gravity8x, lightsout, tunnelvision, fisheye, potato, paycut, " +
            "tinypockets, butterfingers, reversecontrols, australianmoon, moonstorm, endall";

        private const float Min5 = 300f, Min10 = 600f, Min15 = 900f, Min30 = 1800f;

        public static void RegisterBuiltIns(EffectRegistry registry, PluginConfig config)
        {
            void Feedback(EffectContext ctx, string label)
            {
                if (config.ShowOnScreenFeedback.Value)
                    OnScreenMessages.Show($"{label}\n({ctx.Describe()})", 5f);
            }

            void Add(string key, string name, EffectScope scope, float duration, string feedback,
                System.Action<EffectContext, float> run)
            {
                registry.Register(new Effect(key, name, scope, duration, (ctx, seconds) =>
                {
                    Feedback(ctx, feedback);
                    run(ctx, seconds);
                }));
            }

            // --- The player's own vehicles, tools and dust ---

            Add("breakdown", "Vehicle breakdown", Player, Min15, "🚗💨 Vehicles broke down!",
                (ctx, s) => GameBridge.BreakVehicles(ctx, s));
            Add("toothbrush", "Toothbrush only", Player, Min5, "🪥 Toothbrush only!",
                (ctx, s) => GameBridge.ToothbrushOnly(ctx, s));
            Add("emptydust", "Drop all dust", Player, 0f, "🫙 Your dust spills out!",
                (ctx, s) => GameBridge.DropDustCanisters(ctx)); // 1 canister per kg held

            // Every player re-dusts the same sector: the first game picks it and shares it.
            Add("refilldust", "Re-dust a sector", Player, 0f, "🌑 A sector gets dusty again!",
                (ctx, s) => GameBridge.RefillRandomSector(ctx));

            // --- Shared world state the host owns ---

            Add("joonasoff", "J.O.O.N.A.S. breakdown", World, 0f, "🤖 J.O.O.N.A.S. switched off!",
                (ctx, s) => GameBridge.RobotsOff(ctx, "J.O.O.N.A.S. breakdown", MoonCleaners.MoonWorld.MapRobotKind.Joonas));
            Add("roombaoff", "Roomba breakdown", World, 0f, "🧹 The robot vacuums went home!",
                (ctx, s) => GameBridge.RobotsOff(ctx, "Roomba breakdown",
                    MoonCleaners.MoonWorld.MapRobotKind.MkI, MoonCleaners.MoonWorld.MapRobotKind.MkII));
            Add("bankrupt", "Bankrupt", World, 0f, "💸 Bankrupt!",
                (ctx, s) => GameBridge.Bankrupt(ctx));

            // --- Timed effects on each player's own game ---

            Add("gravity2x", "2x gravity", Player, Min15, "🪐 2x gravity!", (ctx, s) => TimedEffects.Gravity.StartLevel(2, s));
            Add("gravity4x", "4x gravity", Player, Min15, "🪐 4x gravity!", (ctx, s) => TimedEffects.Gravity.StartLevel(4, s));
            Add("gravity8x", "8x gravity", Player, Min15, "🪐 8x gravity!", (ctx, s) => TimedEffects.Gravity.StartLevel(8, s));
            Add("lightsout", "Lights out", Player, Min15, "🔦 Lights out!", (ctx, s) => TimedEffects.LightsOut.Start(s));
            Add("tunnelvision", "Tunnel vision", Player, Min15, "🔭 Tunnel vision!",
                (ctx, s) => TimedEffects.Fov.StartMode("Tunnel vision", fov: 30f, seconds: s));
            Add("fisheye", "Fisheye", Player, Min15, "🐟 Fisheye!",
                (ctx, s) => TimedEffects.Fov.StartMode("Fisheye", fov: 140f, seconds: s));
            Add("potato", "Potato mode", Player, Min15, "🥔 Potato mode!", (ctx, s) => TimedEffects.Potato.Start(s));
            Add("paycut", "Paycut", Player, Min30, "📉 Paycut: half pay!", (ctx, s) => TimedEffects.Paycut.Start(s));
            Add("tinypockets", "Tiny pockets", Player, Min15, "👖 Tiny pockets!", (ctx, s) => TimedEffects.TinyPockets.Start(s));
            Add("butterfingers", "Butterfingers", Player, Min10, "🧈 Butterfingers!", (ctx, s) => TimedEffects.Butterfingers.Start(s));
            Add("reversecontrols", "Reverse controls", Player, Min10, "🔄 Controls reversed!", (ctx, s) => TimedEffects.Reverse.Start(s));
            Add("australianmoon", "Australian moon", Player, Min10, "🙃 Australian moon!", (ctx, s) => TimedEffects.UpsideDown.Start(s));
            Add("moonstorm", "Moon storm", Player, Min10, "🌪 Moon storm!", (ctx, s) => TimedEffects.Storm.Start(s));

            Add("endall", "End all timed effects", Player, 0f, "✅ All effects ended.",
                (ctx, s) => GameBridge.EndAllTimed(ctx));
        }
    }
}

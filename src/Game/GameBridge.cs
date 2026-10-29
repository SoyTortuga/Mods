using System;
using MoonCleaners;
using MoonSweepersTwitch.Util;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MoonSweepersTwitch.Game
{
    /// <summary>
    /// ============================================================================
    ///  Where effects actually change the game.
    /// ============================================================================
    /// Every method here runs on the Unity MAIN THREAD (effects are dispatched there
    /// by <see cref="EffectRegistry"/>, and <see cref="Tick"/> is called from
    /// <see cref="ModBehaviour"/>.Update), so it is safe to touch Unity/game objects.
    ///
    /// The game's own types live in the MoonCleaners namespace (decompiled from
    /// BepInEx\interop\Assembly-CSharp.dll). The two implemented effects below are
    /// host-local and drive the local player's own components, so they don't fight
    /// the co-op host or require any network replication:
    ///   - BreakVehicles:   ejects the local player from any vehicle for a while.
    ///   - ToothbrushOnly:  forces the local player's toolbelt to the brush for a while.
    ///
    /// The remaining methods (confetti/fog/dust/shake/speed/money) are still stubs —
    /// they only log until wired up, mirroring the original template.
    /// ============================================================================
    /// </summary>
    public static class GameBridge
    {
        // ---- timed-restriction state. All times are Time.realtimeSinceStartup. ----
        private static float _vehiclesBrokenUntil;
        private static float _toothbrushUntil;

        // Cached local-player components. Unity's overloaded == treats a destroyed
        // object as null, so a scene change transparently forces a re-acquire.
        private static MoonVehiclePassenger? _passenger;
        private static MoonToolbelt? _toolbelt;
        private static int _brushSlot = -1;

        // Latches so the "effect ended" feedback fires exactly once.
        private static bool _vehiclesWasBroken;
        private static bool _toothbrushWasActive;

        /// <summary>True while the vehicle-breakdown window is active.</summary>
        public static bool VehiclesBroken => Time.realtimeSinceStartup < _vehiclesBrokenUntil;

        /// <summary>True while the toothbrush-only window is active.</summary>
        public static bool ToothbrushActive => Time.realtimeSinceStartup < _toothbrushUntil;

        // =====================================================================
        //  Implemented effects
        // =====================================================================

        /// <summary>
        /// Disable the local player's vehicles for <paramref name="seconds"/>: they get
        /// ejected now (if driving) and can't stay in a vehicle until it wears off.
        /// </summary>
        public static void BreakVehicles(EffectContext ctx, float seconds)
        {
            _vehiclesBrokenUntil = Time.realtimeSinceStartup + seconds;
            Plugin.Log.LogInfo(
                $"[GameBridge] Vehicles disabled for {seconds:0}s ({ctx.Describe()}).");
            TryEjectFromVehicle(); // immediate kick if they're already driving
        }

        /// <summary>
        /// Restrict the local player to the toothbrush for <paramref name="seconds"/>:
        /// any other tool (including a prototype) is swapped back to the brush each frame.
        /// </summary>
        public static void ToothbrushOnly(EffectContext ctx, float seconds)
        {
            _toothbrushUntil = Time.realtimeSinceStartup + seconds;
            _brushSlot = -1; // re-resolve against the current toolbelt
            Plugin.Log.LogInfo(
                $"[GameBridge] Toothbrush-only for {seconds:0}s ({ctx.Describe()}).");
        }

        /// <summary>
        /// Force the local player to drop all the moon dust they're holding: each 1 kg
        /// is packed into a canister and dropped on the ground via the game's own
        /// mechanic (so it creates real, networked <c>MoonDroppedCanister</c>s). Holding
        /// 5 kg drops 5 canisters they have to go re-collect. One-shot (not timed).
        /// </summary>
        public static void DropDustCanisters(EffectContext ctx)
        {
            var inv = Object.FindObjectOfType<MoonDustInventory>();
            if (inv == null)
            {
                Plugin.Log.LogInfo($"[GameBridge] DropDustCanisters: no dust inventory (not in-game?). ({ctx.Describe()})");
                return;
            }

            var world = inv.World;
            var walker = inv.walker;
            string player = inv.playerId;
            if (world == null || walker == null || string.IsNullOrEmpty(player))
            {
                Plugin.Log.LogWarning("[GameBridge] DropDustCanisters: world/walker/playerId unavailable.");
                return;
            }

            var t = walker.transform;
            Vector3 point = t.position;
            Vector3 up = t.up;

            int dropped = 0;
            int guard = 0;
            double last = inv.Kilograms;
            // Pack one canister (<=1 kg) per call until the player's dust is gone.
            // The decrease check guards against a pack call that can't make progress.
            while (inv.Kilograms > 0.0001 && guard++ < 200)
            {
                if (!world.PackDustCanister(player, point, up, out int _))
                    break;
                dropped++;

                double now = inv.Kilograms;
                if (now >= last - 1e-6) break; // no progress -> stop (avoid runaway)
                last = now;
            }

            Plugin.Log.LogInfo($"[GameBridge] Dropped {dropped} dust canister(s) ({ctx.Describe()}).");
        }

        /// <summary>
        /// Called every frame on the main thread. Enforces whichever timed effects are
        /// active and announces when each one ends. Never throws into the game loop.
        /// </summary>
        public static void Tick()
        {
            try
            {
                bool broken = VehiclesBroken;
                if (broken)
                {
                    TryEjectFromVehicle();
                }
                else if (_vehiclesWasBroken)
                {
                    OnScreenMessages.Show("🔧 Vehicles repaired.", 4f);
                }
                _vehiclesWasBroken = broken;

                bool brushOnly = ToothbrushActive;
                if (brushOnly)
                {
                    EnforceToothbrush();
                }
                else if (_toothbrushWasActive)
                {
                    OnScreenMessages.Show("🧰 Tools unlocked.", 4f);
                }
                _toothbrushWasActive = brushOnly;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[GameBridge] Tick error: " + ex);
            }
        }

        private static void TryEjectFromVehicle()
        {
            var p = Passenger();
            if (p != null && p.Seated)
                p.GetOut();
        }

        private static void EnforceToothbrush()
        {
            var tb = Toolbelt();
            if (tb == null) return;

            if (_brushSlot < 0)
                _brushSlot = ResolveBrushSlot(tb);
            if (_brushSlot < 0) return;

            // A "prototype" tool is equipped as an overlay on top of the slot; drop it.
            if (tb.IsPrototypeEquipped)
                tb.ClearPrototype();

            if (tb.SelectedIndex != _brushSlot)
                tb.SelectSlot(_brushSlot);
        }

        /// <summary>
        /// Find the toolbelt slot holding the toothbrush. Matches the tool's id/name on
        /// "toothbrush", then any "brush", and finally falls back to slot 0 (the game's
        /// starting tool is the toothbrush). Logs what it picked so config/debug is easy.
        /// </summary>
        private static int ResolveBrushSlot(MoonToolbelt tb)
        {
            var tools = tb.tools;
            if (tools == null || tools.Length == 0)
            {
                Plugin.Log.LogWarning("[GameBridge] Toolbelt has no tools; defaulting to slot 0.");
                return 0;
            }

            int brushFallback = -1;
            for (int i = 0; i < tools.Length; i++)
            {
                var def = tools[i];
                if (def == null) continue;

                string id = ((def.toolId ?? "") + "|" + (def.displayName ?? "")).ToLowerInvariant();
                if (id.Contains("toothbrush"))
                {
                    Plugin.Log.LogInfo($"[GameBridge] Toothbrush = slot {i} ('{def.displayName}' / '{def.toolId}').");
                    return i;
                }
                if (brushFallback < 0 && id.Contains("brush"))
                    brushFallback = i;
            }

            if (brushFallback >= 0)
            {
                Plugin.Log.LogInfo($"[GameBridge] Brush = slot {brushFallback} (no exact 'toothbrush' match).");
                return brushFallback;
            }

            Plugin.Log.LogWarning("[GameBridge] No toothbrush/brush tool found; defaulting to slot 0.");
            return 0;
        }

        private static MoonVehiclePassenger? Passenger()
        {
            // MoonVehiclePassenger exists only on the local player, so FindObjectOfType
            // returns the host's own passenger (remote crew use separate components).
            if (_passenger == null)
                _passenger = Object.FindObjectOfType<MoonVehiclePassenger>();
            return _passenger;
        }

        private static MoonToolbelt? Toolbelt()
        {
            if (_toolbelt == null)
            {
                _toolbelt = Object.FindObjectOfType<MoonToolbelt>();
                _brushSlot = -1; // new toolbelt -> re-resolve the brush slot
            }
            return _toolbelt;
        }

        // =====================================================================
        //  Still-stubbed effects (log only). Wire these up the same way as above.
        // =====================================================================

        private static void NotImplemented(string what, EffectContext ctx)
        {
            Plugin.Log.LogInfo(
                $"[GameBridge] '{what}' fired ({ctx.Describe()}) — on-screen feedback only. " +
                $"Wire this up in GameBridge.cs to affect the game.");
        }

        public static void SpawnConfetti(EffectContext ctx)
        {
            NotImplemented(nameof(SpawnConfetti), ctx);
            // Note: MoonCleaners.MoonConfetti.Burst(Vector3 at, Vector3 up, int count) exists.
        }

        public static void SetFog(EffectContext ctx, bool enabled, float seconds)
        {
            NotImplemented(nameof(SetFog), ctx);
            // Unity built-in fog is usually safe without game code:
            //   RenderSettings.fog = enabled; RenderSettings.fogDensity = 0.04f;
        }

        public static void DropDust(EffectContext ctx, int amount)
        {
            NotImplemented(nameof(DropDust), ctx);
        }

        public static void ShakeWorld(EffectContext ctx, float intensity, float seconds)
        {
            NotImplemented(nameof(ShakeWorld), ctx);
            // Note: the local player's camera is MoonCleaners.MoonWalker.viewCamera.
        }

        public static void SetPlayerSpeedMultiplier(EffectContext ctx, float multiplier, float seconds)
        {
            NotImplemented(nameof(SetPlayerSpeedMultiplier), ctx);
            // Note: MoonWalker has DevSpeedMultiplier / EquipmentSpeedMultiplier.
        }

        public static void GiveMoney(EffectContext ctx, int amount)
        {
            NotImplemented(nameof(GiveMoney), ctx);
            // Note: MoonWorld.DevAddMoney / CrewBuildMoney touch shared (team) state.
        }
    }
}

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
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

        private static readonly System.Random _rng = new System.Random();

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
            var inv = FindFirst<MoonDustInventory>();
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
        /// Refill a random already-cleared hex sector with dust.
        ///
        /// The game has no additive dust API, so this writes the dust data directly:
        /// it finds the chunk under the sector's centre (<see cref="MoonWorld.ChunkAt"/>),
        /// refills that chunk's coarse per-cell "remaining" buffer to full, re-uploads
        /// the texture/mesh/collision through the game's own cleaning-refresh methods
        /// (which are NOT stripped — the game uses them while you clean), and resets the
        /// region's aggregate remaining so progress + UI follow.
        ///
        /// Note: an earlier attempt looked like it did nothing, but that was the stripped
        /// <c>FindObjectOfType</c> throwing before any of this ran. Now that the MoonWorld
        /// lookup goes through <see cref="FindFirst{T}"/>, the before/after dust reading is
        /// logged so we can see whether the write actually takes. Caveats: it re-dusts a
        /// whole chunk (roughly a sector), it's host-local (not networked to crew), and it
        /// may not survive a save/reload.
        /// </summary>
        public static void RefillRandomSector(EffectContext ctx)
        {
            var world = FindFirst<MoonWorld>();
            if (world == null)
            {
                Plugin.Log.LogInfo($"[GameBridge] RefillSector: no MoonWorld (not in-game?). ({ctx.Describe()})");
                return;
            }

            var regions = world.Regions;
            int count = (regions != null) ? regions.Count : 0;
            if (regions == null || count <= 0)
            {
                Plugin.Log.LogWarning("[GameBridge] RefillSector: no regions available.");
                return;
            }

            // A sector is "cleared" when its remaining dust is ~0.
            var cleared = new List<int>();
            var remaining = world.regionRemaining;
            if (remaining != null && remaining.Length >= count)
            {
                for (int i = 0; i < count; i++)
                    if (remaining[i] <= 0.01f) cleared.Add(i);
            }
            else
            {
                for (int i = 0; i < count; i++)
                    if (world.RemainingAt(regions.Centers[i].normalized) <= 0.01f) cleared.Add(i);
            }

            if (cleared.Count == 0)
            {
                Plugin.Log.LogInfo("[GameBridge] RefillSector: no cleared sectors to refill.");
                OnScreenMessages.Show("✨ Nothing's clean enough to re-dust!", 5f);
                return;
            }

            int id = cleared[_rng.Next(cleared.Count)];
            Vector3 dir = regions.Centers[id].normalized;
            float before = world.RemainingAt(dir);

            var chunk = world.ChunkAt(dir, out Vector2 _local);
            int cells = 0;
            if (chunk != null)
            {
                var coarse = chunk.coarseRemaining;
                if (coarse != null)
                {
                    for (int k = 0; k < coarse.Length; k++) coarse[k] = byte.MaxValue; // full dust
                    cells = coarse.Length;
                }
                world.UploadCleaning(chunk);
                world.RefreshCleanedMesh(chunk, true, new Rect(0f, 0f, 1f, 1f));
                world.QueueCleaningCollision(chunk);
                world.UpdateCleaningCollisions();
            }

            // Make the region read as un-cleaned again.
            if (remaining != null && remaining.Length > id)
            {
                float init = world.RegionInitialDust(id);
                remaining[id] = (init > 0f) ? init : 1f;
            }
            world.MarkRegionTouched(id);
            world.QueueRegionCheck(id);

            float after = world.RemainingAt(dir);
            Plugin.Log.LogInfo(
                $"[GameBridge] RefillSector region {id}: chunk={(chunk != null)}, coarseCells={cells}, " +
                $"remaining {before:0.###} -> {after:0.###}. ({ctx.Describe()})");

            if (chunk != null && cells > 0 && after > before + 0.001f)
                OnScreenMessages.Show($"🌑 Sector {id} re-dusted!", 5f);
            else if (chunk != null && cells > 0)
                OnScreenMessages.Show($"⚠ Sector {id}: wrote dust data, but it didn't stick.", 6f);
            else
                OnScreenMessages.Show($"⚠ Sector {id}: couldn't reach the dust data.", 6f);
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
                _passenger = FindFirst<MoonVehiclePassenger>();
            return _passenger;
        }

        private static MoonToolbelt? Toolbelt()
        {
            if (_toolbelt == null)
            {
                _toolbelt = FindFirst<MoonToolbelt>();
                _brushSlot = -1; // new toolbelt -> re-resolve the brush slot
            }
            return _toolbelt;
        }

        // =====================================================================
        //  Object lookup
        // =====================================================================

        /// <summary>
        /// Find the first live instance of a game component.
        ///
        /// This IL2CPP build STRIPS <c>UnityEngine.Object.FindObjectOfType&lt;T&gt;()</c>
        /// (it routes to <c>FindObjectOfType(Type, bool)</c>, which throws
        /// "Method unstripping failed" — the shipped game never calls it, so il2cpp
        /// dropped it). So we try the lookup APIs the game DOES keep, in order of how
        /// likely Unity 6 retains them, and use whichever one works. Each attempt is
        /// guarded because calling a stripped method throws.
        /// </summary>
        private static T? FindFirst<T>() where T : Object
        {
            var type = Il2CppType.Of<T>();

            // Unity 6's current API — the game uses it, so it is almost certainly kept.
            if (TryArr<T>(() => Object.FindObjectsByType<T>(FindObjectsSortMode.None),
                       "Object.FindObjectsByType", out var a)) return a;

            // Older generic scene/all-objects searches.
            if (TryArr<T>(() => Object.FindObjectsOfType<T>(),
                       "Object.FindObjectsOfType", out var b)) return b;
            if (TryArr<T>(() => Resources.FindObjectsOfTypeAll<T>(),
                       "Resources.FindObjectsOfTypeAll", out var c)) return c;

            // Non-generic Type-based forms as a last resort.
            if (TryArrObj<T>(() => Object.FindObjectsOfType(type),
                          "Object.FindObjectsOfType(Type)", out var d)) return d;
            if (TryArrObj<T>(() => Resources.FindObjectsOfTypeAll(type),
                          "Resources.FindObjectsOfTypeAll(Type)", out var e)) return e;

            Plugin.Log.LogWarning($"[GameBridge] FindFirst<{typeof(T).Name}>: no working lookup API.");
            return null;
        }

        private static bool TryArr<T>(Func<Il2CppArrayBase<T>> get, string label, out T? first)
            where T : Object
        {
            first = null;
            try
            {
                var arr = get();
                if (arr != null && arr.Length > 0) first = arr[0];
                return first != null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[GameBridge] {label}<{typeof(T).Name}> unavailable: {ex.Message}");
                return false;
            }
        }

        private static bool TryArrObj<T>(Func<Il2CppArrayBase<Object>> get, string label, out T? first)
            where T : Object
        {
            first = null;
            try
            {
                var arr = get();
                if (arr != null && arr.Length > 0) first = arr[0]?.TryCast<T>();
                return first != null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[GameBridge] {label}<{typeof(T).Name}> unavailable: {ex.Message}");
                return false;
            }
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

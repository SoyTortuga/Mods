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
        /// Live-mask re-dust probe: paint dust back into a small circle around the player.
        ///
        /// Each loaded chunk holds its authoritative cleaning in <c>chunk.cleaning</c>
        /// (a <see cref="MoonCleanMask"/>). Removing pages outright worked but left the
        /// game's mesh rebuild pointing at missing pages (NullReferenceException in
        /// RefreshCleanedMesh), and rebuilding collision after that dropped the player
        /// through the ground. This version uses <c>MoonCleanMask.Paint</c>, the painter
        /// cleaning itself goes through, to write the "untouched" value back, so the
        /// pages stay intact and the game's own change tracking sees the edit.
        ///
        /// Safety: the "untouched" byte is derived from the mask's own blank-page checksum
        /// and the probe aborts if that doesn't resolve cleanly or if the player is already
        /// on untouched ground. Only a small circle (radius 0.03 of the chunk) is painted,
        /// and the refresh chain stops at the first failing step, so collision is never
        /// rebuilt from a half-built mesh.
        /// </summary>
        public static void RefillRandomSector(EffectContext ctx)
        {
            var inv = FindFirst<MoonDustInventory>();
            var world = inv?.World ?? FindFirst<MoonWorld>();
            if (world == null)
            {
                Plugin.Log.LogInfo($"[GameBridge] Refill: no MoonWorld (not in-game?). ({ctx.Describe()})");
                return;
            }

            var walker = inv?.walker;
            if (walker == null)
            {
                Plugin.Log.LogWarning("[GameBridge] Refill: no local walker; can't locate the player's chunk.");
                return;
            }

            Vector3 dir = walker.transform.position.normalized;
            var chunk = world.ChunkAt(dir, out Vector2 _local);
            if (chunk == null)
            {
                Plugin.Log.LogWarning("[GameBridge] Refill: ChunkAt returned null under the player.");
                return;
            }

            int chunkId = world.ChunkId(chunk);
            var mask = chunk.cleaning;
            if (mask == null)
            {
                Plugin.Log.LogWarning($"[GameBridge] Refill: chunk {chunkId} has no cleaning mask (not loaded?).");
                return;
            }

            // 1) Read the mask's constants and work out which byte means "untouched".
            int res = MoonCleanMask.Resolution;
            int pageSize = MoonCleanMask.PageSize;
            int pageQuads = MoonCleanMask.PageQuads;
            ulong blankField = MoonCleanMask.BlankSum;
            ulong blankSum = blankField;
            try { blankSum = MoonCleanMask.BlankPageSum(); }
            catch (Exception ex) { Plugin.Log.LogWarning("[GameBridge] Refill: BlankPageSum threw: " + ex.Message); }

            int cx = Mathf.Clamp((int)(_local.x * res), 0, res - 1);
            int cy = Mathf.Clamp((int)(_local.y * res), 0, res - 1);
            int rawHere = -1;
            try { rawHere = mask.RawGrid(cx, cy); }
            catch (Exception ex) { Plugin.Log.LogWarning("[GameBridge] Refill: RawGrid threw: " + ex.Message); }

            float worldBefore = world.RemainingAt(dir);
            float maskBefore = SafeSample(mask, _local);
            Plugin.Log.LogInfo(
                $"[GameBridge] Refill: chunk {chunkId} local=({_local.x:0.####},{_local.y:0.####}) " +
                $"res={res} pageSize={pageSize} pageQuads={pageQuads} blankSum={blankSum} (field {blankField}) " +
                $"pages={mask.PageCount} raw@({cx},{cy})={rawHere} sample={maskBefore:0.###} remaining={worldBefore:0.###}");

            if (!TryBlankValue(blankSum, pageSize, pageQuads, out byte blank))
            {
                Plugin.Log.LogWarning("[GameBridge] Refill: couldn't derive the untouched byte value from blankSum; aborting before any write.");
                return;
            }
            if (rawHere == blank)
            {
                Plugin.Log.LogInfo($"[GameBridge] Refill: the ground here is already untouched (raw={rawHere}); stand on cleaned ground. No write.");
                return;
            }

            // 2) Paint a small circle around the player back to the untouched value.
            const float radius = 0.03f;
            Vector2 centre = _local;
            float x0 = Mathf.Max(0f, centre.x - radius), y0 = Mathf.Max(0f, centre.y - radius);
            float x1 = Mathf.Min(1f, centre.x + radius), y1 = Mathf.Min(1f, centre.y + radius);
            var bounds = new Rect(x0, y0, x1 - x0, y1 - y0);

            Func<Vector2, byte, byte> fill = (uv, cur) =>
                (uv - centre).sqrMagnitude <= radius * radius ? blank : cur;

            int painted;
            try
            {
                painted = mask.Paint(bounds, DelegateSupport.ConvertDelegate<Il2CppSystem.Func<Vector2, byte, byte>>(fill), null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameBridge] Refill: Paint threw: {ex.Message}. Nothing refreshed.");
                return;
            }

            int rawPainted = -1;
            try { rawPainted = mask.RawGrid(cx, cy); } catch { }
            Plugin.Log.LogInfo(
                $"[GameBridge] Refill: Paint(blank={blank}) changed {painted} texel(s); raw@centre {rawHere} -> {rawPainted}.");
            if (painted <= 0)
            {
                Plugin.Log.LogWarning("[GameBridge] Refill: Paint changed nothing (it may only allow cleaning). Nothing refreshed.");
                return;
            }

            // 3) Refresh, stopping at the first failure so collision is never rebuilt from a
            //    half-built mesh.
            bool ok = Step("mask.Upload", () => mask.Upload())
                && Step("UploadCleaning", () => world.UploadCleaning(chunk))
                && Step("RefreshCleanedMesh", () => world.RefreshCleanedMesh(chunk, true, bounds))
                && Step("QueueCleaningCollision", () => world.QueueCleaningCollision(chunk))
                && Step("UpdateCleaningCollisions", () => world.UpdateCleaningCollisions());

            float worldAfter = world.RemainingAt(dir);
            float maskAfter = SafeSample(mask, _local);
            Plugin.Log.LogInfo(
                $"[GameBridge] Refill: chunk {chunkId}, refresh {(ok ? "completed" : "STOPPED at failure")}, " +
                $"RemainingAt {worldBefore:0.###} -> {worldAfter:0.###}, " +
                $"mask.Sample {maskBefore:0.###} -> {maskAfter:0.###}. ({ctx.Describe()})");
        }

        /// <summary>
        /// Work out the per-texel byte of an untouched page from the blank-page checksum.
        /// A zero sum means untouched texels are 0; otherwise the sum must divide evenly by
        /// one of the plausible texel counts per page into a value that fits a byte.
        /// </summary>
        private static bool TryBlankValue(ulong blankSum, int pageSize, int pageQuads, out byte blank)
        {
            blank = 0;
            if (blankSum == 0) return true;

            long[] counts =
            {
                (long)pageSize * pageSize, (long)(pageSize + 1) * (pageSize + 1),
                (long)pageQuads * pageQuads, (long)(pageQuads + 1) * (pageQuads + 1),
            };
            foreach (long n in counts)
            {
                if (n <= 0) continue;
                ulong un = (ulong)n;
                if (blankSum % un == 0 && blankSum / un <= 255)
                {
                    blank = (byte)(blankSum / un);
                    return true;
                }
            }
            return false;
        }

        private static float SafeSample(MoonCleanMask mask, Vector2 uv)
        {
            try { return mask.Sample(uv.x, uv.y); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[GameBridge] Refill: mask.Sample threw: " + ex.Message);
                return float.NaN;
            }
        }

        private static bool Step(string label, Action action)
        {
            try
            {
                action();
                Plugin.Log.LogInfo($"[GameBridge] Refill: {label} ok.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameBridge] Refill: {label} threw: {ex.Message}");
                return false;
            }
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

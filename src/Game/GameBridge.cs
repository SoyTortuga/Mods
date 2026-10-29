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

        /// <summary>The game's per-sector arrays, read once per effect.</summary>
        private sealed class RegionData
        {
            public MoonWorld World = null!;
            public Il2CppArrayBase<Vector3> Centers = null!;
            public Il2CppArrayBase<byte> States = null!;
            public Il2CppArrayBase<float> Remaining = null!;
            public Il2CppArrayBase<float> Initial = null!;
            public Il2CppArrayBase<bool> Touched = null!;
            public Il2CppArrayBase<Vector4>? Gpu;
            public int Count;
        }

        private static RegionData? ReadRegions(MoonWorld world)
        {
            var regions = world.Regions;
            var d = new RegionData
            {
                World = world,
                Centers = regions?.Centers!,
                States = world.regionStates,
                Remaining = world.regionRemaining,
                Initial = world.regionInitial,
                Touched = world.regionTouched,
                Gpu = world.regionGpu,
            };
            if (regions == null || d.Centers == null || d.States == null || d.Remaining == null ||
                d.Initial == null || d.Touched == null)
                return null;

            d.Count = Math.Min(regions.Count, Math.Min(d.Centers.Length, Math.Min(d.States.Length,
                Math.Min(d.Remaining.Length, Math.Min(d.Initial.Length, d.Touched.Length)))));
            return d.Count > 0 ? d : null;
        }

        /// <summary>
        /// Re-dust a random sector the player has fully cleared, anywhere on the moon.
        ///
        /// A cleared sector is held as region state, not in the cleaning mask: when a
        /// sector completes, its cleaned pages are pruned from the chunk masks and the clean
        /// look comes from <c>regionStates</c> alone (1 = cleared by the player). Flipping it
        /// back to 0 with full dust makes the game treat it as dusty again; this was
        /// confirmed to render dusty after a save/reload. A sector that isn't loaded right
        /// now rebuilds from this data when it next loads. One that's loaded near the player
        /// may not redraw live yet, because the chunk mesh refresh still fails when called
        /// directly. Host-local.
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

            var d = ReadRegions(world);
            if (d == null)
            {
                Plugin.Log.LogWarning("[GameBridge] Refill: region data unavailable.");
                return;
            }

            var cleared = new List<int>();
            for (int i = 0; i < d.Count; i++)
                if (d.States[i] == 1) cleared.Add(i);
            if (cleared.Count == 0)
            {
                Plugin.Log.LogInfo($"[GameBridge] Refill: no cleared sectors to re-dust. ({ctx.Describe()})");
                return;
            }

            int id = cleared[_rng.Next(cleared.Count)];
            string where = "";
            var walker = inv?.walker;
            if (walker != null)
            {
                float dot = Vector3.Dot(d.Centers[id].normalized, walker.transform.position.normalized);
                float degrees = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f)) * Mathf.Rad2Deg;
                where = $" {degrees:0.#}° around the moon from you";
            }

            if (UncompleteRegion(d, id))
            {
                Plugin.Log.LogInfo(
                    $"[GameBridge] Refill: re-dusted cleared sector {id}{where} " +
                    $"({cleared.Count} were cleared). ({ctx.Describe()})");
            }
        }

        /// <summary>
        /// Same as <see cref="RefillRandomSector"/>, but for the cleared sector the player
        /// stands in, followed by a live chunk refresh. Not wired to an effect: kept for
        /// working out the live redraw. The refresh stops at the first failing step and
        /// never rebuilds collision.
        /// </summary>
        private static void RefillSectorUnderPlayer(EffectContext ctx)
        {
            var inv = FindFirst<MoonDustInventory>();
            var world = inv?.World ?? FindFirst<MoonWorld>();
            var walker = inv?.walker;
            var d = world != null ? ReadRegions(world) : null;
            if (world == null || walker == null || d == null)
            {
                Plugin.Log.LogInfo($"[GameBridge] Refill(here): world/walker/regions unavailable. ({ctx.Describe()})");
                return;
            }

            Vector3 dir = walker.transform.position.normalized;
            int id = NearestRegion(d, dir, _ => true);
            if (d.States[id] != 1)
            {
                Plugin.Log.LogInfo(
                    $"[GameBridge] Refill(here): region {id} under you has state {d.States[id]}, not 1. No write.");
                return;
            }
            if (!UncompleteRegion(d, id)) return;

            var chunk = world.ChunkAt(dir, out Vector2 _local);
            var mask = chunk?.cleaning;
            bool ok = true;
            if (mask != null) ok = Step("mask.Upload", () => mask.Upload());
            if (ok && chunk != null) ok = Step("UploadCleaning", () => world.UploadCleaning(chunk));
            if (ok && chunk != null) ok = Step("RefreshCleanedMesh", () => world.RefreshCleanedMesh(chunk, true, new Rect(0f, 0f, 1f, 1f)));
            Plugin.Log.LogInfo(
                $"[GameBridge] Refill(here): region {id}, RemainingAt={world.RemainingAt(dir):0.###}, " +
                $"refresh {(ok ? "completed" : "STOPPED at failure")}. ({ctx.Describe()})");
        }

        private static int NearestRegion(RegionData d, Vector3 dir, Func<int, bool> accept)
        {
            int best = -1;
            float bestDot = -2f;
            for (int i = 0; i < d.Count; i++)
            {
                if (!accept(i)) continue;
                float dot = Vector3.Dot(d.Centers[i].normalized, dir);
                if (dot > bestDot) { bestDot = dot; best = i; }
            }
            return best;
        }

        /// <summary>
        /// Flip one cleared sector (state 1) back to normal with full dust. Returns false and
        /// writes nothing if the sector isn't state 1 or no full-dust amount can be found.
        /// </summary>
        private static bool UncompleteRegion(RegionData d, int id)
        {
            var world = d.World;
            if (id < 0 || id >= d.Count || d.States[id] != 1)
            {
                Plugin.Log.LogInfo($"[GameBridge] Uncomplete: region {id} isn't a cleared sector. No write.");
                return false;
            }

            // Reference: the nearest untouched normal sector, for its GPU data and dust amount.
            Vector3 centre = d.Centers[id].normalized;
            int refId = NearestRegion(d, centre, i => d.States[i] == 0 && !d.Touched[i]);

            float full = TryFloat(() => world.RegionInitialDust(id));
            if (full <= 0f && refId >= 0) full = TryFloat(() => world.RegionInitialDust(refId));
            if (full <= 0f)
            {
                for (int i = 0; i < d.Count && full <= 0f; i++)
                    if (d.Initial[i] > 0f) full = d.Initial[i];
            }
            if (full <= 0f)
            {
                Plugin.Log.LogWarning("[GameBridge] Uncomplete: couldn't find a full-dust amount. No write.");
                return false;
            }

            d.States[id] = 0;
            d.Remaining[id] = full;
            d.Initial[id] = full;
            d.Touched[id] = false;
            var gpu = d.Gpu;
            if (gpu != null && refId >= 0 && refId < gpu.Length && id < gpu.Length) gpu[id] = gpu[refId];
            try { world.CompletedRegionCount = Math.Max(0, world.CompletedRegionCount - 1); }
            catch (Exception ex) { Plugin.Log.LogWarning("[GameBridge] Uncomplete: setting CompletedRegionCount threw: " + ex.Message); }

            bool ok = Step("UpdateProgress", () => world.UpdateProgress());
            Plugin.Log.LogInfo(
                $"[GameBridge] Uncomplete: region {id} -> state={d.States[id]} remaining={d.Remaining[id]:0.###} " +
                $"complete={SafeBool(() => world.IsRegionComplete(id))} " +
                $"CompletedRegionCount={world.CompletedRegionCount} (ref {refId}, UpdateProgress {(ok ? "ok" : "failed")}).");
            return true;
        }

        private static float TryFloat(Func<float> f)
        {
            try { return f(); } catch { return -1f; }
        }

        /// <summary>
        /// Read-only sector-state probe (writes nothing; kept for diagnostics).
        ///
        /// The paint probe showed that a fully cleared sector isn't held in the cleaning
        /// mask at all: the mask under the player read untouched (255) while the world
        /// reported 0 dust remaining. Completed sectors are tracked per region instead
        /// (<c>regionStates</c>, <c>regionRemaining</c>, <c>regionInitial</c>,
        /// <c>regionTouched</c>, <c>CompletedRegionCount</c>) and drawn from that state.
        /// Before flipping a sector back to "in progress", this logs the sectors nearest
        /// the player and a tally of every state value, cross-checked against
        /// <c>IsRegionComplete</c> and <c>CompletedRegionCount</c>, so we learn which byte
        /// means "complete".
        /// </summary>
        private static void ProbeSectorStates(EffectContext ctx)
        {
            var inv = FindFirst<MoonDustInventory>();
            var world = inv?.World ?? FindFirst<MoonWorld>();
            var walker = inv?.walker;
            if (world == null || walker == null)
            {
                Plugin.Log.LogInfo($"[GameBridge] SectorProbe: no world/walker (not in-game?). ({ctx.Describe()})");
                return;
            }

            var regions = world.Regions;
            int count = regions != null ? regions.Count : 0;
            var states = world.regionStates;
            var remaining = world.regionRemaining;
            var initial = world.regionInitial;
            var touched = world.regionTouched;
            Plugin.Log.LogInfo(
                $"[GameBridge] SectorProbe: regions={count} states={states?.Length ?? -1} " +
                $"remaining={remaining?.Length ?? -1} initial={initial?.Length ?? -1} touched={touched?.Length ?? -1} " +
                $"CompletedRegionCount={world.CompletedRegionCount} TouchedRegionCount={world.TouchedRegionCount}");
            if (count <= 0 || states == null) return;

            // The three sector centres closest to the player (the player may be near a border).
            Vector3 dir = walker.transform.position.normalized;
            var best = new List<(float dot, int id)>();
            for (int i = 0; i < count; i++)
            {
                float d = Vector3.Dot(regions.Centers[i].normalized, dir);
                best.Add((d, i));
            }
            best.Sort((a, b) => b.dot.CompareTo(a.dot));
            for (int k = 0; k < Math.Min(3, best.Count); k++)
            {
                int id = best[k].id;
                Plugin.Log.LogInfo(
                    $"[GameBridge] SectorProbe: #{k + 1} region {id} dot={best[k].dot:0.######} " +
                    $"state={At(states, id)} remaining={At(remaining, id)} initial={At(initial, id)} " +
                    $"touched={At(touched, id)} IsRegionComplete={SafeBool(() => world.IsRegionComplete(id))} " +
                    $"RegionRemainingDust={SafeFloat(() => world.RegionRemainingDust(id))}");
            }

            // Tally every state value and how many of each the game calls complete.
            var total = new int[256];
            var complete = new int[256];
            int n = Math.Min(count, states.Length);
            for (int i = 0; i < n; i++)
            {
                byte s = states[i];
                total[s]++;
                if (SafeBool(() => world.IsRegionComplete(i)) == "True") complete[s]++;
            }
            var tally = new System.Text.StringBuilder();
            for (int s = 0; s < 256; s++)
                if (total[s] > 0) tally.Append($"state {s}: {total[s]} ({complete[s]} complete); ");
            Plugin.Log.LogInfo($"[GameBridge] SectorProbe: {tally} ({ctx.Describe()})");
        }

        private static string At<T>(Il2CppArrayBase<T>? arr, int i) =>
            arr != null && i >= 0 && i < arr.Length ? arr[i]!.ToString()! : "n/a";

        private static string SafeBool(Func<bool> f)
        {
            try { return f().ToString(); } catch (Exception ex) { return "threw: " + ex.Message; }
        }

        private static string SafeFloat(Func<float> f)
        {
            try { return f().ToString("0.###"); } catch (Exception ex) { return "threw: " + ex.Message; }
        }

        /// <summary>
        /// Paint dust back into a small circle around the player (not wired to an effect).
        /// Kept for partially cleaned, not-yet-complete sectors, where the cleaning still
        /// lives in the mask.
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
        private static void PaintDustAroundPlayer(EffectContext ctx)
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
        /// Work out the per-texel byte of an untouched page. <c>BlankSum</c> is a hash, not
        /// an arithmetic sum, so the value can't be divided out of it. Instead, build a
        /// page filled with a candidate value, hash it with the game's own
        /// <c>MoonCleanMask.Sum</c>, and accept the candidate only if it reproduces
        /// <c>BlankSum</c> exactly. Candidates are 0 and 255 at each plausible page size.
        /// </summary>
        private static bool TryBlankValue(ulong blankSum, int pageSize, int pageQuads, out byte blank)
        {
            blank = 0;
            int[] sides = { pageSize, pageQuads, pageSize + 1, pageQuads + 1 };
            byte[] values = { 0, 255 };
            foreach (int side in sides)
            {
                if (side <= 0) continue;
                int n = side * side;
                foreach (byte v in values)
                {
                    var managed = new byte[n];
                    if (v != 0) for (int i = 0; i < n; i++) managed[i] = v;

                    ulong hash;
                    try { hash = MoonCleanMask.Sum(new Il2CppStructArray<byte>(managed)); }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"[GameBridge] Refill: Sum({side}x{side} of {v}) threw: {ex.Message}");
                        continue;
                    }

                    Plugin.Log.LogInfo($"[GameBridge] Refill: Sum({side}x{side} of {v}) = {hash}{(hash == blankSum ? "  <-- matches BlankSum" : "")}");
                    if (hash == blankSum)
                    {
                        blank = v;
                        return true;
                    }
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

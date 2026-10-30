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
    /// BepInEx\interop\Assembly-CSharp.dll). Effects here either drive the local
    /// player's own components (vehicle breakdown, toothbrush, dropping dust) or change
    /// shared world state the host controls (robots, money, re-dusting sectors). Timed
    /// effects that change a value for a while live in <see cref="TimedEffects"/>.
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

        // Tick runs every frame while an effect is active, so a missing component is
        // retried at most every couple of seconds and a repeating error is logged once.
        private static float _passengerRetryAt;
        private static float _toolbeltRetryAt;
        private static string? _lastTickError;

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

            // Re-triggering while active only extends the timer; the original tool stays
            // remembered from the first swap.
            if (_swapOriginal == null)
                TrySwapInToothbrush();
        }

        // =====================================================================
        //  Toothbrush loadout swap
        // =====================================================================
        // The brush slot (toolbelt slot 0) holds whichever brush the player equipped,
        // e.g. the "Extra wide broom", so locking the slot isn't enough. While the effect
        // runs, the player's loadout puts the toothbrush in that slot, then restores the
        // original. The game autosaves, so a restore note is written to disk before the
        // swap and deleted after the restore; if a session ends mid-effect, the next one
        // finds the note and restores the tool.

        private const string ToothbrushId = "toothbrush";
        private static string? _swapPlayer;
        private static int _swapSlot = -1;
        private static string? _swapOriginal;
        private static bool _pendingRestoreChecked;
        private static float _pendingRestoreRetryAt;

        private static string RestoreNotePath => System.IO.Path.Combine(
            BepInEx.Paths.ConfigPath, "com.soytortuga.moonsweepers.twitch.toothbrush-restore.txt");

        private static void TrySwapInToothbrush()
        {
            try
            {
                var inv = FindFirst<MoonDustInventory>();
                var world = inv?.World;
                string? player = inv?.playerId;
                var tb = Toolbelt();
                if (world == null || string.IsNullOrEmpty(player) || tb == null)
                {
                    Plugin.Log.LogWarning("[GameBridge] Toothbrush swap: world/player/toolbelt unavailable; only locking the slot.");
                    return;
                }

                int slot = ResolveBrushSlot(tb);
                _brushSlot = slot;
                var snap = world.PlayerLoadout(player);
                var slots = snap?.slots;
                if (snap == null || slots == null || slot < 0 || slot >= slots.Length)
                {
                    Plugin.Log.LogWarning($"[GameBridge] Toothbrush swap: no loadout slot {slot}; only locking the slot.");
                    return;
                }

                string current = slots[slot];
                if (current == ToothbrushId)
                {
                    Plugin.Log.LogInfo($"[GameBridge] Toothbrush swap: slot {slot} already holds the toothbrush.");
                    return;
                }
                if (!OwnsTool(snap, tb, slot, ToothbrushId))
                {
                    Plugin.Log.LogWarning("[GameBridge] Toothbrush swap: player doesn't own a toothbrush; leaving the loadout alone.");
                    return;
                }

                // Write the note first, so a crash right after the swap can still be undone.
                WriteRestoreNote(player!, slot, current);
                slots[slot] = ToothbrushId;
                world.CommitLoadout(player);

                string now = world.LoadoutToolId(player, slot);
                if (now == ToothbrushId)
                {
                    _swapPlayer = player;
                    _swapSlot = slot;
                    _swapOriginal = current;
                    Plugin.Log.LogInfo($"[GameBridge] Toothbrush swap: slot {slot} '{current}' -> '{now}'.");
                }
                else
                {
                    // PlayerLoadout may have handed back a copy; undo the edit to it and drop the note.
                    slots[slot] = current;
                    DeleteRestoreNote();
                    Plugin.Log.LogWarning(
                        $"[GameBridge] Toothbrush swap didn't take: slot {slot} still '{now}'. Only locking the slot.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[GameBridge] Toothbrush swap failed: " + ex.Message);
            }
        }

        private static void EndToothbrushSwap()
        {
            if (_swapOriginal == null || _swapPlayer == null) return;
            RestoreLoadout(_swapPlayer, _swapSlot, _swapOriginal, "effect ended");
            _swapPlayer = null;
            _swapSlot = -1;
            _swapOriginal = null;
        }

        /// <summary>
        /// Put <paramref name="original"/> back in the slot, but only if the slot still holds
        /// the toothbrush we put there. Returns false if the world isn't ready yet.
        /// </summary>
        private static bool RestoreLoadout(string player, int slot, string original, string reason)
        {
            try
            {
                var inv = FindFirst<MoonDustInventory>();
                var world = inv?.World;
                if (world == null) return false;

                var slots = world.PlayerLoadout(player)?.slots;
                if (slots == null || slot < 0 || slot >= slots.Length) return false;

                if (slots[slot] != ToothbrushId)
                {
                    Plugin.Log.LogInfo(
                        $"[GameBridge] Toothbrush restore ({reason}): slot {slot} holds '{slots[slot]}', not the toothbrush; leaving it.");
                    DeleteRestoreNote();
                    return true;
                }

                slots[slot] = original;
                world.CommitLoadout(player);
                Plugin.Log.LogInfo(
                    $"[GameBridge] Toothbrush restore ({reason}): slot {slot} -> '{world.LoadoutToolId(player, slot)}'.");
                DeleteRestoreNote();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameBridge] Toothbrush restore ({reason}) failed: {ex.Message}");
                return false;
            }
        }

        private static void CheckPendingRestore()
        {
            string path = RestoreNotePath;
            if (!System.IO.File.Exists(path))
            {
                _pendingRestoreChecked = true;
                return;
            }

            // Retry every few seconds until a save is loaded for the same player.
            _pendingRestoreRetryAt = Time.realtimeSinceStartup + 5f;
            string[] lines;
            try { lines = System.IO.File.ReadAllLines(path); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[GameBridge] Toothbrush restore note unreadable: " + ex.Message);
                _pendingRestoreChecked = true;
                return;
            }
            if (lines.Length < 3 || !int.TryParse(lines[1], out int slot))
            {
                Plugin.Log.LogWarning("[GameBridge] Toothbrush restore note is malformed; deleting it.");
                DeleteRestoreNote();
                _pendingRestoreChecked = true;
                return;
            }

            var inv = FindFirst<MoonDustInventory>();
            if (inv?.World == null || inv.playerId != lines[0]) return;

            if (RestoreLoadout(lines[0], slot, lines[2], "left over from the last session"))
                _pendingRestoreChecked = true;
        }

        private static bool OwnsTool(MoonUpgradeSnapshot snap, MoonToolbelt tb, int slot, string id)
        {
            var owned = snap.tools?.ToArray();
            if (owned != null)
                for (int i = 0; i < owned.Length; i++)
                    if (owned[i] != null && owned[i].id == id) return true;

            // The starter tool may not be listed; count the belt's base tool for the slot.
            var baseTools = tb.tools;
            var baseTool = baseTools != null && slot >= 0 && slot < baseTools.Length ? baseTools[slot] : null;
            return baseTool != null && baseTool.toolId == id;
        }

        private static void WriteRestoreNote(string player, int slot, string original)
        {
            try { System.IO.File.WriteAllLines(RestoreNotePath, new[] { player, slot.ToString(), original }); }
            catch (Exception ex) { Plugin.Log.LogWarning("[GameBridge] Couldn't write the toothbrush restore note: " + ex.Message); }
        }

        private static void DeleteRestoreNote()
        {
            try { if (System.IO.File.Exists(RestoreNotePath)) System.IO.File.Delete(RestoreNotePath); }
            catch (Exception ex) { Plugin.Log.LogWarning("[GameBridge] Couldn't delete the toothbrush restore note: " + ex.Message); }
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

        // =====================================================================
        //  Robots and money (instant; shared world state the host controls)
        // =====================================================================

        /// <summary>
        /// Switch off every robot of the given kinds, the same way a player does at the
        /// robot's menu. The players have to go and switch each one back on.
        /// <c>MoonWorld.MapRobots</c> lists every robot (loaded or not) with its dock, kind
        /// (MkI / MkII / Joonas) and on/off state; <c>SetRobotOn(dock, false)</c> is the
        /// game's own switch, which it also syncs to co-op crew.
        /// </summary>
        public static void RobotsOff(EffectContext ctx, string label, params MoonWorld.MapRobotKind[] kinds)
        {
            var world = FindFirst<MoonDustInventory>()?.World ?? FindFirst<MoonWorld>();
            if (world == null)
            {
                Plugin.Log.LogInfo($"[GameBridge] {label}: no MoonWorld (not in-game?). ({ctx.Describe()})");
                return;
            }

            var list = new Il2CppSystem.Collections.Generic.List<MoonWorld.MapRobot>();
            world.MapRobots(list);
            var robots = list.ToArray();

            int total = 0, switchedOff = 0, alreadyOff = 0;
            for (int i = 0; i < robots.Length; i++)
            {
                var r = robots[i];
                if (r == null || Array.IndexOf(kinds, r.kind) < 0) continue;
                total++;
                if (!r.on) { alreadyOff++; continue; }

                int dock = r.dock;
                if (Step($"{label}: SetRobotOn({dock}, false) [{r.kind} #{r.number}]", () => world.SetRobotOn(dock, false)))
                    switchedOff++;
            }

            if (robots.Length == 0)
            {
                // MapRobots came back empty; fall back to the loaded robot docks.
                var docks = MoonRobotDock.all?.ToArray();
                int dockCount = docks != null ? docks.Length : 0;
                for (int i = 0; i < dockCount; i++)
                {
                    var dock = docks![i];
                    var robot = dock?.Robot;
                    if (dock == null || robot == null) continue;

                    var kind = robot.Launcher != null
                        ? MoonWorld.MapRobotKind.Joonas
                        : MoonWorld.MapRobotKind.MkI; // MkI vs MkII isn't distinguishable here; both count as Roombas
                    bool wanted = Array.IndexOf(kinds, kind) >= 0 ||
                                  (kind == MoonWorld.MapRobotKind.MkI && Array.IndexOf(kinds, MoonWorld.MapRobotKind.MkII) >= 0);
                    if (!wanted) continue;
                    total++;

                    int id = dock.DockId;
                    if (!world.RobotOn(id)) { alreadyOff++; continue; }
                    if (Step($"{label}: SetRobotOn({id}, false) [dock fallback, {kind}]", () => world.SetRobotOn(id, false)))
                        switchedOff++;
                }
                Plugin.Log.LogInfo($"[GameBridge] {label}: MapRobots was empty; checked {dockCount} loaded dock(s).");
            }

            Plugin.Log.LogInfo(
                $"[GameBridge] {label}: switched off {switchedOff} of {total} robot(s) " +
                $"({alreadyOff} were already off). ({ctx.Describe()})");
        }

        /// <summary>
        /// Drain the team's shared money to zero, through the game's own purchase charge so
        /// the save, co-op sync and money display all update normally.
        /// </summary>
        public static void Bankrupt(EffectContext ctx)
        {
            var inv = FindFirst<MoonDustInventory>();
            var world = inv?.World;
            string? player = inv?.playerId;
            if (world == null || string.IsNullOrEmpty(player))
            {
                Plugin.Log.LogInfo($"[GameBridge] Bankrupt: world/player unavailable (not in-game?). ({ctx.Describe()})");
                return;
            }

            var before = world.WalletDollars(player);
            double beforeDollars = Il2CppSystem.Decimal.ToDouble(before);
            if (beforeDollars <= 0.0)
            {
                Plugin.Log.LogInfo($"[GameBridge] Bankrupt: the wallet is already empty. ({ctx.Describe()})");
                return;
            }

            // ChargeBuild adds the amount it's given (charging the balance doubled it in the
            // first test), so a spend is a negative amount.
            Step("Bankrupt: ChargeBuild(-balance)", () => world.ChargeBuild(player, -before));
            double afterDollars = Il2CppSystem.Decimal.ToDouble(world.WalletDollars(player));

            if (Math.Abs(afterDollars) > 0.005)
            {
                // Fall back to the shared balance itself.
                var wallets = world.wallets;
                if (wallets != null)
                {
                    Plugin.Log.LogWarning(
                        $"[GameBridge] Bankrupt: ChargeBuild left ${afterDollars:0.##}; setting the shared balance to 0.");
                    wallets.Balance = Il2CppSystem.Decimal.Zero;
                    afterDollars = Il2CppSystem.Decimal.ToDouble(world.WalletDollars(player));
                }
            }
            Plugin.Log.LogInfo(
                $"[GameBridge] Bankrupt: wallet ${beforeDollars:0.##} -> ${afterDollars:0.##}. ({ctx.Describe()})");
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
        /// now rebuilds from this data when it next loads; one that's already loaded is
        /// redrawn with <c>RestoreRenderingBindings</c>. Host-local.
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

            int id;
            int clearedCount;
            if (int.TryParse(ctx.Arg, out int shared))
            {
                // Relayed: re-dust the sector the original game picked, so everyone matches.
                id = shared;
                clearedCount = -1;
                if (id < 0 || id >= d.Count || d.States[id] != 1)
                {
                    Plugin.Log.LogInfo($"[GameBridge] Refill: sector {id} is already dusty here. ({ctx.Describe()})");
                    return;
                }
            }
            else
            {
                var cleared = new List<int>();
                for (int i = 0; i < d.Count; i++)
                    if (d.States[i] == 1) cleared.Add(i);
                if (cleared.Count == 0)
                {
                    Plugin.Log.LogInfo($"[GameBridge] Refill: no cleared sectors to re-dust. ({ctx.Describe()})");
                    return;
                }
                id = cleared[_rng.Next(cleared.Count)];
                clearedCount = cleared.Count;
                ctx.Arg = id.ToString(); // shared with the crew
            }
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
                // Redraw loaded ground live. The redraw lab found this world-level re-bind is
                // what makes an already-loaded sector show its dust without a reload.
                bool redrawn = Step("Refill: RestoreRenderingBindings", () => world.RestoreRenderingBindings());
                Plugin.Log.LogInfo(
                    $"[GameBridge] Refill: re-dusted cleared sector {id}{where} " +
                    $"({(clearedCount >= 0 ? clearedCount + " were cleared, " : "")}redraw {(redrawn ? "ok" : "failed")}). ({ctx.Describe()})");
            }
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
            // regionGpu is per-sector shader data. Keep this sector's own x/y/z (z differs per
            // sector; copying a neighbour's made the hex merge into it on screen) and only set
            // w, which reads 0 for a cleared sector and -1 for an untouched one.
            var gpu = d.Gpu;
            string gpuChange = "n/a";
            if (gpu != null && id < gpu.Length)
            {
                Vector4 old = gpu[id];
                float w = refId >= 0 && refId < gpu.Length ? gpu[refId].w : -1f;
                gpu[id] = new Vector4(old.x, old.y, old.z, w);
                gpuChange = $"{old} -> {gpu[id]}";
            }
            try { world.CompletedRegionCount = Math.Max(0, world.CompletedRegionCount - 1); }
            catch (Exception ex) { Plugin.Log.LogWarning("[GameBridge] Uncomplete: setting CompletedRegionCount threw: " + ex.Message); }

            bool ok = Step("Refill: UpdateProgress", () => world.UpdateProgress());
            Plugin.Log.LogInfo(
                $"[GameBridge] Uncomplete: region {id} -> state={d.States[id]} remaining={d.Remaining[id]:0.###} " +
                $"complete={SafeBool(() => world.IsRegionComplete(id))} " +
                $"gpu {gpuChange} CompletedRegionCount={world.CompletedRegionCount} " +
                $"(ref {refId}, UpdateProgress {(ok ? "ok" : "failed")}).");
            return true;
        }

        private static float TryFloat(Func<float> f)
        {
            try { return f(); } catch { return -1f; }
        }

        private static string SafeBool(Func<bool> f)
        {
            try { return f().ToString(); } catch (Exception ex) { return "threw: " + ex.Message; }
        }

        private static bool Step(string label, Action action)
        {
            try
            {
                action();
                Plugin.Log.LogInfo($"[GameBridge] {label} ok.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GameBridge] {label} threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Called every frame on the main thread. Enforces whichever timed effects are
        /// active and announces when each one ends. Never throws into the game loop.
        /// </summary>
        /// <summary>
        /// End every timed effect right now (breakdown, toothbrush, gravity, FOV, …). Each
        /// restores its original values on the next tick.
        /// </summary>
        public static void EndAllTimed(EffectContext ctx)
        {
            _vehiclesBrokenUntil = 0f;
            _toothbrushUntil = 0f;
            TimedEffects.StopAll();
            Plugin.Log.LogInfo($"[GameBridge] All timed effects ended. ({ctx.Describe()})");
        }

        public static void Tick()
        {
            TimedEffects.Tick();
            CrewRelay.Tick();

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
                    EndToothbrushSwap();
                    OnScreenMessages.Show("🧰 Tools unlocked.", 4f);
                }
                _toothbrushWasActive = brushOnly;

                // Put back a brush-slot tool left swapped by a session that ended mid-effect.
                if (!brushOnly && !_pendingRestoreChecked && Time.realtimeSinceStartup >= _pendingRestoreRetryAt)
                    CheckPendingRestore();
            }
            catch (Exception ex)
            {
                if (ex.Message != _lastTickError)
                {
                    _lastTickError = ex.Message;
                    Plugin.Log.LogError("[GameBridge] Tick error (repeats of this message are suppressed): " + ex);
                }
            }
        }

        private static void TryEjectFromVehicle()
        {
            // Driving and riding are separate systems: the driver is "mounted" on a
            // MoonPrototypeVehicle through the walker, while a rider sits via
            // MoonVehiclePassenger. Handle both.
            var w = LocalWalker();
            if (w != null && w.Mounted)
            {
                var v = w.mountedVehicle;
                bool ok = v != null && v.Exit(false);
                ThrottledLog($"[GameBridge] Breakdown: driver seat -> Exit() {(ok ? "ok" : "refused or no vehicle")}.");
            }

            var p = Passenger();
            if (p != null && p.Seated)
            {
                p.GetOut();
                ThrottledLog("[GameBridge] Breakdown: passenger seat -> GetOut().");
            }
        }

        private static float _nextThrottledLogAt;

        /// <summary>Log at most once a second, for actions that can repeat every frame.</summary>
        private static void ThrottledLog(string message)
        {
            if (Time.realtimeSinceStartup < _nextThrottledLogAt) return;
            _nextThrottledLogAt = Time.realtimeSinceStartup + 1f;
            Plugin.Log.LogInfo(message);
        }

        private static void EnforceToothbrush()
        {
            var tb = Toolbelt();
            if (tb == null) return;

            if (_brushSlot < 0)
                _brushSlot = ResolveBrushSlot(tb);
            if (_brushSlot < 0) return;

            // Diagnostic: log the toolbelt's state whenever it changes, to see what the
            // game reports while the player tries to switch tools.
            string state =
                $"selected={tb.SelectedIndex} prototype={tb.IsPrototypeEquipped} " +
                $"tool='{tb.SelectedTool?.displayName}' brushEquipped={tb.BrushEquipped}";
            if (state != _lastBeltState)
            {
                _lastBeltState = state;
                Plugin.Log.LogInfo($"[GameBridge] Toothbrush: toolbelt {state} (brush slot {_brushSlot}).");
            }

            // A "prototype" tool is equipped as an overlay on top of the slot; drop it.
            if (tb.IsPrototypeEquipped)
                tb.ClearPrototype();

            if (tb.SelectedIndex != _brushSlot)
            {
                bool ok = tb.SelectSlot(_brushSlot);
                ThrottledLog($"[GameBridge] Toothbrush: SelectSlot({_brushSlot}) -> {ok}.");
            }
        }

        private static string? _lastBeltState;

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

        private static MoonWalker? _walker;
        private static float _walkerRetryAt;

        /// <summary>The local player's walker, taken from the local dust inventory.</summary>
        internal static MoonWalker? LocalWalker()
        {
            if (_walker == null && Time.realtimeSinceStartup >= _walkerRetryAt)
            {
                _walker = FindFirst<MoonDustInventory>()?.walker;
                if (_walker == null) _walkerRetryAt = Time.realtimeSinceStartup + 2f;
            }
            return _walker;
        }

        private static MoonVehiclePassenger? Passenger()
        {
            if (_passenger == null && Time.realtimeSinceStartup >= _passengerRetryAt)
            {
                _passenger = PickOwned<MoonVehiclePassenger>(p => p.walker, "passenger");
                if (_passenger == null) _passengerRetryAt = Time.realtimeSinceStartup + 2f;
            }
            return _passenger;
        }

        private static MoonToolbelt? Toolbelt()
        {
            if (_toolbelt == null && Time.realtimeSinceStartup >= _toolbeltRetryAt)
            {
                _toolbelt = PickOwned<MoonToolbelt>(t => t.walker, "toolbelt");
                if (_toolbelt == null) _toolbeltRetryAt = Time.realtimeSinceStartup + 2f;
                _brushSlot = -1; // new toolbelt -> re-resolve the brush slot
            }
            return _toolbelt;
        }

        /// <summary>
        /// Of every live <typeparamref name="T"/>, return the one whose walker is the local
        /// player's. There can be more than one (co-op crew, or extra instances), so the first
        /// match isn't necessarily ours. Falls back to the first instance if none match, and
        /// logs what it found.
        /// </summary>
        private static T? PickOwned<T>(Func<T, MoonWalker?> walkerOf, string label) where T : Object
        {
            var local = LocalWalker();
            Il2CppArrayBase<T>? all;
            try { all = Object.FindObjectsByType<T>(FindObjectsSortMode.None); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[GameBridge] Find {label}s failed: {ex.Message}");
                return null;
            }
            if (all == null || all.Length == 0) return null;

            int picked = -1;
            for (int i = 0; i < all.Length && picked < 0; i++)
            {
                var w = all[i] != null ? walkerOf(all[i]) : null;
                if (local != null && w != null && w.Pointer == local.Pointer) picked = i;
            }
            bool matched = picked >= 0;
            if (!matched) picked = 0;

            Plugin.Log.LogInfo(
                $"[GameBridge] Found {all.Length} {label}(s); using #{picked} " +
                $"({(matched ? "matches the local player" : local == null ? "local player unknown" : "no match, first one")}).");
            return all[picked];
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
        internal static T? FindFirst<T>() where T : Object
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
    }
}

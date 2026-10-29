using System;
using System.Collections.Generic;
using MoonCleaners;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace MoonSweepersTwitch.Game
{
    /// <summary>
    /// An effect that changes some game values for a while, then puts them back.
    ///
    /// Every frame (<see cref="Tick"/>): while active, apply once the game is ready
    /// (retrying every 2 s until it is), then <see cref="Enforce"/> so a player or the game
    /// can't undo it; when the time runs out, <see cref="Revert"/> to the originals.
    /// Subclasses record originals only the first time they apply, so a retried apply can
    /// never mistake its own changed value for the original.
    /// </summary>
    internal abstract class TimedEffect
    {
        private float _until;
        private bool _applied;
        private float _retryAt;
        private string? _lastError;

        public abstract string Name { get; }

        public bool Active => Time.realtimeSinceStartup < _until;

        public void Start(float seconds)
        {
            _until = Time.realtimeSinceStartup + seconds;
            Plugin.Log.LogInfo($"[Timed] {Name}: active for {seconds:0}s.");
        }

        /// <summary>End now; the originals are restored on the next tick.</summary>
        public void Stop() => _until = 0f;

        public void Tick()
        {
            float now = Time.realtimeSinceStartup;
            try
            {
                if (now < _until)
                {
                    if (_applied)
                    {
                        Enforce();
                    }
                    else if (now >= _retryAt)
                    {
                        _applied = TryApply();
                        if (_applied) Plugin.Log.LogInfo($"[Timed] {Name}: applied.");
                        else _retryAt = now + 2f; // game not ready yet
                    }
                }
                else if (_applied)
                {
                    string name = Name;
                    _applied = false;
                    Revert();
                    Plugin.Log.LogInfo($"[Timed] {name}: ended, original values restored.");
                }
            }
            catch (Exception ex)
            {
                if (ex.Message != _lastError)
                {
                    _lastError = ex.Message;
                    Plugin.Log.LogError($"[Timed] {Name} threw (repeats suppressed): {ex}");
                }
            }
        }

        /// <summary>Record originals (first time only) and apply. False if the game isn't ready.</summary>
        protected abstract bool TryApply();

        /// <summary>Called every frame while applied, to re-assert the change.</summary>
        protected virtual void Enforce() { }

        /// <summary>Put the originals back and forget them.</summary>
        protected abstract void Revert();
    }

    /// <summary>2x / 4x / 8x gravity: falling is N times stronger; run speed, jump height and driven vehicles 1/N.</summary>
    internal sealed class GravityEffect : TimedEffect
    {
        private int _level = 1;
        private MoonSettings? _settings;
        private float? _gravity, _jumpHeight, _runSpeed;

        // Vehicles driven while the effect runs, with their original SpeedFactor. They stay
        // slowed until the effect ends, even after the player gets out.
        private readonly List<(MoonPrototypeVehicle vehicle, float speedFactor)> _vehicles =
            new List<(MoonPrototypeVehicle vehicle, float speedFactor)>();

        public override string Name => $"{_level}x gravity";

        /// <summary>
        /// A stronger level takes over and restarts the timer; a weaker one while a stronger
        /// one is running is ignored. Returns whether it started.
        /// </summary>
        public bool StartLevel(int level, float seconds)
        {
            if (Active && level < _level)
            {
                Plugin.Log.LogInfo($"[Timed] {level}x gravity ignored: {_level}x is already running.");
                return false;
            }
            _level = level;
            Start(seconds);
            if (_settings != null && _gravity.HasValue) Write(); // already applied: switch level now
            return true;
        }

        protected override bool TryApply()
        {
            _settings = TimedEffects.Settings();
            if (_settings == null) return false;
            _gravity ??= _settings.gravity;
            _jumpHeight ??= _settings.jumpHeight;
            _runSpeed ??= _settings.runSpeed;
            Write();
            SlowDrivenVehicle();
            return true;
        }

        protected override void Enforce() => SlowDrivenVehicle();

        private void Write()
        {
            var s = _settings!;
            s.gravity = _gravity!.Value * _level;
            s.jumpHeight = _jumpHeight!.Value / _level;
            s.runSpeed = _runSpeed!.Value / _level;
            Plugin.Log.LogInfo(
                $"[Timed] {_level}x gravity: gravity {_gravity!.Value:0.###} -> {s.gravity:0.###}, " +
                $"jumpHeight {_jumpHeight!.Value:0.###} -> {s.jumpHeight:0.###}, runSpeed {_runSpeed!.Value:0.###} -> {s.runSpeed:0.###}.");

            // A level change also re-slows every vehicle already affected.
            foreach (var (vehicle, factor) in _vehicles)
                if (vehicle != null) vehicle.SpeedFactor = factor / _level;
        }

        /// <summary>Slow the vehicle the local player is driving to 1/N of its own speed factor.</summary>
        private void SlowDrivenVehicle()
        {
            var walker = GameBridge.LocalWalker();
            var vehicle = walker != null && walker.Mounted ? walker.mountedVehicle : null;
            if (vehicle == null) return;

            int index = -1;
            for (int i = 0; i < _vehicles.Count; i++)
                if (_vehicles[i].vehicle != null && _vehicles[i].vehicle.Pointer == vehicle.Pointer) { index = i; break; }
            if (index < 0)
            {
                _vehicles.Add((vehicle, vehicle.SpeedFactor));
                index = _vehicles.Count - 1;
                Plugin.Log.LogInfo(
                    $"[Timed] {_level}x gravity: vehicle speed factor {vehicle.SpeedFactor:0.###} -> {vehicle.SpeedFactor / _level:0.###}.");
            }

            float wanted = _vehicles[index].speedFactor / _level;
            if (Mathf.Abs(vehicle.SpeedFactor - wanted) > 0.0001f) vehicle.SpeedFactor = wanted;
        }

        protected override void Revert()
        {
            if (_settings != null && _gravity.HasValue)
            {
                _settings.gravity = _gravity.Value;
                _settings.jumpHeight = _jumpHeight!.Value;
                _settings.runSpeed = _runSpeed!.Value;
            }
            foreach (var (vehicle, factor) in _vehicles)
                if (vehicle != null) vehicle.SpeedFactor = factor;
            _vehicles.Clear();
            _settings = null;
            _gravity = _jumpHeight = _runSpeed = null;
            _level = 1;
        }
    }

    /// <summary>Scales one or more <see cref="MoonSettings"/> values by a factor (Paycut, Tiny Pockets, Butterfingers).</summary>
    internal sealed class SettingsScaleEffect : TimedEffect
    {
        private readonly string _name;
        private readonly float _factor;
        private readonly (string field, Func<MoonSettings, float> get, Action<MoonSettings, float> set)[] _fields;
        private MoonSettings? _settings;
        private float[]? _originals;

        public SettingsScaleEffect(string name, float factor,
            params (string field, Func<MoonSettings, float> get, Action<MoonSettings, float> set)[] fields)
        {
            _name = name;
            _factor = factor;
            _fields = fields;
        }

        public override string Name => _name;

        protected override bool TryApply()
        {
            _settings = TimedEffects.Settings();
            if (_settings == null) return false;

            if (_originals == null)
            {
                _originals = new float[_fields.Length];
                for (int i = 0; i < _fields.Length; i++) _originals[i] = _fields[i].get(_settings);
            }
            for (int i = 0; i < _fields.Length; i++)
            {
                _fields[i].set(_settings, _originals[i] * _factor);
                Plugin.Log.LogInfo(
                    $"[Timed] {_name}: {_fields[i].field} {_originals[i]:0.###} -> {_fields[i].get(_settings):0.###}.");
            }
            return true;
        }

        protected override void Revert()
        {
            if (_settings != null && _originals != null)
                for (int i = 0; i < _fields.Length; i++) _fields[i].set(_settings, _originals[i]);
            _settings = null;
            _originals = null;
        }
    }

    /// <summary>
    /// Tiny Pockets: cuts how much dust the player can carry to a quarter.
    ///
    /// The game works capacity out (<c>MoonWorld.InventoryCapacityKg</c>) from a small base
    /// (<c>MoonSettings.inventoryCapacityKg</c>, 1 kg) plus the dust container's capacity,
    /// which comes from that tool definition's <c>containerKg</c>. Scaling only the base did
    /// nothing noticeable, so this scales the base and every container tool's
    /// <c>containerKg</c>, and logs the real total before and after.
    /// </summary>
    internal sealed class TinyPocketsEffect : TimedEffect
    {
        private const float Factor = 0.25f;
        private MoonSettings? _settings;
        private float? _baseKg;
        private readonly List<(MoonToolDefinition tool, float kg)> _containers =
            new List<(MoonToolDefinition tool, float kg)>();
        private bool _containersCaptured;

        public override string Name => "Tiny pockets";

        protected override bool TryApply()
        {
            var inv = GameBridge.FindFirst<MoonDustInventory>();
            var world = inv?.World;
            string? player = inv?.playerId;
            if (world == null || string.IsNullOrEmpty(player)) return false;

            _settings = world.settings;
            double before = world.InventoryCapacityKg(player);

            _baseKg ??= _settings.inventoryCapacityKg;
            _settings.inventoryCapacityKg = _baseKg.Value * Factor;

            if (!_containersCaptured)
            {
                foreach (var tool in ContainerTools(world, player!))
                    _containers.Add((tool, tool.containerKg));
                _containersCaptured = true;
            }
            foreach (var (tool, kg) in _containers) tool.containerKg = kg * Factor;

            double after = world.InventoryCapacityKg(player);
            Plugin.Log.LogInfo(
                $"[Timed] Tiny pockets: carrying capacity {before:0.##} kg -> {after:0.##} kg " +
                $"(base {_baseKg.Value:0.##} -> {_settings.inventoryCapacityKg:0.##}, {_containers.Count} container tool(s)).");

            ShrinkVehicleStorage(world);
            return true;
        }

        protected override void Enforce()
        {
            // The game may raise a vehicle's capacity back (e.g. on upgrade); hold it down.
            foreach (var (storage, capacity) in _storages)
            {
                double wanted = capacity * Factor;
                if (storage != null && storage.capacity > wanted + 0.001) storage.capacity = wanted;
            }
        }

        protected override void Revert()
        {
            if (_settings != null && _baseKg.HasValue) _settings.inventoryCapacityKg = _baseKg.Value;
            foreach (var (tool, kg) in _containers)
                if (tool != null) tool.containerKg = kg;
            foreach (var (storage, capacity) in _storages)
                if (storage != null) storage.capacity = capacity;
            TimedEffects.DeleteVehicleRestoreNote();

            _settings = null;
            _baseKg = null;
            _containers.Clear();
            _containersCaptured = false;
            _storages.Clear();
            _storagesCaptured = false;
        }

        // Vehicle dust storage, with each entry's original capacity. Only the capacity
        // changes, never the dust inside, so a vehicle already over the new limit keeps its
        // dust but can't take more.
        private readonly List<(MoonVehicleStorageSnapshot storage, double capacity)> _storages =
            new List<(MoonVehicleStorageSnapshot storage, double capacity)>();
        private bool _storagesCaptured;

        private void ShrinkVehicleStorage(MoonWorld world)
        {
            if (!_storagesCaptured)
            {
                var all = world.vehicleStorage;
                if (all != null)
                {
                    foreach (var entry in all)
                    {
                        var storage = entry.Value;
                        if (storage == null || storage.capacity <= 0.0 || MoonWorld.RobotStorage(entry.Key)) continue;
                        _storages.Add((storage, storage.capacity));
                    }
                }
                _storagesCaptured = true;

                // Vehicle storage is saved with the game, so note the originals first.
                TimedEffects.WriteVehicleRestoreNote(_storages);
            }

            double before = 0, after = 0;
            foreach (var (storage, capacity) in _storages)
            {
                if (storage == null) continue;
                storage.capacity = capacity * Factor;
                before += capacity;
                after += storage.capacity;
            }
            Plugin.Log.LogInfo(
                $"[Timed] Tiny pockets: {_storages.Count} vehicle storage(s), total capacity {before:0.##} kg -> {after:0.##} kg.");
        }

        /// <summary>
        /// Every tool definition that holds dust. Tool definitions are assets rather than
        /// scene objects, so they're listed through <c>Resources.FindObjectsOfTypeAll</c>,
        /// falling back to the player's loadout if that lookup isn't available.
        /// </summary>
        private static List<MoonToolDefinition> ContainerTools(MoonWorld world, string player)
        {
            var found = new List<MoonToolDefinition>();
            void Add(MoonToolDefinition? t)
            {
                if (t == null || t.containerKg <= 0f) return;
                for (int i = 0; i < found.Count; i++)
                    if (found[i].Pointer == t.Pointer) return;
                found.Add(t);
            }

            try
            {
                var all = Resources.FindObjectsOfTypeAll<MoonToolDefinition>();
                for (int i = 0; all != null && i < all.Length; i++) Add(all[i]);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Timed] Tiny pockets: listing tool assets failed (" + ex.Message + "); using the loadout.");
            }

            if (found.Count == 0)
            {
                for (int slot = 0; slot < 10; slot++)
                {
                    try { Add(world.LoadoutTool(player, slot)); } catch { }
                }
                try { Add(world.HeldTool(player)); } catch { }
            }
            return found;
        }
    }

    /// <summary>Lights Out: keeps the local player's helmet lamp switched off.</summary>
    internal sealed class LightsOutEffect : TimedEffect
    {
        private MoonHelmetLamp? _lamp;
        private bool? _wasOn;

        public override string Name => "Lights out";

        protected override bool TryApply()
        {
            var lamp = LocalLamp();
            if (lamp == null) return false;
            _lamp = lamp;
            _wasOn ??= lamp.On;
            lamp.On = false;
            Plugin.Log.LogInfo($"[Timed] Lights out: helmet lamp was {(_wasOn.Value ? "on" : "off")}, now off.");
            return true;
        }

        protected override void Enforce()
        {
            if (_lamp != null && _lamp.On) _lamp.On = false;
        }

        protected override void Revert()
        {
            if (_lamp != null && _wasOn.HasValue) _lamp.On = _wasOn.Value;
            _lamp = null;
            _wasOn = null;
        }

        private static MoonHelmetLamp? LocalLamp()
        {
            var local = GameBridge.LocalWalker();
            var lamps = MoonHelmetLamp.lamps?.ToArray();
            if (local == null || lamps == null) return null;
            for (int i = 0; i < lamps.Length; i++)
            {
                var w = lamps[i] != null ? lamps[i].walker : null;
                if (w != null && w.Pointer == local.Pointer) return lamps[i];
            }
            return null;
        }
    }

    /// <summary>
    /// Tunnel Vision / Fisheye: one field-of-view override, since two would fight. Starting
    /// either while the other runs switches to it and restarts the timer.
    ///
    /// The game's FOV is a saved user preference, so the original is written to a restore
    /// note before the change and the note is deleted after restoring. If a session ends
    /// mid-effect, <see cref="TimedEffects.Tick"/> restores it next session.
    /// </summary>
    internal sealed class FovEffect : TimedEffect
    {
        private string _mode = "FOV";
        private float _target;
        private float? _originalSetting, _originalCamera;

        public override string Name => _mode;

        public void StartMode(string mode, float fov, float seconds)
        {
            _mode = mode;
            _target = fov;
            Start(seconds);
        }

        protected override bool TryApply()
        {
            var settings = MoonUserSettings.Current;
            var cam = GameBridge.LocalWalker()?.viewCamera;
            if (settings == null || cam == null) return false;

            if (_originalSetting == null)
            {
                _originalSetting = settings.fieldOfView;
                TimedEffects.WriteFovRestoreNote(_originalSetting.Value);
            }
            _originalCamera ??= cam.fieldOfView;
            Enforce();
            Plugin.Log.LogInfo(
                $"[Timed] {_mode}: FOV setting {_originalSetting!.Value:0.#} (camera {_originalCamera!.Value:0.#}) -> {_target:0.#}.");
            return true;
        }

        protected override void Enforce()
        {
            var settings = MoonUserSettings.Current;
            if (settings != null) settings.fieldOfView = _target;
            var cam = GameBridge.LocalWalker()?.viewCamera;
            if (cam != null) cam.fieldOfView = _target;
        }

        protected override void Revert()
        {
            var settings = MoonUserSettings.Current;
            if (settings != null && _originalSetting.HasValue) settings.fieldOfView = _originalSetting.Value;
            var cam = GameBridge.LocalWalker()?.viewCamera;
            if (cam != null && _originalCamera.HasValue) cam.fieldOfView = _originalCamera.Value;
            TimedEffects.DeleteFovRestoreNote();
            _originalSetting = _originalCamera = null;
        }
    }

    /// <summary>
    /// Potato Mode: drops the URP render scale so everything is drawn at a fraction of the
    /// resolution. Changes the in-memory pipeline asset only, never the saved preferences.
    /// </summary>
    internal sealed class PotatoEffect : TimedEffect
    {
        private const float Scale = 0.25f;
        private UniversalRenderPipelineAsset? _pipeline;
        private float? _original;

        public override string Name => "Potato mode";

        protected override bool TryApply()
        {
            var pipeline = MoonGameSettings.runtimePipeline;
            if (pipeline == null) return false;
            _pipeline = pipeline;
            _original ??= pipeline.renderScale;
            pipeline.renderScale = Scale;
            Plugin.Log.LogInfo($"[Timed] Potato mode: renderScale {_original!.Value:0.##} -> {pipeline.renderScale:0.##}.");
            return true;
        }

        protected override void Enforce()
        {
            if (_pipeline != null && Mathf.Abs(_pipeline.renderScale - Scale) > 0.001f) _pipeline.renderScale = Scale;
        }

        protected override void Revert()
        {
            if (_pipeline != null && _original.HasValue) _pipeline.renderScale = _original.Value;
            _pipeline = null;
            _original = null;
        }
    }

    /// <summary>The timed effects, ticked every frame from <see cref="GameBridge.Tick"/>.</summary>
    internal static class TimedEffects
    {
        public static readonly GravityEffect Gravity = new GravityEffect();
        public static readonly FovEffect Fov = new FovEffect();
        public static readonly LightsOutEffect LightsOut = new LightsOutEffect();
        public static readonly PotatoEffect Potato = new PotatoEffect();

        public static readonly SettingsScaleEffect Paycut = new SettingsScaleEffect("Paycut", 0.5f,
            ("dollarsPerKilogram", s => s.dollarsPerKilogram, (s, v) => s.dollarsPerKilogram = v),
            ("dollarsPerSection", s => s.dollarsPerSection, (s, v) => s.dollarsPerSection = v));

        public static readonly TinyPocketsEffect TinyPockets = new TinyPocketsEffect();

        public static readonly SettingsScaleEffect Butterfingers = new SettingsScaleEffect("Butterfingers", 0.5f,
            ("canisterPickupReach", s => s.canisterPickupReach, (s, v) => s.canisterPickupReach = v));

        private static readonly TimedEffect[] All =
            { Gravity, Fov, LightsOut, Potato, Paycut, TinyPockets, Butterfingers };

        private static bool _fovRestoreChecked;
        private static float _fovRestoreRetryAt;

        public static void Tick()
        {
            for (int i = 0; i < All.Length; i++) All[i].Tick();

            if (!_fovRestoreChecked && !Fov.Active && Time.realtimeSinceStartup >= _fovRestoreRetryAt)
                CheckPendingFovRestore();

            if (!_vehicleRestoreChecked && !TinyPockets.Active && Time.realtimeSinceStartup >= _vehicleRestoreRetryAt)
                CheckPendingVehicleRestore();
        }

        /// <summary>End every timed effect now; each restores its originals on the next tick.</summary>
        public static void StopAll()
        {
            for (int i = 0; i < All.Length; i++) All[i].Stop();
        }

        /// <summary>The game's tuning sheet (gravity, speeds, pay rates, capacities).</summary>
        internal static MoonSettings? Settings() =>
            GameBridge.FindFirst<MoonDustInventory>()?.World?.settings;

        // ---- Vehicle capacity restore note (vehicle storage is saved with the game) ----

        private static bool _vehicleRestoreChecked;
        private static float _vehicleRestoreRetryAt;

        private static string VehicleNotePath => System.IO.Path.Combine(
            BepInEx.Paths.ConfigPath, "com.soytortuga.moonsweepers.twitch.vehicle-capacity-restore.txt");

        internal static void WriteVehicleRestoreNote(List<(MoonVehicleStorageSnapshot storage, double capacity)> storages)
        {
            try
            {
                var lines = new List<string>();
                foreach (var (storage, capacity) in storages)
                    if (storage != null && !string.IsNullOrEmpty(storage.id))
                        lines.Add(storage.id + "\t" + capacity.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                System.IO.File.WriteAllLines(VehicleNotePath, lines);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Timed] Couldn't write the vehicle capacity restore note: " + ex.Message); }
        }

        internal static void DeleteVehicleRestoreNote()
        {
            try { if (System.IO.File.Exists(VehicleNotePath)) System.IO.File.Delete(VehicleNotePath); }
            catch (Exception ex) { Plugin.Log.LogWarning("[Timed] Couldn't delete the vehicle capacity restore note: " + ex.Message); }
        }

        /// <summary>Raise any vehicle left shrunk by a session that ended mid-effect back to its original capacity.</summary>
        private static void CheckPendingVehicleRestore()
        {
            if (!System.IO.File.Exists(VehicleNotePath))
            {
                _vehicleRestoreChecked = true;
                return;
            }

            _vehicleRestoreRetryAt = Time.realtimeSinceStartup + 5f;
            var all = GameBridge.FindFirst<MoonDustInventory>()?.World?.vehicleStorage;
            if (all == null) return; // no save loaded yet

            int restored = 0;
            try
            {
                foreach (var line in System.IO.File.ReadAllLines(VehicleNotePath))
                {
                    var parts = line.Split('\t');
                    if (parts.Length != 2 || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double capacity)) continue;

                    if (all.TryGetValue(parts[0], out var storage) && storage != null && storage.capacity < capacity - 0.001)
                    {
                        storage.capacity = capacity;
                        restored++;
                    }
                }
                Plugin.Log.LogInfo($"[Timed] Restored {restored} vehicle storage capacity(ies) left over from the last session.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Timed] Vehicle capacity restore failed: " + ex.Message);
            }
            DeleteVehicleRestoreNote();
            _vehicleRestoreChecked = true;
        }

        // ---- FOV restore note (the FOV is a saved preference) ----

        private static string FovNotePath => System.IO.Path.Combine(
            BepInEx.Paths.ConfigPath, "com.soytortuga.moonsweepers.twitch.fov-restore.txt");

        internal static void WriteFovRestoreNote(float fov)
        {
            try { System.IO.File.WriteAllText(FovNotePath, fov.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (Exception ex) { Plugin.Log.LogWarning("[Timed] Couldn't write the FOV restore note: " + ex.Message); }
        }

        internal static void DeleteFovRestoreNote()
        {
            try { if (System.IO.File.Exists(FovNotePath)) System.IO.File.Delete(FovNotePath); }
            catch (Exception ex) { Plugin.Log.LogWarning("[Timed] Couldn't delete the FOV restore note: " + ex.Message); }
        }

        private static void CheckPendingFovRestore()
        {
            if (!System.IO.File.Exists(FovNotePath))
            {
                _fovRestoreChecked = true;
                return;
            }

            _fovRestoreRetryAt = Time.realtimeSinceStartup + 5f;
            var settings = MoonUserSettings.Current;
            if (settings == null) return; // settings not loaded yet

            try
            {
                string text = System.IO.File.ReadAllText(FovNotePath).Trim();
                if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fov) && fov > 1f && fov < 179f)
                {
                    settings.fieldOfView = fov;
                    Plugin.Log.LogInfo($"[Timed] Restored FOV {fov:0.#} left over from the last session.");
                }
                else
                {
                    Plugin.Log.LogWarning($"[Timed] FOV restore note '{text}' is invalid; deleting it.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[Timed] FOV restore failed: " + ex.Message);
            }
            DeleteFovRestoreNote();
            _fovRestoreChecked = true;
        }
    }
}

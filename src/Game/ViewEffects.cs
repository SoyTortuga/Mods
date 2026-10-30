using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace MoonSweepersTwitch.Game
{
    /// <summary>
    /// Reverse Controls: inverts the local player's movement and look.
    ///
    /// The walker reads its <c>move</c> and <c>look</c> input actions; appending the Input
    /// System's <c>invertVector2</c> processor to each binding that produces the Vector2
    /// (a WASD composite as a whole, a stick, mouse delta; not the individual W/A/S/D parts)
    /// flips both axes. A binding override replaces path, interactions AND processors, so
    /// the existing path/interaction overrides are carried over and the existing processors
    /// kept in front of the inversion. Each binding is restored exactly afterwards.
    /// </summary>
    internal sealed class ReverseControlsEffect : TimedEffect
    {
        private const string Invert = "invertVector2";

        private readonly List<(InputAction action, int index, string? path, string? interactions, string? processors)> _changed =
            new List<(InputAction action, int index, string? path, string? interactions, string? processors)>();

        public override string Name => "Reverse controls";

        protected override bool TryApply()
        {
            var walker = GameBridge.LocalWalker();
            if (walker == null || walker.move == null || walker.look == null) return false;

            if (_changed.Count == 0)
            {
                TimedEffects.WriteReverseControlsNote();
                InvertAction(walker.move, "move");
                InvertAction(walker.look, "look");
            }
            Plugin.Log.LogInfo($"[Timed] Reverse controls: inverted {_changed.Count} binding(s).");
            return true;
        }

        private void InvertAction(InputAction action, string label)
        {
            var bindings = action.bindings;
            var array = bindings.m_Array;
            int start = bindings.m_StartIndex, count = bindings.m_Length;
            for (int i = 0; array != null && i < count; i++)
            {
                var b = array[start + i];
                if (b is null || b.isPartOfComposite) continue;

                string current = b.effectiveProcessors ?? "";
                if (current.Contains(Invert)) continue;
                string combined = current.Length > 0 ? current + "," + Invert : Invert;

                _changed.Add((action, i, b.overridePath, b.overrideInteractions, b.overrideProcessors));
                InputActionRebindingExtensions.ApplyBindingOverride(action, i, new InputBinding
                {
                    overridePath = b.overridePath,
                    overrideInteractions = b.overrideInteractions,
                    overrideProcessors = combined,
                });
                Plugin.Log.LogInfo($"[Timed] Reverse controls: {label}[{i}] '{b.path}' processors '{current}' -> '{combined}'.");
            }
        }

        protected override void Revert()
        {
            foreach (var (action, index, path, interactions, processors) in _changed)
            {
                if (action == null) continue;
                if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(interactions) && string.IsNullOrEmpty(processors))
                    InputActionRebindingExtensions.RemoveBindingOverride(action, index);
                else
                    InputActionRebindingExtensions.ApplyBindingOverride(action, index, new InputBinding
                    {
                        overridePath = path,
                        overrideInteractions = interactions,
                        overrideProcessors = processors,
                    });
            }
            _changed.Clear();
            TimedEffects.DeleteReverseControlsNote();
        }

        /// <summary>
        /// Remove an inversion left in the move/look bindings by a session that ended
        /// mid-effect (in case the game saved input overrides). Returns false until the
        /// walker exists.
        /// </summary>
        internal static bool StripLeftoverInversion()
        {
            var walker = GameBridge.LocalWalker();
            if (walker == null || walker.move == null || walker.look == null) return false;

            int stripped = Strip(walker.move) + Strip(walker.look);
            Plugin.Log.LogInfo($"[Timed] Reverse controls: removed {stripped} inversion(s) left over from the last session.");
            return true;
        }

        private static int Strip(InputAction action)
        {
            int stripped = 0;
            var bindings = action.bindings;
            var array = bindings.m_Array;
            int start = bindings.m_StartIndex, count = bindings.m_Length;
            for (int i = 0; array != null && i < count; i++)
            {
                var b = array[start + i];
                string? procs = b?.overrideProcessors;
                if (b is null || string.IsNullOrEmpty(procs) || !procs!.Contains(Invert)) continue;

                var kept = new List<string>();
                foreach (var p in procs.Split(','))
                    if (p.Trim() != Invert && p.Trim().Length > 0) kept.Add(p.Trim());
                string? rest = kept.Count > 0 ? string.Join(",", kept) : null;

                if (rest == null && string.IsNullOrEmpty(b.overridePath) && string.IsNullOrEmpty(b.overrideInteractions))
                    InputActionRebindingExtensions.RemoveBindingOverride(action, i);
                else
                    InputActionRebindingExtensions.ApplyBindingOverride(action, i, new InputBinding
                    {
                        overridePath = b.overridePath,
                        overrideInteractions = b.overrideInteractions,
                        overrideProcessors = rest,
                    });
                stripped++;
            }
            return stripped;
        }
    }

    /// <summary>
    /// Australian Moon: the player's view is upside down. The camera hook rolls the view
    /// camera 180° just before it renders and puts it back just after, so the game's own
    /// aiming and interaction never see the roll and the screen-space HUD stays upright.
    /// </summary>
    internal sealed class UpsideDownEffect : TimedEffect
    {
        private Camera? _rolled;
        private Quaternion _savedRotation;

        public override string Name => "Australian moon";

        protected override bool TryApply()
        {
            if (GameBridge.LocalWalker()?.viewCamera == null) return false;
            return CameraHooks.EnsureInstalled();
        }

        protected override void Revert() { } // the hook stops rolling once IsApplied is false

        internal void BeforeRender(Camera cam)
        {
            if (!IsApplied || !CameraHooks.IsPlayerCamera(cam)) return;
            var t = cam.transform;
            _savedRotation = t.rotation;
            t.rotation = _savedRotation * Quaternion.Euler(0f, 0f, 180f);
            _rolled = cam;
        }

        internal void AfterRender(Camera cam)
        {
            if (_rolled == null || cam == null || cam.Pointer != _rolled.Pointer) return;
            cam.transform.rotation = _savedRotation;
            _rolled = null;
        }
    }

    /// <summary>
    /// Moon Storm: vision is cut to a short distance. While the player's camera renders,
    /// its draw distance is shortened and it clears to a grey "storm wall" instead of
    /// space; grey exponential fog fades the terrain into it (if the game's shaders
    /// support fog, the wall is softened; if not, it's a hard grey edge).
    /// </summary>
    internal sealed class MoonStormEffect : TimedEffect
    {
        private const float Distance = 35f;
        private static readonly Color StormGrey = new Color(136f / 255f, 139f / 255f, 142f / 255f, 1f); // #888b8e

        private bool? _fog;
        private FogMode _fogMode;
        private Color _fogColor;
        private float _fogDensity;

        private Camera? _changed;
        private float _savedFar;
        private CameraClearFlags _savedClear;
        private Color _savedBackground;

        public override string Name => "Moon storm";

        protected override bool TryApply()
        {
            if (GameBridge.LocalWalker()?.viewCamera == null) return false;
            if (!CameraHooks.EnsureInstalled()) return false;

            if (_fog == null)
            {
                _fog = RenderSettings.fog;
                _fogMode = RenderSettings.fogMode;
                _fogColor = RenderSettings.fogColor;
                _fogDensity = RenderSettings.fogDensity;
            }
            Enforce();
            Plugin.Log.LogInfo($"[Timed] Moon storm: view distance {Distance} m, fog on.");
            return true;
        }

        protected override void Enforce()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = StormGrey;
            RenderSettings.fogDensity = 0.09f; // ~95% fogged by 35 m
        }

        protected override void Revert()
        {
            if (_fog.HasValue)
            {
                RenderSettings.fog = _fog.Value;
                RenderSettings.fogMode = _fogMode;
                RenderSettings.fogColor = _fogColor;
                RenderSettings.fogDensity = _fogDensity;
            }
            _fog = null;
        }

        internal void BeforeRender(Camera cam)
        {
            if (!IsApplied || !CameraHooks.IsPlayerCamera(cam)) return;
            _savedFar = cam.farClipPlane;
            _savedClear = cam.clearFlags;
            _savedBackground = cam.backgroundColor;
            cam.farClipPlane = Mathf.Min(_savedFar, Distance);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = StormGrey;
            _changed = cam;
        }

        internal void AfterRender(Camera cam)
        {
            if (_changed == null || cam == null || cam.Pointer != _changed.Pointer) return;
            cam.farClipPlane = _savedFar;
            cam.clearFlags = _savedClear;
            cam.backgroundColor = _savedBackground;
            _changed = null;
        }
    }

    /// <summary>
    /// Runs view effects at the last moment before the player's camera renders, and undoes
    /// them right after, through URP's begin/endCameraRendering events. Installed lazily the
    /// first time a view effect needs it.
    /// </summary>
    internal static class CameraHooks
    {
        private static bool _installed;
        private static bool _failed;
        private static string? _lastError;

        // Kept referenced so the converted delegates aren't collected.
        private static Il2CppSystem.Action<ScriptableRenderContext, Camera>? _begin, _end;

        public static bool EnsureInstalled()
        {
            if (_installed) return true;
            if (_failed) return false;
            try
            {
                _begin = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                    new Action<ScriptableRenderContext, Camera>(OnBegin));
                _end = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                    new Action<ScriptableRenderContext, Camera>(OnEnd));
                RenderPipelineManager.add_beginCameraRendering(_begin);
                RenderPipelineManager.add_endCameraRendering(_end);
                _installed = true;
                Plugin.Log.LogInfo("[CameraHooks] Installed.");
                return true;
            }
            catch (Exception ex)
            {
                _failed = true;
                Plugin.Log.LogError("[CameraHooks] Couldn't install the render hooks: " + ex);
                return false;
            }
        }

        internal static bool IsPlayerCamera(Camera cam)
        {
            var view = GameBridge.LocalWalker()?.viewCamera;
            return cam != null && view != null && cam.Pointer == view.Pointer;
        }

        private static void OnBegin(ScriptableRenderContext context, Camera cam)
        {
            try
            {
                TimedEffects.UpsideDown.BeforeRender(cam);
                TimedEffects.Storm.BeforeRender(cam);
            }
            catch (Exception ex) { LogOnce(ex); }
        }

        private static void OnEnd(ScriptableRenderContext context, Camera cam)
        {
            try
            {
                TimedEffects.Storm.AfterRender(cam);
                TimedEffects.UpsideDown.AfterRender(cam);
            }
            catch (Exception ex) { LogOnce(ex); }
        }

        private static void LogOnce(Exception ex)
        {
            if (ex.Message == _lastError) return;
            _lastError = ex.Message;
            Plugin.Log.LogError("[CameraHooks] Render hook threw (repeats suppressed): " + ex);
        }
    }
}

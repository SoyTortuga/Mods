using System;
using System.Collections.Generic;
using MoonSweepersTwitch.Config;
using MoonSweepersTwitch.Game;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace MoonSweepersTwitch.Util
{
    /// <summary>
    /// The one MonoBehaviour injected into IL2CPP. Gives the mod a per-frame
    /// Update (to drain the dispatcher, enforce timed effects, and poll the test
    /// hotkey) and OnGUI (overlay).
    /// </summary>
    public sealed class ModBehaviour : MonoBehaviour
    {
        private PluginConfig? _config;
        private EffectRegistry? _effects;

        // Logged once if the configured hotkey can't be used, so we never spam.
        private bool _hotkeyWarned;

        // Required constructor for IL2CPP-injected MonoBehaviours.
        public ModBehaviour(IntPtr ptr) : base(ptr) { }

        // Parsed from the TestHotkeys config ("F1=joonasoff || F2=bankrupt").
        private readonly List<(KeyCode key, string effect)> _extraHotkeys = new List<(KeyCode key, string effect)>();

        public void Initialize(PluginConfig config, EffectRegistry effects)
        {
            _config = config;
            _effects = effects;
            OnScreenMessages.Enabled = config.ShowOnScreenFeedback.Value;
            ParseExtraHotkeys(config.TestHotkeys.Value);
        }

        private void ParseExtraHotkeys(string spec)
        {
            _extraHotkeys.Clear();
            if (string.IsNullOrWhiteSpace(spec)) return;

            foreach (var pair in spec.Split(new[] { "||" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(new[] { '=' }, 2);
                if (parts.Length == 2 &&
                    Enum.TryParse(parts[0].Trim(), ignoreCase: true, out KeyCode key) &&
                    key != KeyCode.None && parts[1].Trim().Length > 0)
                {
                    _extraHotkeys.Add((key, parts[1].Trim()));
                }
                else
                {
                    Plugin.Log.LogWarning($"TestHotkeys: couldn't read '{pair.Trim()}' (expected Key=effectKey).");
                }
            }
            Plugin.Log.LogInfo($"TestHotkeys: {_extraHotkeys.Count} extra test hotkey(s) loaded.");
        }

        private void Update()
        {
            MainThreadDispatcher.Drain();

            // Enforce any active timed effects (vehicle breakdown, toothbrush-only).
            GameBridge.Tick();

            if (_config == null || _effects == null) return;

            PollTestHotkey();
        }

        private void PollTestHotkey()
        {
            // Moon Sweepers ships with the new Input System active, so the legacy
            // UnityEngine.Input API throws every frame. Read the keyboard device instead.
            try
            {
                var keyboard = Keyboard.current;
                if (keyboard == null) return;

                PollOne(keyboard, _config!.TestEffectKey.Value, _config.TestEffectKeyName.Value);
                PollOne(keyboard, _config.TestEffectKey2.Value, _config.TestEffectKey2Name.Value);
                foreach (var (key, effect) in _extraHotkeys)
                    PollOne(keyboard, key, effect);
            }
            catch (Exception ex)
            {
                if (!_hotkeyWarned)
                {
                    _hotkeyWarned = true;
                    Plugin.Log.LogWarning("Test hotkey polling failed: " + ex.Message);
                }
            }
        }

        private void PollOne(Keyboard keyboard, KeyCode code, string effectKey)
        {
            if (code == KeyCode.None || string.IsNullOrEmpty(effectKey)) return;

            var control = ResolveKeyControl(keyboard, code);
            if (control == null)
            {
                if (!_hotkeyWarned)
                {
                    _hotkeyWarned = true;
                    Plugin.Log.LogWarning(
                        $"Test hotkey '{code}' isn't mapped to an Input System key; hotkey disabled.");
                }
                return;
            }

            if (control.wasPressedThisFrame)
                _effects!.Trigger(effectKey, EffectContext.Test(effectKey));
        }

        /// <summary>
        /// Map the legacy <see cref="KeyCode"/> stored in config to the new Input
        /// System's named <see cref="KeyControl"/> on the current keyboard. Covers the
        /// function, letter, digit, arrow and common editing/modifier keys; returns
        /// null for anything unmapped (the hotkey then simply disables itself).
        /// </summary>
        private static KeyControl? ResolveKeyControl(Keyboard kb, KeyCode code)
        {
            switch (code)
            {
                case KeyCode.F1: return kb.f1Key;
                case KeyCode.F2: return kb.f2Key;
                case KeyCode.F3: return kb.f3Key;
                case KeyCode.F4: return kb.f4Key;
                case KeyCode.F5: return kb.f5Key;
                case KeyCode.F6: return kb.f6Key;
                case KeyCode.F7: return kb.f7Key;
                case KeyCode.F8: return kb.f8Key;
                case KeyCode.F9: return kb.f9Key;
                case KeyCode.F10: return kb.f10Key;
                case KeyCode.F11: return kb.f11Key;
                case KeyCode.F12: return kb.f12Key;

                case KeyCode.A: return kb.aKey;
                case KeyCode.B: return kb.bKey;
                case KeyCode.C: return kb.cKey;
                case KeyCode.D: return kb.dKey;
                case KeyCode.E: return kb.eKey;
                case KeyCode.F: return kb.fKey;
                case KeyCode.G: return kb.gKey;
                case KeyCode.H: return kb.hKey;
                case KeyCode.I: return kb.iKey;
                case KeyCode.J: return kb.jKey;
                case KeyCode.K: return kb.kKey;
                case KeyCode.L: return kb.lKey;
                case KeyCode.M: return kb.mKey;
                case KeyCode.N: return kb.nKey;
                case KeyCode.O: return kb.oKey;
                case KeyCode.P: return kb.pKey;
                case KeyCode.Q: return kb.qKey;
                case KeyCode.R: return kb.rKey;
                case KeyCode.S: return kb.sKey;
                case KeyCode.T: return kb.tKey;
                case KeyCode.U: return kb.uKey;
                case KeyCode.V: return kb.vKey;
                case KeyCode.W: return kb.wKey;
                case KeyCode.X: return kb.xKey;
                case KeyCode.Y: return kb.yKey;
                case KeyCode.Z: return kb.zKey;

                case KeyCode.Alpha0: return kb.digit0Key;
                case KeyCode.Alpha1: return kb.digit1Key;
                case KeyCode.Alpha2: return kb.digit2Key;
                case KeyCode.Alpha3: return kb.digit3Key;
                case KeyCode.Alpha4: return kb.digit4Key;
                case KeyCode.Alpha5: return kb.digit5Key;
                case KeyCode.Alpha6: return kb.digit6Key;
                case KeyCode.Alpha7: return kb.digit7Key;
                case KeyCode.Alpha8: return kb.digit8Key;
                case KeyCode.Alpha9: return kb.digit9Key;

                case KeyCode.Space: return kb.spaceKey;
                case KeyCode.Return: return kb.enterKey;
                case KeyCode.KeypadEnter: return kb.numpadEnterKey;
                case KeyCode.Tab: return kb.tabKey;
                case KeyCode.Escape: return kb.escapeKey;
                case KeyCode.Backspace: return kb.backspaceKey;
                case KeyCode.Delete: return kb.deleteKey;
                case KeyCode.Insert: return kb.insertKey;
                case KeyCode.Home: return kb.homeKey;
                case KeyCode.End: return kb.endKey;
                case KeyCode.PageUp: return kb.pageUpKey;
                case KeyCode.PageDown: return kb.pageDownKey;
                case KeyCode.UpArrow: return kb.upArrowKey;
                case KeyCode.DownArrow: return kb.downArrowKey;
                case KeyCode.LeftArrow: return kb.leftArrowKey;
                case KeyCode.RightArrow: return kb.rightArrowKey;
                case KeyCode.LeftShift: return kb.leftShiftKey;
                case KeyCode.RightShift: return kb.rightShiftKey;
                case KeyCode.LeftControl: return kb.leftCtrlKey;
                case KeyCode.RightControl: return kb.rightCtrlKey;
                case KeyCode.LeftAlt: return kb.leftAltKey;
                case KeyCode.RightAlt: return kb.rightAltKey;
                case KeyCode.BackQuote: return kb.backquoteKey;

                default: return null;
            }
        }

        private void OnGUI()
        {
            OnScreenMessages.Draw();
        }
    }
}

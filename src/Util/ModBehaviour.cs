using System;
using MoonSweepersTwitch.Config;
using MoonSweepersTwitch.Game;
using UnityEngine;

namespace MoonSweepersTwitch.Util
{
    /// <summary>
    /// The one MonoBehaviour injected into IL2CPP. Gives the mod a per-frame
    /// Update (to drain the dispatcher and poll the test hotkey) and OnGUI (overlay).
    /// </summary>
    public sealed class ModBehaviour : MonoBehaviour
    {
        private PluginConfig? _config;
        private EffectRegistry? _effects;

        // Required constructor for IL2CPP-injected MonoBehaviours.
        public ModBehaviour(IntPtr ptr) : base(ptr) { }

        public void Initialize(PluginConfig config, EffectRegistry effects)
        {
            _config = config;
            _effects = effects;
            OnScreenMessages.Enabled = config.ShowOnScreenFeedback.Value;
        }

        private void Update()
        {
            MainThreadDispatcher.Drain();

            if (_config == null || _effects == null) return;

            var key = _config.TestEffectKey.Value;
            if (key != KeyCode.None && Input.GetKeyDown(key))
            {
                var effectKey = _config.TestEffectKeyName.Value;
                _effects.Trigger(effectKey, EffectContext.Test(effectKey));
            }
        }

        private void OnGUI()
        {
            OnScreenMessages.Draw();
        }
    }
}

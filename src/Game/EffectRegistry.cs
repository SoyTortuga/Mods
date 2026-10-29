using System;
using System.Collections.Generic;
using MoonSweepersTwitch.Util;

namespace MoonSweepersTwitch.Game
{
    /// <summary>A named, runnable in-game effect.</summary>
    public sealed class Effect
    {
        public string Key { get; }
        public string DisplayName { get; }
        private readonly Action<EffectContext> _run;

        public Effect(string key, string displayName, Action<EffectContext> run)
        {
            Key = key;
            DisplayName = displayName;
            _run = run;
        }

        public void Run(EffectContext ctx) => _run(ctx);
    }

    /// <summary>
    /// Holds every effect keyed by its config name and runs them on the main thread.
    /// Triggering is safe to call from any thread.
    /// </summary>
    public sealed class EffectRegistry
    {
        private readonly Dictionary<string, Effect> _effects =
            new Dictionary<string, Effect>(StringComparer.OrdinalIgnoreCase);

        public void Register(Effect effect) => _effects[effect.Key] = effect;

        public bool Contains(string key) => _effects.ContainsKey(key);

        public IEnumerable<string> Keys => _effects.Keys;

        /// <summary>
        /// Queue the effect to run on the Unity main thread. No-op (with a log) if the
        /// key is unknown so a typo in config never crashes the stream.
        /// </summary>
        public void Trigger(string key, EffectContext ctx)
        {
            if (string.IsNullOrWhiteSpace(key)) return;

            if (!_effects.TryGetValue(key, out var effect))
            {
                Plugin.Log.LogWarning(
                    $"No effect named '{key}'. Check your mappings. Known effects: " +
                    string.Join(", ", Keys));
                return;
            }

            MainThreadDispatcher.Enqueue(() =>
            {
                Plugin.Log.LogInfo($"Effect '{effect.Key}' <- {ctx.Describe()}");
                try
                {
                    effect.Run(ctx);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Effect '{effect.Key}' threw: {ex}");
                }
            });
        }
    }
}

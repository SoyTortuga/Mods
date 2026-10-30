using System;
using System.Collections.Generic;
using MoonSweepersTwitch.Util;

namespace MoonSweepersTwitch.Game
{
    /// <summary>Where an effect has to run for it to reach everyone it should.</summary>
    public enum EffectScope
    {
        /// <summary>Changes the player's own game (view, controls, movement, tools); runs on every player with the mod.</summary>
        Player,
        /// <summary>Changes shared world state the host owns (money, robots); runs on the host only.</summary>
        World,
    }

    /// <summary>A named, runnable in-game effect.</summary>
    public sealed class Effect
    {
        public string Key { get; }
        public string DisplayName { get; }
        public EffectScope Scope { get; }

        /// <summary>Full duration in seconds; 0 for an instant effect.</summary>
        public float Duration { get; }

        private readonly Action<EffectContext, float> _run;

        public Effect(string key, string displayName, EffectScope scope, float duration, Action<EffectContext, float> run)
        {
            Key = key;
            DisplayName = displayName;
            Scope = scope;
            Duration = duration;
            _run = run;
        }

        /// <summary>Run with the context's remaining time if it has one, else the full duration.</summary>
        public void Run(EffectContext ctx) => _run(ctx, ctx.Seconds ?? Duration);
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

        public bool TryGet(string key, out Effect effect) => _effects.TryGetValue(key, out effect!);

        public IEnumerable<string> Keys => _effects.Keys;

        /// <summary>
        /// Queue the effect to run on the Unity main thread. No-op (with a log) if the
        /// key is unknown so a typo in config never crashes the stream.
        ///
        /// An effect triggered here (not relayed) is also published to the crew. A World
        /// effect only runs on the host: triggered on another player's game, it is
        /// published for the host to run instead of running locally.
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

                bool runHere = effect.Scope == EffectScope.Player || CrewRelay.RunsWorldEffects;
                if (runHere)
                {
                    try
                    {
                        effect.Run(ctx);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"Effect '{effect.Key}' threw: {ex}");
                    }
                }
                else
                {
                    Plugin.Log.LogInfo($"Effect '{effect.Key}' changes the shared world, so the host runs it.");
                }

                if (!ctx.FromCrew) CrewRelay.Publish(effect, ctx);
            });
        }
    }
}

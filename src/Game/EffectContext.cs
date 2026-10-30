namespace MoonSweepersTwitch.Game
{
    public enum EffectSource
    {
        ChannelPoints,
        Bits,
        Test,
        /// <summary>Relayed from another player's game through the lobby (see <see cref="CrewRelay"/>).</summary>
        Crew,
    }

    /// <summary>
    /// Everything an effect might want to know about what triggered it.
    /// </summary>
    public sealed class EffectContext
    {
        public EffectSource Source { get; init; }

        /// <summary>Viewer display name (may be empty for anonymous cheers).</summary>
        public string UserName { get; init; } = "";

        /// <summary>Bits cheered, or the Channel Point cost of the reward.</summary>
        public int Amount { get; init; }

        /// <summary>Reward title (channel points) — empty for bits.</summary>
        public string RewardTitle { get; init; } = "";

        /// <summary>Free text the viewer attached: reward user input or cheer message.</summary>
        public string Message { get; init; } = "";

        /// <summary>
        /// Run a timed effect for this many seconds instead of its full duration. Set for
        /// relayed effects, which start with only the time the original has left.
        /// </summary>
        public float? Seconds { get; init; }

        /// <summary>
        /// An effect-specific detail shared with the crew, e.g. which sector refilldust
        /// picked so every player re-dusts the same one. An effect that picks such a
        /// detail sets it here so the relay can pass it on.
        /// </summary>
        public string? Arg { get; set; }

        /// <summary>True when this run was relayed from another player's game.</summary>
        public bool FromCrew => Source == EffectSource.Crew;

        public static EffectContext Test(string effectKey) => new EffectContext
        {
            Source = EffectSource.Test,
            UserName = "you (test)",
            Amount = 0,
            RewardTitle = effectKey,
            Message = "manual test",
        };

        /// <summary>Short human-readable description of the trigger, for logs/toasts.</summary>
        public string Describe() => Source switch
        {
            EffectSource.ChannelPoints => $"{UserName} redeemed \"{RewardTitle}\"",
            EffectSource.Bits => $"{UserName} cheered {Amount} bits",
            EffectSource.Crew => $"relayed from {UserName}",
            _ => "Test trigger",
        };
    }
}

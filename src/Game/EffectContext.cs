namespace MoonSweepersTwitch.Game
{
    public enum EffectSource
    {
        ChannelPoints,
        Bits,
        Test,
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
            _ => "Test trigger",
        };
    }
}

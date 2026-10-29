namespace MoonSweepersTwitch.Game
{
    /// <summary>
    /// ============================================================================
    ///  THE ONE FILE YOU FINISH YOURSELF.
    /// ============================================================================
    /// Every method here runs on the Unity MAIN THREAD and is where an effect
    /// actually changes the game. They currently only log, because this project was
    /// written without access to Moon Sweepers' compiled code, so the game's real
    /// class/field/method names are unknown.
    ///
    /// To make an effect do something:
    ///   1. In the game folder, after launching once with BepInEx + the patcher,
    ///      open  BepInEx\interop\Assembly-CSharp.dll  in a decompiler
    ///      (dnSpyEx / ILSpy / dotPeek). Find the relevant manager
    ///      (e.g. a PlayerController, DustManager, EconomyManager, etc.).
    ///   2. Uncomment the Assembly-CSharp reference in MoonSweepersTwitch.csproj.
    ///   3. Either call static singletons directly, or capture an instance via a
    ///      Harmony patch in GamePatches.cs and use it here.
    ///
    /// Each method shows, in comments, the typical shape of that code. Names like
    /// `PlayerController` are ILLUSTRATIVE — replace them with the real ones.
    /// ============================================================================
    /// </summary>
    public static class GameBridge
    {
        private static void NotImplemented(string what, EffectContext ctx)
        {
            Plugin.Log.LogInfo(
                $"[GameBridge] '{what}' fired ({ctx.Describe()}) — on-screen feedback only. " +
                $"Wire this up in GameBridge.cs to affect the game.");
        }

        public static void SpawnConfetti(EffectContext ctx)
        {
            NotImplemented(nameof(SpawnConfetti), ctx);
            // Example: Object.Instantiate(myConfettiPrefab, PlayerRef.Current.transform.position, Quaternion.identity);
        }

        public static void SetFog(EffectContext ctx, bool enabled, float seconds)
        {
            NotImplemented(nameof(SetFog), ctx);
            // Example (Unity built-in fog, usually safe without game code):
            //   UnityEngine.RenderSettings.fog = enabled;
            //   UnityEngine.RenderSettings.fogDensity = 0.04f;
            // then schedule it off again after `seconds` (see GamePatches timer note).
        }

        public static void DropDust(EffectContext ctx, int amount)
        {
            NotImplemented(nameof(DropDust), ctx);
            // Example: GamePatches.DustManager?.SpawnDirtAt(PlayerRef.Current.transform.position, amount);
        }

        public static void ShakeWorld(EffectContext ctx, float intensity, float seconds)
        {
            NotImplemented(nameof(ShakeWorld), ctx);
            // Example: find the active Cinemachine/camera shake component and pulse it,
            // or nudge the player camera transform over `seconds`.
        }

        public static void SetPlayerSpeedMultiplier(EffectContext ctx, float multiplier, float seconds)
        {
            NotImplemented(nameof(SetPlayerSpeedMultiplier), ctx);
            // Example: var p = GamePatches.LocalPlayer; if (p != null) { p.moveSpeed *= multiplier; ... revert later }
        }

        public static void GiveMoney(EffectContext ctx, int amount)
        {
            NotImplemented(nameof(GiveMoney), ctx);
            // Example: GamePatches.Economy?.AddMoney(amount);
            //
            // MULTIPLAYER NOTE: Moon Sweepers is co-op. A client-side edit like this
            // may not replicate to other players, or may be overwritten/rejected by
            // the host. Effects that change shared state generally need to run on, or
            // be requested from, the host. Visual/local effects (fog, confetti,
            // camera shake, your own movement) are the safest to start with.
        }
    }
}

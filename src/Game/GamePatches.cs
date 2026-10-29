namespace MoonSweepersTwitch.Game
{
    /// <summary>
    /// Optional home for Harmony patches that capture references to the game's own
    /// objects so <see cref="GameBridge"/> can use them.
    ///
    /// This is left as a template on purpose: the correct types come from the game's
    /// decompiled Assembly-CSharp, which this project does not ship. Once you know the
    /// real class names, the pattern below captures a manager instance the moment the
    /// game creates it.
    ///
    /// To activate:
    ///   1. Uncomment the Assembly-CSharp reference in MoonSweepersTwitch.csproj.
    ///   2. Add  using HarmonyLib;  and the game's namespace.
    ///   3. In Plugin.Load(), create and apply a Harmony instance:
    ///        var harmony = new HarmonyLib.Harmony(PluginInfo.Guid);
    ///        harmony.PatchAll(typeof(GamePatches).Assembly);
    ///   4. Replace the illustrative names below with the real ones.
    ///
    /// Example (names are PLACEHOLDERS):
    /// <code>
    /// public static PlayerController? LocalPlayer { get; private set; }
    /// public static EconomyManager? Economy { get; private set; }
    /// public static DustManager? DustManager { get; private set; }
    ///
    /// [HarmonyPatch(typeof(PlayerController), "Start")]
    /// public static class PlayerController_Start
    /// {
    ///     public static void Postfix(PlayerController __instance)
    ///     {
    ///         if (__instance.IsLocalPlayer) LocalPlayer = __instance;
    ///     }
    /// }
    ///
    /// [HarmonyPatch(typeof(EconomyManager), "Awake")]
    /// public static class EconomyManager_Awake
    /// {
    ///     public static void Postfix(EconomyManager __instance) => Economy = __instance;
    /// }
    /// </code>
    ///
    /// Timed effects (fog for 20s, speed boost for 30s) can revert themselves by
    /// starting a coroutine on the mod's ModBehaviour, or by scheduling an action on
    /// MainThreadDispatcher after tracking an end-time in Update.
    /// </summary>
    public static class GamePatches
    {
        // Intentionally empty until wired to the real game. See the doc comment above.
    }
}

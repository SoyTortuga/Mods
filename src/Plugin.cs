using System;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using MoonSweepersTwitch.Config;
using MoonSweepersTwitch.Game;
using MoonSweepersTwitch.Twitch;
using MoonSweepersTwitch.Util;
using UnityEngine;

namespace MoonSweepersTwitch
{
    /// <summary>
    /// Entry point. Owns configuration, the Twitch EventSub connection, and the
    /// single injected MonoBehaviour that pumps work onto Unity's main thread.
    /// </summary>
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        public static Plugin Instance { get; private set; } = null!;
        // 'new' because BasePlugin exposes an instance 'Log'; we want a static accessor.
        public static new ManualLogSource Log { get; private set; } = null!;

        private PluginConfig _config = null!;
        private EffectRegistry _effects = null!;
        private TwitchService? _twitch;
        private GameObject? _hostObject;

        public override void Load()
        {
            Instance = this;
            Log = base.Log;

            _config = new PluginConfig(Config);
            _effects = new EffectRegistry();
            Effects.RegisterBuiltIns(_effects, _config);
            CrewRelay.Init(_effects, () => _config.CrewRelayEnabled.Value);

            // Inject the MonoBehaviour that gives us Update()/OnGUI() on the main thread.
            ClassInjector.RegisterTypeInIl2Cpp<ModBehaviour>();
            _hostObject = new GameObject("MoonSweepersTwitch");
            UnityEngine.Object.DontDestroyOnLoad(_hostObject);
            _hostObject.hideFlags = HideFlags.HideAndDontSave;
            var behaviour = _hostObject.AddComponent<ModBehaviour>();
            behaviour.Initialize(_config, _effects);

            // Start Twitch on a background thread; it hands game effects back to the
            // main thread via MainThreadDispatcher. Twitch being off or unconfigured is not
            // fatal: the mod still loads, and the test hotkeys still work.
            if (!_config.TwitchEnabled.Value)
            {
                Log.LogInfo("Twitch is off ([Twitch] Enabled = false).");
            }
            else if (string.IsNullOrWhiteSpace(_config.ClientId.Value))
            {
                Log.LogWarning(
                    "No Twitch Client ID configured. The mod is loaded and the test " +
                    "hotkeys work, but Twitch events are disabled. Set [Twitch] ClientId " +
                    "in BepInEx\\config\\" + PluginInfo.Guid + ".cfg to connect.");
            }
            else
            {
                _twitch = new TwitchService(_config, _effects);
                _ = StartTwitchAsync();
            }

            Log.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} loaded.");
        }

        private async Task StartTwitchAsync()
        {
            try
            {
                await _twitch!.StartAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (TimeoutException)
            {
                Log.LogWarning(
                    "The Twitch sign-in code expired without being approved. Restart the game for a " +
                    "new code. Not the streamer? Set [Twitch] Enabled = false in BepInEx\\config\\" +
                    PluginInfo.Guid + ".cfg and this prompt stops.");
            }
            catch (Exception ex)
            {
                Log.LogError("Twitch service failed to start: " + ex);
                MainThreadDispatcher.Enqueue(() =>
                    OnScreenMessages.Show("Twitch connection failed — see BepInEx log.", 8f));
            }
        }

        public override bool Unload()
        {
            try
            {
                _twitch?.Dispose();
            }
            catch (Exception ex)
            {
                Log.LogWarning("Error during Twitch shutdown: " + ex.Message);
            }

            if (_hostObject != null)
            {
                UnityEngine.Object.Destroy(_hostObject);
                _hostObject = null;
            }

            return true;
        }
    }
}

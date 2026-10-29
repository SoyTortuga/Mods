# Moon Sweepers — Twitch Integration

A [BepInEx 6](https://docs.bepinex.dev/) mod for the Steam game **Moon Sweepers**
(Unity 6000.6 / IL2CPP) that lets your Twitch stream drive what happens in the
game. **Channel Point redemptions** and **Bits/cheers** trigger in-game effects.

It connects to Twitch with **EventSub over WebSocket** and authorizes with the
**Device Code flow**, so there's no server to host and no client secret to manage —
you just paste a Client ID and type a short code once.

---

## ⚠️ Status: read this first

The whole **Twitch side is complete and working**: OAuth, token refresh, EventSub
connection with auto-reconnect, channel-point and bits subscriptions, and routing
events to named effects via your config.

Four **chat sabotage effects are implemented** against the real game code (host-local,
tested in-game):

| Key | What it does |
|---|---|
| `breakdown` | Ejects the streamer from any vehicle and keeps them out of vehicles for 15 minutes. |
| `toothbrush` | Swaps the toothbrush into the brush slot and locks the toolbelt to it for 5 minutes, then gives the original brush back. If the game closes mid-effect, the brush is restored on the next load. |
| `emptydust` | Drops all held moon dust on the ground as 1 kg canisters (5 kg held = 5 canisters). |
| `refilldust` | Re-dusts a random sector the streamer has fully cleared, live, keeping its hex shape. |

The remaining template effects (`confetti`, `fog`, `dropdust`, `earthquake`,
`speedboost`, `slowmo`, `payout`) are still stubs that only log; to make one real,
finish its method in [`src/Game/GameBridge.cs`](src/Game/GameBridge.cs). See
[Wiring effects into the game](#wiring-effects-into-the-game).

There are two **test hotkeys** (F8 and F9 by default, each mapped to an effect in the
config) so you can fire effects without opening Twitch. On-screen toasts don't show on
this build of the game (its IMGUI is stripped), so check the BepInEx log to confirm an
effect fired.

---

## Requirements

- **Moon Sweepers** (Steam, Windows).
- **BepInEx 6, IL2CPP, win-x64** — bleeding-edge build (e.g. `6.0.0-be.791`).
  Stock BepInEx does **not** work on this game by itself (see below).
- **MoonSweepers BepInEx Patcher** — required because the game ships IL2CPP metadata
  v108 and BepInEx's bundled Cpp2IL only reads up to v106, so interop assemblies can't
  be generated without it.
- **.NET SDK 6.0+** to build the mod.
- A Twitch account that is the **broadcaster** (the channel you stream on).
- **Twitch Affiliate or Partner** status to create Channel Point rewards. (Bits also
  require Affiliate/Partner. Without it, the mod still loads and the test hotkey works.)

---

## Install BepInEx + patcher (one time)

1. Install **BepInEx 6 (IL2CPP, win-x64)** into the Moon Sweepers folder
   (where `Moon Sweepers.exe` lives). You should end up with a `BepInEx` folder,
   `winhttp.dll`, and `doorstop_config.ini` next to the exe.
2. Install the **MoonSweepers BepInEx Patcher** per its instructions (typically a DLL
   dropped into `BepInEx/patchers/`).
3. **Launch the game once and quit.** On this run BepInEx generates the interop
   assemblies into `BepInEx/interop/` (`Assembly-CSharp.dll`, `UnityEngine.*.dll`, …).
   A healthy run ends with `Chainloader startup complete` in
   `BepInEx/LogOutput.log`. (A Cpp2IL metadata warning can still appear even when the
   patcher is doing its job.)

> The game folder is usually:
> `C:\Program Files (x86)\Steam\steamapps\common\Moon Sweepers`

---

## Build the mod

1. Clone this repo.
2. Point the build at your game install, either:
   - copy `Directory.Build.props.user.example` to `Directory.Build.props.user` and set
     `<GameDir>`, **or**
   - pass it on the command line.
3. Build:

```bash
dotnet build src/MoonSweepersTwitch.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Moon Sweepers"
```

If `GameDir` is valid, the build copies `MoonSweepersTwitch.dll` into
`…\Moon Sweepers\BepInEx\plugins\MoonSweepersTwitch\` automatically. Otherwise copy it
there by hand.

> **Pin the BepInEx version.** The `BepInEx.Unity.IL2CPP` package version in
> [`src/MoonSweepersTwitch.csproj`](src/MoonSweepersTwitch.csproj) should match the
> BepInEx build you installed (check `BepInEx/core`). Packages come from the BepInEx
> NuGet feed, which is already configured in [`nuget.config`](nuget.config).

---

## Create a Twitch application (one time)

1. Go to the [Twitch Developer Console → Register Your Application](https://dev.twitch.tv/console/apps).
2. Name: anything. **OAuth Redirect URLs:** `http://localhost` (Device Code flow
   doesn't actually use it, but the console requires one). Category: *Game Integration*.
   Client Type: **Public**.
3. Copy the **Client ID**. You do **not** need the client secret.

---

## Configure

Run the game once with the mod installed to generate the config file, then edit:

```
…\Moon Sweepers\BepInEx\config\com.soytortuga.moonsweepers.twitch.cfg
```

Key settings:

| Section | Key | What it does |
|---|---|---|
| `Twitch` | `ClientId` | Your Twitch app Client ID. |
| `Twitch` | `EnableChannelPoints` / `EnableBits` | Turn each event source on/off. |
| `ChannelPoints` | `RewardMappings` | Reward → effect map. |
| `Bits` | `BitMappings` | Cheer-amount → effect map. |
| `General` | `ShowOnScreenFeedback` | Toggle the on-screen toasts. |
| `General` | `DebugLogging` | Log raw events, reward titles/ids, etc. |
| `General` | `TestEffectKey` / `TestEffectKeyEffect` | Hotkey + which effect it fires. |

**Mapping format.** Pairs are separated by `||`; each pair is `left=>effectKey`.

```ini
[ChannelPoints]
RewardMappings = Make it Foggy=>fog || Dump Dust=>dropdust || Shake the Moon=>earthquake

[Bits]
# When someone cheers N bits, the effect for the HIGHEST threshold <= N fires.
BitMappings = 1=>confetti || 100=>fog || 1000=>earthquake
```

- Channel Point rewards match by **title** (case-insensitive). To match by the reward's
  stable id instead, use `id:<reward-id>=>effectKey`. Turn on `DebugLogging` to print
  titles and ids when you redeem.
- Built-in effect keys: `breakdown`, `toothbrush`, `emptydust`, `refilldust` (working),
  plus the stubbed `confetti`, `fog`, `dropdust`, `earthquake`, `speedboost`, `slowmo`,
  `payout`.

---

## First-run authorization

1. Start the game with the mod + a `ClientId` set.
2. The BepInEx console (and an on-screen toast) print:
   `TWITCH SETUP: open https://www.twitch.tv/activate and enter code XXXXXXXX`
3. Open that URL **logged in as your broadcaster account**, enter the code, approve the
   requested scopes (`channel:read:redemptions`, `bits:read`).
4. You'll see `✅ Twitch connected!`. The token is cached in
   `BepInEx/config/com.soytortuga.moonsweepers.twitch.tokens.json` and auto-refreshes,
   so you only do this once (delete that file to re-authorize a different account).

Now redeem a mapped reward or cheer bits and watch the effect fire.

---

## Wiring effects into the game

Open [`src/Game/GameBridge.cs`](src/Game/GameBridge.cs) — it's the only file you need
to touch. Each method (e.g. `DropDust`, `GiveMoney`, `ShakeWorld`) currently just logs.
To make one real:

1. After the first BepInEx launch, decompile `BepInEx/interop/Assembly-CSharp.dll`
   (dnSpyEx / ILSpy / dotPeek) and find the relevant manager (player controller,
   economy, dust/dirt spawner, …).
2. Uncomment the `Assembly-CSharp` reference in
   [`src/MoonSweepersTwitch.csproj`](src/MoonSweepersTwitch.csproj).
3. Either call the game's static singletons directly, or capture an instance with a
   Harmony patch — [`src/Game/GamePatches.cs`](src/Game/GamePatches.cs) shows the
   exact pattern (and how to apply Harmony from `Plugin.Load`).
4. Fill in the method body. Add brand-new effects by registering them in
   [`src/Game/Effects.cs`](src/Game/Effects.cs).

Everything in `GameBridge` runs on the Unity **main thread**, so it's safe to touch
Unity objects there.

> **Stripped Unity APIs.** This IL2CPP build drops Unity methods the game never calls,
> and calling one throws "Method unstripping failed". In particular
> `Object.FindObjectOfType<T>()` is gone: use `GameBridge.FindFirst<T>()`, which falls
> back through lookups the build keeps. If a new effect seems to do nothing, check
> `BepInEx\LogOutput.log` for that message first.

> **Multiplayer caveat.** Moon Sweepers is co-op (up to 16 players). Client-side edits
> to **shared** state (money, spawned objects, world state) may not replicate to other
> players, or may be corrected by the host. Start with **local/visual** effects — fog,
> confetti, camera shake, your own movement speed — which are the safest. Shared-state
> effects generally need to run on, or be requested from, the host.

---

## Project layout

```
src/
  Plugin.cs                 BepInEx entry point; lifecycle & wiring
  PluginInfo.cs             GUID / name / version
  Config/PluginConfig.cs    BepInEx config bindings
  Twitch/
    TwitchService.cs        Orchestrates auth + EventSub + maps events -> effects
    TwitchAuth.cs           Device Code flow, token cache & refresh
    HelixApi.cs             helix/users + create EventSub subscriptions
    EventSubClient.cs       WebSocket lifecycle (welcome/keepalive/reconnect/revocation)
    StoredToken.cs          Persisted token model
  Game/
    Effects.cs              Built-in effect catalogue
    EffectRegistry.cs       Runs effects on the main thread
    EffectContext.cs        Who/what triggered an effect
    GameBridge.cs           >>> YOU FINISH THIS <<< real game manipulation
    GamePatches.cs          Harmony patch template for capturing game refs
  Util/
    ModBehaviour.cs         Injected MonoBehaviour: Update() + OnGUI()
    MainThreadDispatcher.cs  Background -> main-thread hand-off
    OnScreenMessages.cs     IMGUI toast overlay
```

---

## Troubleshooting

- **Mod doesn't load / no toast on F8** — check `BepInEx/LogOutput.log` for
  `Moon Sweepers Twitch Integration vX loaded`. If BepInEx itself isn't loading, the
  patcher step is likely missing.
- **`403` when subscribing to channel points/bits** — your account isn't
  Affiliate/Partner, or you declined a scope. Re-authorize (delete the `.tokens.json`).
- **Redemptions do nothing** — turn on `DebugLogging`, redeem, and copy the exact
  reward title (or `id:`) into `RewardMappings`.
- **`authorization timed out`** — you didn't finish the device code in time; reload the
  mod (or restart) to get a new code.

---

## How this maps to Twitch's API

- **Auth:** [Device Code Grant Flow](https://dev.twitch.tv/docs/authentication/getting-tokens-oauth/#device-code-grant-flow)
  — public client, no secret, refreshable.
- **Transport:** [EventSub over WebSocket](https://dev.twitch.tv/docs/eventsub/handling-websocket-events/).
- **Subscriptions:**
  [`channel.channel_points_custom_reward_redemption.add`](https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/#channelchannel_points_custom_reward_redemptionadd)
  (scope `channel:read:redemptions`) and
  [`channel.cheer`](https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/#channelcheer)
  (scope `bits:read`).

---

## Credits & disclaimer

Not affiliated with DragDrop Games or Twitch. Moon Sweepers has no official modding
API; this relies on the community BepInEx setup. Use at your own risk, and prefer local
effects in multiplayer so you don't desync or disrupt other players.

## License

[MIT](LICENSE).

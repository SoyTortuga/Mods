**Moon Sweepers: Twitch chat mod setup** 🌙

When chat buys a Power-up on stream, it hits **everyone in the lobby who has the mod**. If you don't install it, it won't affect you.

**Easiest: Vortex** (Nexus's mod manager)
1. Install Vortex from nexusmods.com, open it, and add **Moon Sweepers** from the Games tab.
2. Open the collection: <COLLECTION LINK> and click **Add to Vortex**.
3. Start the game. The first start takes about 15 seconds longer, which is normal.

**By hand**
1. Download BepInEx: https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip and extract it into the game folder (where `MoonSweepers.exe` is).
2. Download the patcher: https://www.nexusmods.com/moonsweepers/mods/9 and extract it into the same folder.
3. Download the mod: <MOD LINK> and extract it into `BepInEx\plugins`.
4. Start the game.

Game folder: right-click Moon Sweepers in Steam → Manage → Browse local files.

**You don't need Twitch.** A window may ask you to sign in to Twitch; ignore it. To turn that off, open `BepInEx\config\com.soytortuga.moonsweepers.twitch.cfg`, set `Enabled = false` under `[Twitch]`, and save.

Something wrong? Send me `BepInEx\LogOutput.log` from the game folder.

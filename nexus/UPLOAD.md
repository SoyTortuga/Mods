# Publishing on Nexus Mods

Everything to paste is in this folder:

| File | Where it goes |
|---|---|
| `dist\MoonSweepersTwitch-1.0.0.zip` | The mod's main file (build it with `tools\package.ps1`) |
| `mod-page.bbcode` | The mod page description |
| `power-ups.txt` | Titles, prices and descriptions for your Twitch Power-ups |
| `collection.txt` | The collection's name, summary, description and per-mod notes |
| `crew-guide.md` | A Discord post for your crew (under Discord's 2,000-character limit) |

## Before you publish

These haven't been tested yet:

- **Crew sharing:** you host, a friend joins with the mod, and effects hit both of you. Also check that Bankruptcy drains the money once and that Mercy ends everything for both of you.
- **Roomba Shutdown and J.O.O.N.A.S. Shutdown:** these need a save that has robots.
- **A live Power-up purchase:** someone else buys one on your channel. Twitch doesn't send the event when you use your own.

You can create the page now and keep it unpublished until these pass.

## 1. The mod page

1. On nexusmods.com, go to **Moon Sweepers** and click **Upload a mod**.
2. **Details**
   - **Name:** Moon Sweepers Twitch Integration
   - **Summary:** Let your Twitch chat sabotage the crew: Bits and Power-ups switch off lamps, flip the moon, crank gravity, spill dust and drain your money. Hits every player who runs the mod.
   - **Description:** paste `mod-page.bbcode`. If the editor shows formatted text, switch it to BBCode/source view first, or the tags will show up as plain text.
   - **Category:** Gameplay (or Utilities, if there's no Gameplay category).
   - **Version:** 1.0.0
   - **AI content:** if the form asks, say yes. Much of the code was written with an AI assistant, and Nexus labels pages like this "AI Assisted".
3. **Files:** upload `dist\MoonSweepersTwitch-1.0.0.zip` as the **Main file**, version 1.0.0.
4. **Requirements**
   - **Nexus requirement:** MoonSweepers BepInEx Patcher (mod 9).
   - **Off-site requirement:** BepInEx 6.0.0-be.791 (IL2CPP win-x64), at
     `https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip`
5. **Permissions:** the mod is MIT-licensed, so others may reuse it with credit. Choose the options that allow modification and reuse, with credit required.
6. **Images:** a screenshot of an effect running, for example Australian Moon or Moon Fog, makes a good main image.
7. Publish once the tests above pass.

## 2. The collection

You build collections in **Vortex**, from mods Vortex has installed.

1. **Back up your config first.** Copy `BepInEx\config` from the game folder somewhere safe. It holds your Twitch sign-in and your Power-up mappings.
2. Vortex has to install BepInEx, the patcher and the mod itself, so it can't use your hand-installed copies. Delete these from the game folder: `BepInEx`, `dotnet`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and `changelog.txt`.
3. In Vortex, open **Games**, find **Moon Sweepers** and click **Manage**. Vortex downloads ChemBoy1's extension for it.
4. Install all three:
   - **BepInEx:** download the zip from the link above and drag it onto Vortex's **Mods** page.
   - **Patcher:** on mod 9's Files tab, click **Mod Manager Download**.
   - **Your mod:** use **Mod Manager Download** on your own page. If the page isn't public yet, drag in the zip from `dist` instead.
5. Click **Deploy**, start the game once and check that it reaches `Chainloader startup complete` in `BepInEx\LogOutput.log`. Then copy your saved `config` folder back into `BepInEx`.
6. In Vortex, open **Collections**, choose **Create a collection**, and add the three mods.
7. On the collection's mods list, set **BepInEx**'s source to **Direct download** with the link above, since it isn't on Nexus. Also set your mod to come from your Nexus page rather than a local file.
8. Paste the per-mod notes from `collection.txt` into each mod's instructions.
9. **Upload**. This creates a draft on Nexus. Open it on the website, paste the name, summary and description from `collection.txt`, add an image, and publish.

## 3. Your crew

Put the collection and mod page links into `crew-guide.md` in place of `<COLLECTION LINK>` and `<MOD LINK>`, then post it in your Discord.

## Updating later

1. Change `Version` in `src\PluginInfo.cs`.
2. Run `tools\package.ps1`.
3. Upload the new zip as the main file and move the old one to **Old versions**.
4. Add a changelog entry on the page.
5. In Vortex, update the collection to the new version and upload a new revision.

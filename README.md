# OmniBonk

<p align="center">
  <img src="images/togetherButton.png" alt="logo" />
</p>

<!-- Shield -->

[![Contributors][contributors-shield]][contributors-url]
[![Download][download-shield]][download-url]
[![Forks][forks-shield]][forks-url]
[![Stargazers][stars-shield]][stars-url]
[![Issues][issues-shield]][issues-url]
[![GPL-2.0 License][license-shield]][license-url]

# About The Mod

OmniBonk brings online co-op to Megabonk, for up to 6 players.

This mod feature:

- Peer to peer online up to 6 players (Relay transport for ipv6 addresses or when ipv4 direct connection failed)
- Random and private queue
- Mostly (not all) in game mechanics synchronized
  - Weapons (also their updgrade)
  - Projectiles (not perfect for all)
  - Monsters (follow, target, attacks)
  - Chest
  - Shrines
  - Portal
  - Challenges
  - and probably other i forgot
- Death cam
- Custom balance to match the number of players (Probably unbalanced right now but it can be tuned later)
- Auto-update
- Probably won't work with other non cosmetic mods

More info at [Notable Network Changes](./NETPLAY_CHANGES.md)

## New in OmniBonk 1.0

These are new in 1.0. Each one is in the lobby's **Options** page.
**Only the host's choice counts**: it applies to everyone in the session.

### Endless Run (`Endless: ON`)

The run no longer ends at the tier's last stage. It carries on until everyone is dead.

- **The route.** Every portal leads to the next room on a shuffled route through Forest
  (stages 1–3), Desert (stages 1–3) and the Graveyard (the first crypt, the surface and the second
  crypt).
- **Boss rooms.** After every three rooms comes a boss room: the **Ent**, the **Pharaoh** with its
  pylons, or the **Ghost King**. Its exit leads on to the next room.
- **Starting.** Start the run on Forest, Desert or Graveyard.
- **Getting harder.** Difficulty follows how far the run has gone. A late "stage 1" room is not easy
  again. After every boss room, enemies get **×1.5 HP** and deal **×1.25 damage**, on top of the
  game's own difficulty that rises with time.
- **What carries over.** Your items, weapons, level, gold, chest prices and run stats (kills, time
  alive, damage) all carry across every room. The end screen covers the whole run.
- **Every tier.** Portals open after each stage boss on every tier, including Tier 1.
- **Everyone together.** The host picks the route, and every player always travels to the same room.

### No Projectile Cap (`No Proj. Cap: ON`)

The game fires at most **80 projectiles** in one burst and drops the rest. This lets a weapon fire
all of them.

- **When it matters.** Only very late in a run, once a weapon's Quantity is above 80. Before that it
  changes nothing.
- **Same for everyone.** The host applies it to the weapons it simulates for teammates too, so
  everyone sees the same volley.

### Lobby

- **Names.** Every player sees everyone's name in the lobby, not only the host's.
- **Options page.** Saves, Shared XP, No Proj. Cap and Endless.

## What's confirmed working (as of 2026-10-21)

Everything below has been **played and confirmed in sync**, in online runs with up to three
players, including Endless runs of 11 rooms. Things that are built but not yet confirmed are listed
separately, and are not counted as working.

**Combat, items and the damage panel**
- Teammates' attacks look the same on every screen: shield orbs, aura size, and projectile count and
  size all use the owner's stats
- Every item works for the player who owns it, with that player's own stats. That covers on-hit
  items, items that react to you being hit, and timed items. A teammate's item no longer fires for
  you, heals you, or uses your stats
- Burn, poison and Bloodmark count as the damage of the player who applied them
- The end-of-run damage panel shows only your own weapons and items, at their real levels. No more
  teammate rows at LVL 0
- Enemies stop taking damage while an encounter window has the game paused (shared experience)
- Enemy and boss max health is the same for everyone

**Enemies and bosses**
- Every player sees the same enemies. A client no longer keeps enemies that the host has already
  killed
- Minibosses from the stage's own timeline spawn near a random player, not always next to the host
- Bosses that come out of an object spawn at that object: Graveyard coffins (inside their barrier),
  Desert graves, bush and bandit challenges, and the Skeleton King statue
- Desert final boss: the boss moves for everyone, the pylons can be charged by everyone, and
  returned weapons go back to their real owner
- With two stage bosses, the portal waits until both are dead
- On the tier's last stage, no player gets a portal that the game never meant to exist

**Revives**
- A dead player's coffin appears on the ground where it can be reached, also on Desert hills
- The revived player comes back on the map, not outside it

**Spawning and the world**
- Ghost-item summons only appear for the player who owns the Ghost item
- The run's interactable counters agree between players
- Charge shrines have the same charge value for every player
- Magnet pickups work when a client collects them
- Desert tumbleweeds no longer spawn twice
- Falling off the final arena puts you back on it without desyncing anyone

**Shared experience (pause and rewards)**
- The failsafe no longer closes a window you are still using, and no longer freezes the run at an
  encounter
- XP is shared as gains, so players' levels stay the same
- Levels no longer drift apart at a stage change

**Lobby and runs**
- A player who dies and spectates comes back at the next level. Their death no longer ends the run
- Steam avatars show for every player in the lobby
- Character select no longer floods the log once a friend joins
- No more client error floods

**Built, waiting for a play test**
- No Projectile Cap's effect (needs a weapon with Quantity above 80)
- Lobby names for every player
- Re-hosting over Steam without the other player having to restart (needs two real players)

**Known and parked**
- Clients log `Look rotation viewing vector is zero` warnings while the host fires rockets. This is
  cosmetic only
- Teammates' HUD cards may show no weapon and tome icons. This dates from the original mod and has
  not been investigated

**Not tested yet**
- Every playtest so far was on a local network with no packet loss. Bad connections may still show
  problems that local tests cannot

The full per-issue history is in [`docs/netplay/08-observed-bugs.md`](docs/netplay/08-observed-bugs.md),
and the Endless design is in [`docs/netplay/18-endless-and-projectile-cap-plan.md`](docs/netplay/18-endless-and-projectile-cap-plan.md).

> [!IMPORTANT]
> **Everyone in a session must run the same version.** In the lobby, a player on a different
> version shows **WRONG VERSION**, and the host cannot start until they update. On the Steam transport, a lobby from a different version cannot be joined at all.

## Changelog

<details>
<summary>📋 Click to view full changelog</summary>

### OmniBonk v1.0.0

- 🔒 **Version check in the lobby.** A player on a different version shows WRONG VERSION, and the host cannot start until it is fixed
- ♾️ **Endless Run**, **No Projectile Cap** and the lobby **Options** page (see above)
- 🛠️ **Many sync fixes** (see "What's confirmed working" above)

</details>

# Install

OmniBonk is developed and tested on Windows. Linux and Steam Deck work through Proton; see
[Linux Support](#linux-support-proton--steam-deck) below. A major game update can break the mod
until it is updated.

> [!NOTE]
> Back up your save before installing. It is in `{user}/AppData/LocalLow/Ved/Megabonk`; copying
> that whole folder is the simplest way.

OmniBonk runs on the [BepInEx 6 Bleeding Edge build](https://builds.bepinex.dev/projects/bepinex_be).
Get the **IL2CPP, Windows, x64** build, for example
[BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.752](https://builds.bepinex.dev/projects/bepinex_be/752/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.752%2Bdd0655f.zip),
and extract it into the folder that holds `Megabonk.exe`, something like
`{your own path}/steamapps/common/Megabonk`.

- Launch the game once and wait for the main menu. BepInEx opens a console window next to the game
  and generates the files it needs.
- Close the game, then download the [latest release](https://github.com/CEOYap/OmniBonk/releases/latest).
- Extract the zip and copy the `OmniBonk` folder into `BepInEx/plugins`, something like
  `../steamapps/common/Megabonk/BepInEx/plugins`.
- Launch the game. An `OmniBonk` button, with the version underneath, appears at the bottom of the main menu.

> [!NOTE]
> The button cannot be selected with a controller yet. Click it with the mouse; you can switch back
> to the controller afterwards.

Found a bug? Open an [issue](https://github.com/CEOYap/OmniBonk/issues).

# Updating

## Updating OmniBonk

- **Automatic (GitHub install).** With `[Updates] CheckForUpdates = true` (the default), OmniBonk
  checks for a new release when the game starts and downloads it. The update is applied when you
  quit the game: a small window opens, waits for the game to close, then replaces the files. Start
  the game again and the changelog shows what changed.
- **By hand.** Close the game, download the [latest release](https://github.com/CEOYap/OmniBonk/releases/latest),
  and replace the `BepInEx/plugins/OmniBonk` folder with the one in the zip. Your settings in
  `BepInEx/config/OmniBonk.cfg` are kept.
- **Thunderstore or r2modman.** Update through the mod manager. That build still tells you when a
  new version is out, but does not download it.

Everyone in a session needs the same OmniBonk version; the lobby shows **WRONG VERSION** otherwise.

## When Megabonk itself updates

Steam updates the game on its own. On the first launch after a game update, BepInEx rebuilds the
files it generates for the game (`BepInEx/interop`), so that launch takes a minute longer on the
splash screen. Nothing else is needed if OmniBonk still works.

A game update can change what OmniBonk patches. If the game crashes, the OmniBonk button is
missing, or multiplayer misbehaves after a game update:

1. Check for a new OmniBonk release and update (above).
2. To play solo until then, move the `BepInEx/plugins/OmniBonk` folder out of `plugins`.
3. Everyone in a session also needs the same game version, so let Steam update every player's game.

# How to play

## Random

Play on a random queue, the next 6 players within a time period will get matched together (no pun intended)

## Friendlies

Private queue, Host as to share the room code (copy from the uper right button) and paste it to the players he want to play with

# Self-Hosting a Server

If you want to run your own matchmaking/relay server, check the [Self-Hosting Guide](docs/Setup-Own-Server.md).

# Known issue

- The game can rarely crash while a map loads. Restart the game if it happens.
- Not everything in the game is perfectly synchronized yet. Game-breaking bugs are fixed first.

# Building (Developer)

You need the [.NET SDK](https://dotnet.microsoft.com/download) (6.0 or later) and Git. Build from
a terminal (PowerShell on Windows) in the repository folder.

1. Clone the repository and go into it:

   ```powershell
   git clone https://github.com/CEOYap/OmniBonk.git
   cd OmniBonk
   ```

2. Point the build at your game install, so it compiles against your game's own files and copies
   the result into its `BepInEx/plugins/OmniBonk` folder. Run the game once with BepInEx first,
   so `BepInEx/interop` exists:

   ```powershell
   $env:MegabonkPath = "C:\Program Files (x86)\Steam\steamapps\common\Megabonk"
   ```

   This lasts for the current terminal only. Without it, the build uses the copies in
   `src/plugin/stripped-libs/` and copies nothing into the game.

3. Build the solution:

   ```powershell
   dotnet build OmniBonk.sln
   ```

   This builds the mod (`src/plugin/bin/Debug/net6.0/OmniBonk.dll`, with `OmniBonk.Common.dll`)
   and the matchmaking server (`src/server/bin/Debug/net6.0/OmniBonk.Server.dll`). Add
   `-c Release` for a release build; only Debug builds are copied into the game.

> [!IMPORTANT]
> A Debug build with `MegabonkPath` set overwrites `BepInEx/plugins/OmniBonk` in your game folder
> each time. Close the game before building, or the copy fails because the DLL is in use.

`[Network] ServerUrl` in `{your game path}/BepInEx/config/OmniBonk.cfg` defaults to `ws://127.0.0.1:5000`, a server on the same PC. It is only used when `[Network] UseSteamTransport` is `false`.

## After a game update (developers)

1. Launch the game once so BepInEx regenerates `BepInEx/interop` for the new game build.
2. Rebuild with `MegabonkPath` set (see above) and fix any compile errors: a renamed or removed
   game member shows up here.
3. Start the game and read `BepInEx/LogOutput.log` for Harmony errors. A patch whose target
   method changed fails when the plugin loads, not later.
4. Refresh the committed signature-only copies in `src/plugin/stripped-libs/`, so builds without a
   game install (CI) match the new game. From `src/plugin`, with `MegabonkPath` set and
   [`assembly-publicizer`](https://github.com/BepInEx/BepInEx.AssemblyPublicizer) installed
   (`dotnet tool install -g BepInEx.AssemblyPublicizer.Cli`), run `strip-dlls.bat`.
5. Game method addresses in the reverse-engineering notes are build-specific. Re-dump the new
   build before trusting one; see [`docs/reverse-engineering/00-decompilation-guide.md`](docs/reverse-engineering/00-decompilation-guide.md).

## Developer testing tools

Debug builds include tools for testing multiplayer alone on one PC, using extra copies of the game.
Full setup: [`docs/netplay/05-local-testing.md`](docs/netplay/05-local-testing.md).

| Tool | Setting | What it does |
|---|---|---|
| Test bot | `[Bot] BotMode = true` | That copy joins your lobby by itself, follows you, can't take damage, picks rewards, opens nearby chests and shrines, and revives you |
| Test loadout | `[Testing] TestItems = IceCube, CursedDoll*2, Cactus` | That copy is given these items when a multiplayer run starts, so a test covers the items you choose instead of whatever the chests roll |
| End-screen report | always on | Each copy writes its end-screen damage rows and kill count to `BepInEx/LogOutput.log` as `[report]` lines, so a run can be checked from the logs |

> [!NOTE]
> The test bot and the test loadout exist only in **Debug** builds. Releases are built in
> Release mode and leave them out entirely, so players can't turn them on by editing their
> config, and the settings don't appear in their config file. The end-screen report is in
> every build.

# Linux Support (Proton / Steam Deck)

The mod is compatible with Linux via **Proton**. Native Linux support is currently experimental and unstable due to BepInEx 6 compatibility issues with newer kernels (glibc/CET conflicts).

## Installation

1. **Configure Game for Proton:**
   - In Steam, right-click **Megabonk** -> **Properties** -> **Compatibility**.
   - Check "Force the use of a specific Steam Play compatibility tool".
   - Select **Proton 9.0** (or Experimental).

2. **Install BepInEx 6 (Windows x64):**
   - Download the latest `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.*` build from [BepisBuilds](https://builds.bepinex.dev/projects/bepinex_be).
   - Extract the contents directly into your game directory (`.../steamapps/common/Megabonk/`).
   - **Crucial:** You must see `winhttp.dll` next to `Megabonk.exe`.

3. **Configure Launch Options:**
   - In Steam Properties -> General -> Launch Options, add:
     ```bash
     WINEDLLOVERRIDES="winhttp=n,b" %command%
     ```
   - _This tells Proton to load the local `winhttp.dll` (BepInEx) instead of the system one._

4. **First Run:**
   - Launch the game once to let BepInEx generate the interop assemblies. This might take a minute on the splash screen.

5. **Install the Mod:**
   - Download the latest proton [release](https://github.com/CEOYap/OmniBonk/releases/latest) (in the release page, grab the -proton zip).
   - Extract the `OmniBonk` folder into `.../Megabonk/BepInEx/plugins/`.

After installation, your Megabonk directory should look like this:

```text
Megabonk/
├── BepInEx/
│   ├── core/
│   ├── config/
│   ├── interop/             <-- Generated on first run
│   ├── unity-libs/          <-- Generated on first run
│   └── plugins/
│       └── OmniBonk/
│           ├── OmniBonk.dll
│           ├── OmniBonk.Common.dll
│           └── (other dependency DLLs)
├── Megabonk_Data/
├── winhttp.dll
├── doorstop_config.ini
├── Megabonk.exe
├── GameAssembly.dll
└── UnityPlayer.dll
```

## Building on Linux

You can build the mod natively on Linux using the .NET 8 SDK.

- Ensure `dotnet-sdk-8.0` is installed.
- Clone the repository.
- Run the build script:

  ```bash
  ./scripts/proton/build.sh
  ```

- A `Directory.Build.props` file can be used to override the `MegabonkPath` if your game is installed elsewhere.

```xml
<Project>
  <PropertyGroup>
    <MegabonkPath>/home/user/.local/share/Steam/steamapps/common/Megabonk</MegabonkPath>
  </PropertyGroup>
</Project>
```

# License

OmniBonk is distributed under the GPL-2.0 (see [LICENSE](LICENSE)). It is based on
[Megabonk Together](https://github.com/Fcornaire/megabonk-together) by Fcornaire.

<!-- MARKDOWN LINKS & IMAGES -->
<!-- https://www.markdownguide.org/basic-syntax/#reference-style-links -->

[contributors-shield]: https://img.shields.io/github/contributors/CEOYap/OmniBonk.svg?style=for-the-badge
[contributors-url]: https://github.com/CEOYap/OmniBonk/graphs/contributors
[forks-shield]: https://img.shields.io/github/forks/CEOYap/OmniBonk.svg?style=for-the-badge
[forks-url]: https://github.com/CEOYap/OmniBonk/network/members
[stars-shield]: https://img.shields.io/github/stars/CEOYap/OmniBonk.svg?style=for-the-badge
[stars-url]: https://github.com/CEOYap/OmniBonk/stargazers
[issues-shield]: https://img.shields.io/github/issues/CEOYap/OmniBonk.svg?style=for-the-badge
[issues-url]: https://github.com/CEOYap/OmniBonk/issues
[license-shield]: https://img.shields.io/github/license/CEOYap/OmniBonk.svg?style=for-the-badge
[download-shield]: https://img.shields.io/github/downloads/CEOYap/OmniBonk/total?style=for-the-badge
[download-url]: https://github.com/CEOYap/OmniBonk/releases
[license-url]: https://github.com/CEOYap/OmniBonk/blob/main/LICENSE

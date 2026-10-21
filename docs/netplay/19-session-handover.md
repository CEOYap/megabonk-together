# Session handover 19: release readiness, and the rename to OmniBonk 1.0

Written 2026-10-21 to start a fresh session on what comes next. It covers three things:

- where the project stands after the 2026-10-19 to 2026-10-21 playtest cycle;
- what still blocks a release;
- what the planned rename to **OmniBonk**, version **1.0**, involves.

Nothing here has been released. This file was merged to `main` together with the branch
`claude/ob11-spawn-position` as a fast-forward. **Not pushed.**

---

## 1. Where things stand

### Bug log (`08-observed-bugs.md`)

Every fixed entry is **confirmed in play**, except:

| Entry | Status | Why still open |
|---|---|---|
| OB-33: Steam re-host needs a restart | Fixed, unconfirmed | Needs Steam transport and a real second player; bots cannot test it |
| OB-54: client Ghost King spawn threw; shrine finish effects sent as spawns | Fixed, unconfirmed | Fixed after the last run |
| OB-45 | Test tooling | Not a bug |

OB-3 and OB-23 were closed on 2026-10-21 because neither has reappeared.

### Features added in this cycle

All four are in [`18-endless-and-projectile-cap-plan.md`](18-endless-and-projectile-cap-plan.md).

| Feature | State |
|---|---|
| Lobby settings: message `LobbySettings` (tag 80), host decides, Options page toggles | Confirmed in play |
| No Projectile Cap: lifts the 80-projectile ceiling per volley | Built. The effect needs Quantity above 80, which a run has not reached yet |
| Endless Run, Forest, Desert and Graveyard: route via `EndlessNextRoom` (tag 81), three rooms then a boss | Confirmed in play, several 11-room runs |
| Endless scaling: balance follows the run; ×1.5 enemy HP and ×1.25 enemy damage per boss loop | Built; partly seen (`Endless loop 1, enemy damage x1.25` logged) |
| Lobby names: `LobbyRoster` (tag 82) | Built, unconfirmed |
| Options page layout: buttons under the title | Seen in play |

**Union tags 80 to 82 are new.** A peer on 5.1.0 cannot read them, and there is no version check
between peers (see section 3).

### The last full log review (2026-10-21, 17:23 run: host plus one bot, 11 stage changes)

The bot's log is clean, except for known noise:

- duplicate NAT introductions;
- the Steam init failure when the game is launched outside Steam;
- `Could not find original InventoryHud or itemContainerPrefab`. This has been in the code since
  the original upstream commit: the teammate HUD cards find no `InventoryHud` to copy, so they
  likely show no weapon and tome icons. Not investigated.

**One open item: 9 Unity `NullReferenceException`s on the host.** All of them come right as an
encounter window opens (`[pause] encounterWindow=True`), at 3 of 12 encounters, in one room. The
run carried on: every barrier was released and every digest stayed in sync. **There is no stack
trace.** All copies of the game on one PC share
`C:/Users/Godking/AppData/LocalLow/Ved/Megabonk/Player.log`, and whichever copy starts last
overwrites it. To catch the trace, copy the host's Player.log before the bots start, or start the
host last.

---

## 2. What blocks a release

**Done:**

- **CI-style build.** A Release build against the committed stubs succeeds (`CI=true MegabonkPath=
  dotnet build src/plugin/MegabonkTogether.Plugin.csproj -c Release`); that is what
  `build-plugin.yml` does. The only warnings are old `CS0618` ones in `CameraSwitcher.cs`.
- **Debug-only tools.** Bot mode, `BotInvincible` and the `TestItems` loadout are
  behind `#if DEBUG`, so the Release DLL carries none of them.
- **Diagnostics.** Every `[Diagnostics]` option (`LogBandwidth`, `LogUnityStackTraces`, and the
  others) defaults to off. The test configs have them on locally; a fresh install does not.

**Still to do:**

- **Version files: none bumped.** `<Version>` is still `5.1.0`, which is already tagged and
  released. Releasing means updating three files together and tagging (`CLAUDE.md`, "Releasing").
  Under the rename this becomes 1.0; see section 3.
- **No test under packet loss.** Every playtest ran a host and bots on one PC at 0% loss, and
  CLAUDE.md says netcode bugs are mostly invisible there. A proposed task adds a Debug-only switch
  for loss and latency, using LiteNetLib's own simulation:
  - `[Testing] SimulatedPacketLossPercent`;
  - `SimulatedLatencyMs`.

  At least one Endless run at 2 to 5% loss is worth doing before release.
- **The host runs an older BepInEx.** The host install has BepInEx **6.0.0-be.755**; the bot
  installs have **be.785**. Different builds generate different interop. That is the likely reason
  `RectTransform.GetWorldCorners` compiled but was missing at runtime on the host. Put every test
  install on the same BepInEx build.

---

## 3. The rename to OmniBonk 1.0

### Status, 2026-10-21: done on branch `claude/omnibonk-1.0`, not run in game, not tagged

The project owner's decisions on the five items below:

| # | Decision | What was done |
|---|---|---|
| 1. GUID | **New GUID, `OmniBonk`** | `<AssemblyName>` and `<Product>` are `OmniBonk`, so `PLUGIN_GUID`, the config file (`OmniBonk.cfg`), the Harmony id and the DLL all follow. `[BepInIncompatibility("MegabonkTogether")]` stops it loading next to the old plugin. `<RootNamespace>` and the `Common` assembly keep `MegabonkTogether`. |
| 2. Updater | **A new `CEOYap/OmniBonk` repo** | `AutoUpdaterService` reads releases there and expects `OmniBonk-X.Y.Z.zip`. **The repo does not exist yet**: until it does, the update check logs a failed fetch and nothing else. The updater batch also backed up a `MegabonkTogether.Plugin.dll` that never existed; it now names the real DLL. |
| 3. Version | **1.0.0** | Works because of 1: a separate plugin, so 1.0.0 is never compared with 5.1.0. |
| 4. Server | **Self-host, URL to follow** | `ServerUrl` still defaults to upstream's server. Change the default in `ModConfig.cs` once the URL exists. |
| 5. Mixed versions | **Version handshake** | `ProtocolHello`, union tag 83; `Protocol.Version` 2 → 3. The host cannot start the lobby until every client has confirmed protocol 3. See `01-critical-fixes.md`, P1-3, last section. |

Also renamed: the four workflows (zip and folder names, workflow names that chain on each other),
the Thunderstore package (`CEOYap-OmniBonk`; **the namespace must match the owner's Thunderstore
team, and is a guess**), `CHANGELOG.toml` (restarted at 1.0.0), `README.md`, the main-menu version
label (`OmniBonk v1.0.0`), the changelog modal title, and `CLAUDE.md`. Kept on purpose: the
`TOGETHER!` main-menu button, which says what it does rather than naming the mod, and the
`MegabonkTogether` localization table key, which no player sees.

**Left to do before tagging 1.0.0:** create the `CEOYap/OmniBonk` repo (or rename this one) and push;
set the Thunderstore namespace; a playtest with the handshake, including one peer on 5.1.0 to see
WRONG VERSION and a disabled Start; the 2–5% packet-loss run from section 2. On every test install,
delete `BepInEx/plugins/MegabonkTogether` first, or OmniBonk will refuse to load.

The original plan follows, unchanged.


The project owner plans to release this fork as **OmniBonk**, version **1.0**. Most of the rename
is find-and-replace. The items below need a decision first.

### Decisions needed before renaming

1. **Plugin GUID.**
   - `MyPluginInfo.PLUGIN_GUID` comes from the csproj and names three things:
     - the BepInEx config file (`BepInEx/config/<GUID>.cfg`);
     - the Harmony instance (`Plugin.cs`, `new Harmony(MyPluginInfo.PLUGIN_GUID)`);
     - the plugin itself.
   - A new GUID means players' existing `MegabonkTogether.cfg` is not read, and every setting is
     back at its default.
2. **GitHub owner and repo for the auto-updater.**
   - `AutoUpdaterService.cs` line 59 has `GITHUB_OWNER = "Fcornaire"` and
     `GITHUB_REPO = "megabonk-together"`. That is the **upstream** repository, not this fork
     (`CEOYap/megabonk-together`).
   - It reads `releases/latest` there, so OmniBonk would be offered upstream's releases until this
     changes.
3. **Version numbering.**
   - 1.0 is lower than the 5.1.0 players have installed, and `IsNewerVersion` compares numbers.
   - It only works if OmniBonk is a separate plugin: its own GUID, and its own updater target with
     its own releases. Then there is no older or newer between the two.
   - Decide whether a player should run both plugins at once. They must not: both would patch the
     same game methods.
4. **The matchmaking server.**
   - The default `ServerUrl` is `wss://megabonk-together-matchmaking.balatro-vs-matchmaking.eu`,
     which is **upstream's** server.
   - Either keep using it, which means OmniBonk players can be matched with Megabonk Together
     players, or self-host. The repo has the server (`src/server/`), a `Dockerfile` and
     `docs/Setup-Own-Server.md`.
5. **Mixed versions.**
   - Nothing checks that peers run the same mod version, and tags 80 to 82 are new.
   - A 5.x player in an OmniBonk session would ignore the lobby settings, the Endless route and the
     roster, so their game drifts from everyone else's without any error.
   - Separate matchmaking (item 4) prevents this. A version handshake (a new message) would make
     it fail loudly in every case.

### Mechanical, once those are decided

| Where | What |
|---|---|
| `src/plugin/MegabonkTogether.Plugin.csproj` | `<AssemblyName>`, `<Product>`, `<Version>` (1.0.0). `<RootNamespace>` is optional: renaming the C# namespace touches every file for no player-visible gain, so keeping `MegabonkTogether.*` internally is a reasonable choice. |
| `.github/workflows/*.yml` (4 files) | DLL and zip names, release titles |
| `src/plugin/CHANGELOG.toml` | Shown in game, and the version history restarts; write it for players |
| `README.md`, `THUNDERSTORE_BUILD.md`, `CLAUDE.md`, `docs/` | Project name, links, install paths (`BepInEx/plugins/MegabonkTogether`) |
| Main menu | `MainMenu.cs` shows `v{PLUGIN_VERSION}`, and any branding text |
| Thunderstore | A new package name; it is published separately from the GitHub release |
| `MegabonkPath` post-build copy | Copies into `BepInEx/plugins/MegabonkTogether`; follows the assembly name |

### Licence

The project is **GPL-2.0** (`LICENSE`). A renamed fork must stay GPL-2.0 and keep crediting the
original project and its author. That does not conflict with the repo rule against naming the
MelonLoader research mod; that rule is about that mod, not this project's upstream.

---

## 4. Test setup, for whoever runs the next playtest

| Copy | Path | Role |
|---|---|---|
| Host | `C:\Program Files (x86)\Steam\steamapps\common\Megabonk` | Steam install. A Debug build auto-copies here through `MegabonkPath` |
| Player2 | `D:\Games\Megabonk` | Bot, `BotInvincible = false` |
| Player3 | `D:\Games\Megabonk - Copy` | Bot, `BotInvincible = true` |

- **Copying the DLL.** After a Debug build, copy `src/plugin/bin/Debug/net6.0/MegabonkTogether.dll`
  into both bot folders under `BepInEx/plugins/MegabonkTogether/`.
- **Bot loadouts.** Both bots carry Bloody Cleaver and Unstable Transfusion in `[Testing]
  TestItems`, to exercise the debuff attribution.
- **Starting Endless.** Options page → `Endless: ON`, then start on Graveyard, Forest or Desert.
- **Log lines to watch:**
  - `[endless] Stage change N`;
  - `[endless] Boss loop N cleared`;
  - `[GameBalance] ... (balanced as S)`;
  - `[lobby-settings]`.

# Local Testing — Two Clients, One PC, One Steam Account

How to run a two-player netplay session on a single machine, for iterating on fixes without a
second person or a second Steam account.

---

## Why this works

**Netplay does not touch Steam.** `grep -rn "SteamUser\|CSteamID\|SteamAPI" src/plugin/Services/
src/plugin/Scripts/` returns nothing — identity is the mod's own `ConnectionId` plus the
`PlayerName` config value, and the transport is LiteNetLib UDP with a WebSocket matchmaker.
One Steam account is therefore not a constraint.

> **This changes if the Steamworks migration lands.** Once identity moves to `CSteamID`, two
> instances on one account will share an identity and this setup stops being representative.
> See [`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md).

**Ports resolve themselves.** `UdpClientService` starts at `27015` and increments until it finds
a free port (`UdpClientService.cs:134-153`), so the second instance takes `27016` with no
configuration. The log line `UDPClient listening on port N` confirms which it got.

---

> **Debug builds only.** The test bot (`[Bot] BotMode`), its room-code file and the item loadout
> (`[Testing] TestItems`) are wrapped in `#if DEBUG`. Every release workflow builds `-c Release`,
> so a shipped build has none of it, and a player cannot switch it on by editing their config
> (`TestItems` would otherwise hand out items in a public lobby, and the bot is invincible). The
> test copies need a Debug build, which is what the `MegabonkPath` auto-copy produces. The
> end-of-run `[report]` lines are not developer-only and stay in every build.

## One-time setup

### 1. Second game copy

Steam refuses to launch the same appid twice, so the second instance runs from its own folder,
launched directly. The install is ~669 MB.

```powershell
$src = "C:\Program Files (x86)\Steam\steamapps\common\Megabonk"
$dst = "D:\Games\Megabonk - Copy"
Copy-Item $src $dst -Recurse
```

Both copies keep their own `BepInEx/config/MegabonkTogether.cfg`, which is the point — it is how
each instance gets its own `PlayerName`.

### 2. Local matchmaking server

```powershell
dotnet run --project src/server/MegabonkTogether.Server.csproj
```

Listens on `http://127.0.0.1:5000` (WebSocket matchmaking) and UDP `5678` (relay / NAT
introduction). Leave it running in its own terminal; its console output is useful — it logs
match creation and peer joins.

### 3. Point both instances at it

In **each** copy's `BepInEx/config/MegabonkTogether.cfg`:

```ini
[Network]
ServerUrl = ws://127.0.0.1:5000
# Required: the directly-started copy has no Steam, so both copies must use the matchmaker.
UseSteamTransport = false

[Player]
# Must differ between the copies (e.g. Host / Client2), or the lobby is unreadable.
PlayerName = Host

[Updates]
# Stops the auto-updater replacing your test build.
CheckForUpdates = false
```

`CheckForUpdates = false` matters: `AutoUpdaterService` runs at startup and can overwrite the DLL
you are trying to test.

Note `ws://`, not `wss://` — there is no TLS on a local server.

### 4. Optional: let a test bot play the second copy

In the **second** copy only, add:

```ini
[Bot]
BotMode = true
```

Run that copy windowed at a low resolution. When you host Friendlies in your own copy, it writes the
room code to `%TEMP%\MegabonkTogether-bot-roomcode.txt`; the bot copy reads it on the main menu,
joins, readies up and picks a random character by itself, then follows you through the run. It logs
a `[bot]` status line every 10 seconds. Design:
[`../superpowers/specs/2026-10-19-test-bot-design.md`](../superpowers/specs/2026-10-19-test-bot-design.md).

---

## Each test run

### Deploy the build to both copies

`MegabonkPath` auto-copies Debug output to the **primary** install only. The second copy needs
syncing, so a build-and-deploy step looks like:

```powershell
dotnet build src/plugin/MegabonkTogether.Plugin.csproj -c Debug
Copy-Item "$env:MegabonkPath\BepInEx\plugins\MegabonkTogether\*" `
          "D:\Games\Megabonk - Copy\BepInEx\plugins\MegabonkTogether\" -Recurse -Force
```

Forgetting the second half means testing two different builds against each other, which produces
symptoms that look exactly like desync bugs. **If a result makes no sense, verify both copies
have the same DLL timestamp first.**

### Launch

Steam must be **running**. Launch the **primary copy through the Steam UI** and the second copy by
running its executable directly — Steam refuses to launch the same appid twice, which is the whole
reason the second copy exists.

```powershell
# second copy only; launch the first from Steam
& "D:\Games\Megabonk - Copy\Megabonk.exe"
```

`winhttp.dll` / Doorstop sits in each folder and loads with the process, so BepInEx injects
regardless of how the game was started.

**The two instances are not equivalent, and the log says which is which.** A direct-exe launch logs
`[Steamworks.NET] SteamAPI_Init() failed` and runs with Steam uninitialised; a Steam launch logs
`[Message:     Unity] Steam initialized`. It makes no difference today — nothing in the mod touches
Steam, which is exactly why it is easy to miss — but it is not a harmless difference from the
Steamworks migration onward, where Phase 2's rule is "verify Steam is already initialised by the
game; **do not** call `SteamAPI.Init()`". That assumption is false in a direct-exe launch.

**So this two-instance setup cannot test anything Steam-dependent**, and after Phase 2 it stops
being a valid harness at all — the second instance has no Steam, and both would share one identity
anyway. Noted again under *What this setup cannot tell you*.

### Connect

Use **Friendlies** (the private-code queue) rather than random matchmaking — it is deterministic
and does not depend on who else is online. One instance hosts and shows a code; the other joins
with it.

### Watch the logs

Each copy writes its own `BepInEx/LogOutput.log`. Tail both:

```powershell
Get-Content "C:\Program Files (x86)\Steam\steamapps\common\Megabonk\BepInEx\LogOutput.log" -Wait -Tail 20
Get-Content "D:\Games\Megabonk - Copy\BepInEx\LogOutput.log" -Wait -Tail 20
```

### Turn on Unity log capture — on **both** copies

`LogOutput.log` does **not** contain Unity-sourced lines by default. BepInEx 6 has two
independent gates with opposite defaults, and the one that matters is not the one whose name
suggests it:

| Key | Section | Default | What it does |
|---|---|---|---|
| `UnityLogListening` | `[Logging]` | **true** | admits Unity messages into BepInEx as a source named `Unity` |
| `WriteUnityLog` | `[Logging.Disk]` | **false** | writes those messages to `LogOutput.log` |

Set `WriteUnityLog = true` in `BepInEx/config/BepInEx.cfg` on every copy you intend to compare,
then relaunch. Without it you get no `NullReferenceException` lines, no `Look rotation viewing
vector is zero`, and no Unity stack traces — from *that* copy only.

**This cost three sessions.** A host logged zero Unity-sourced lines while its client logged
thousands; because `UnityLogListening` read `true` on both, the difference was blamed on the
peers running different BepInEx builds (be.755 vs be.785) and every "the host does not have
this" claim in `docs/netplay/` was built on it. The builds were irrelevant — the host was simply
at the stock `WriteUnityLog = false`.

The plugin now reports this at startup so it cannot recur silently. Check for the line before
trusting any comparison between two logs:

```
[Info   :MegabonkTogether] [log-capture] BepInEx 6.0.0-be.785; [Logging.Disk] WriteUnityLog = true. Unity-sourced lines WILL appear in LogOutput.log.
```

A `[log-capture]` **warning** instead means that copy is not capturing Unity lines, and an
absence of Unity errors in its log is not evidence of anything.

### What a healthy barrier looks like

Both barriers log their success paths, not only their failures, so a run can be shown to have worked
rather than merely not complained:

```
[readiness] Round 1 open over 2 participant(s): <id>, <id>
[readiness] <id> ready (2/2) for round 1.
[barrier]   Report from <id> accepted for round 1; barrier closable: True.
[barrier]   Released round 1 (session <n>) to all clients.
[barrier]   Applied release for round 1 (session <n>).
```

Round numbers should increment and never repeat. `[barrier] Ignoring a stale release` and
`[readiness] Dropped a report` are the round-identity mechanism *working* when they appear once
after a hiccup, and a problem when they repeat.

---

## Hands-off session (no one at the keyboard)

`scripts/test/run-local-session.ps1` runs a whole two-copy session by itself: one copy is an
**auto-host** (`[Bot] AutoHost = true` on top of `BotMode`), the other the usual follow bot.

```powershell
pwsh scripts/test/run-local-session.ps1 -Minutes 12 -StageMinutes 4 -ClientItems "SoulHarvester, EnergyCore"
```

What it does, in order:

1. Builds Debug (skip with `-NoBuild`) and copies the output into **both** copies' `plugins\OmniBonk`,
   checking the hash, so the two copies cannot run different builds.
2. Backs up each copy's `OmniBonk.cfg` and `BepInEx.cfg` (`*.session-backup`) and writes the test
   settings: `BotMode`, `AutoHost` (host only), `TestItems`, `PlayerName`, `UseSteamTransport = false`,
   `CheckForUpdates = false`, `AllowSavesDuringNetplay = false`, `WriteUnityLog = true`.
   `EnabledSharedExperience` is written off unless `-SharedXp`; `-SkipChestAnimation` sets the lobby
   setting (OB-65, `[chest-skip]` counts no-window chests on each peer); `-OpenPauseMenu` has each
   client bot open the pause menu every 15 s and leave it (OB-64, `[pause-menu]` when a reward closes it);
   `-SeekShadyGuy` (OB-67, `[shady-guy]`): from 3 min in, the client bot teleports to each Shady Guy and
   shops; the host bot leaves a Shady Guy's shop unbought until the client has bought there, then
   teleports back for a solo shop.
3. Starts the matchmaking server if nothing listens on port 5000.
4. Launches both copies windowed (960×540) from their exe, host first.
   `-HostItems`/`-ClientItems` and `-HostWeapons`/`-ClientWeapons` (`[Testing] TestWeapons`, EWeapon
   names such as `Mine, Rockets`) give each copy a loadout at run start. The host fires every
   weapon, so its weapons are what the client receives.
5. After `-Minutes`, closes both, restores the configs, and copies both logs plus a `summary.txt`
   into `artifacts\local-session\<timestamp>` (gitignored).

A run killed before step 5 leaves its `*.session-backup` files; the next run restores them first.

**What the auto-host does** (`Scripts/Bot/BotController.AutoHost.cs`): opens a lobby through the
Play Together button (the room code reaches the bot through the room-code file), readies, presses
Start once `AutoHostPlayers` are in and ready, picks a random character, selects `AutoHostMap`
(default **Desert**: the fewest obstacles, platforms and puzzles for a bot; crypt rooms and Forest
trap it) by clicking that map's `MapEntry` button, confirms, then
wanders the stage and uses chests and shrines like the bot. After `AutoHostStageMinutes` it opens
the stage's portal (`InteractableBossSpawner.portal`, which the game opens when the boss dies),
walks or teleports to it and presses interact through `BotInput`, so the stage change goes through
the mod's normal interact sync. It logs `[bot] on stage N`, `stage time up`, `entering the portal`.

**Two traps found on the first runs:**

- Both copies share one Megabonk save folder (`LocalLow\Ved\Megabonk\Saves`); closing them together
  collided on `stats.json`. Hence `AllowSavesDuringNetplay = false` for test sessions.
- **An old `MegabonkTogether` plugin anywhere under `plugins` stops OmniBonk loading** (`Could not
  load [OmniBonk 1.0.0] because it is incompatible with: MegabonkTogether`), by design. A build from a
  worktree cut before the rename auto-copies one there through `MegabonkPath`. The script refuses to
  start while one is present.

**Seeing the game without a person:** `scripts/test/capture-game-windows.ps1` saves a PNG of each
running copy (PrintWindow, so the windows need not be in front). The logs say what code ran; a
capture shows which map the run is on, or a host pushing into a wall.

**Three more traps from the first runs:**

- `EndlessRun` on starts in crypt rooms, which have no boss portal; the script turns it off.
- The map screen keeps the last map played; without `AutoHostMap` a run starts wherever the player's
  copy last was.
- `GetComponentsInChildren<T>(bool)` compiles against the stubs but throws `MissingMethodException`
  in game; use `Il2CppFindHelper.RuntimeGetComponentsInChildren<T>`.
- The compile-time `UnityEngine` stubs do not derive from `Il2CppObjectBase`, so `component.Pointer`
  or `TryCast` on a `Component` does not compile. At run time the wrapper is one: cast through
  `object`, as `ReceivedProjectileDiagnostics` does to read a component's real class name.

What it cannot do: judge visuals, or test anything Steam-dependent (see below).

## Letting a bot die

`[Bot] BotInvincible` (default `true`) controls whether a `BotMode` copy can take damage. Set it to
`false`, then restart that copy, to let the bot die. That makes a bot run able to reach game over and
the end-screen `[report]`, and it exercises a bot's own death and revive. It is Debug-only, like
the rest of the bot.

## Testing specific items (OB-45)

Chests roll items at random, so a run may never touch the item you changed. Each copy of the
game can be given a fixed loadout instead, in its own `BepInEx/config` file:

```ini
[Testing]
TestItems = IceCube, Bonker, CursedDoll, Cactus
```

- **Format.** Comma-separated EItem names, case-insensitive, with an optional `*count`
  (`CursedDoll*2`). The names are the game's internal ones: Big Bonk is `Bonker`, Thunder Mitts
  is `GloveLightning`. An unknown name is logged as `[test] OB-45: '<name>' is not an item name`
  and skipped.
- **When it applies.** Granted once per session, when the run starts, through the same path a
  chest uses, so every peer sees the items. Each grant logs `[test] OB-45: granted <item> x<n>.`
- **Leave it empty for normal play.**

**At the end of every multiplayer run,** each copy writes its end-screen damage panel to its log:

```
[report] OB-45: end screen for <id>: 9 damage row(s), 0 at LVL 0, 4646 kill(s), host True.
[report]   Bonker                 LVL  51     216,300,000
```

LVL is computed the way the panel computes it, so a row marked `<-- LVL 0` is a LVL 0 row on
screen. Compare the three logs side by side.

**A loadout that covers every item group.** Use it on the bots, and leave the host empty:

| Copy | `TestItems` | Covers |
|---|---|---|
| Bot 1 | `DemonBlade, BloodyCleaver, Dragonfire, Cactus, Mirror, Bonker, CursedDoll` | heal on crit and lifesteal reactions on the owner only (OB-39/43), a burn credited to its owner (OB-40), thorns and Mirror on the owner only (OB-43/47), host-run procs (OB-37), timed damage (OB-42) |
| Bot 2 | `GlovePoison, Snek, GoldenShield, SpicyMeatball, GlovePower, SluttyCannon, HolyBook, DemonicSoul` | poison credited to its owner (OB-40), owner-only gold (OB-43), host-run procs starting from the bot's position (OB-43), Holy Book on its owner only (OB-42/48), kill stacks on the owner (OB-39) |

An earlier loadout (`IceCube, Bonker, CursedDoll, Cactus` / `GloveLightning, SpicyMeatball,
HolyBook, GoldenShield, EnergyCore`) confirmed OB-37, OB-38 and OB-42 on 2026-10-20.

**What to look for:**

- **Each bot's report** lists its items at their real level (x1 = LVL 1), with damage that grows
  over the run.
- **The host's report** has none of the bots' items, and no `<-- LVL 0` rows.
- **The host's log** carries `[items] OB-37` and `[stats] OB-38`, and few or no
  `[attribution] OB-41` lines.
- **Play without the god-mode build.** Otherwise the host kills everything before the bots' hits
  land.

## What this setup cannot tell you

**Loopback has no packet loss, no latency, and no reordering.** Per
[`../README.md`](../README.md), most netcode bugs in this codebase are invisible at 0% loss —
delivery-method mistakes in particular are *silent* on localhost and only appear in the field.

A clean two-instance run on one PC proves a feature works. It does **not** prove the netcode is
correct. To get closer:

- **`clumsy`** (Windows) can drop and delay packets, but capturing loopback needs a filter that
  targets the mod's ports explicitly — e.g. `udp and (tcp.DstPort == 27015 or udp.DstPort ==
  27016)`. Verify it is actually intercepting before trusting a result; loopback capture is the
  part that most often silently does nothing.
- **Two physical machines on a LAN** with `clumsy` on one is more reliable than loopback
  shaping, and is the minimum for taking a "no desync" claim seriously.
- **Force the relay path.** Direct P2P on `127.0.0.1` always succeeds, so loopback never
  exercises the relay code that real IPv6 and failed-hole-punch users hit.

Also not covered: NAT traversal, the matchmaking server under concurrent load, and anything
involving more than two peers.

---

## Testing the Steam paths specifically

For [P0-0](01-critical-fixes.md#p0-0), the checks are:

| Check | How |
|---|---|
| No leaderboard entry from a netplay run | Finish a netplay run, then open the in-game leaderboard. The prefix blocks unconditionally, so this does **not** require clearing the game's own 349,999 score threshold |
| Achievements still unlock in netplay | Earn any achievement during a session; the Steam overlay popup is the signal |
| Singleplayer still uploads normally | Play a **non**-netplay run afterwards and confirm a leaderboard entry appears — this is the teardown-leak check, and it is the one most likely to regress |

The third row is the one worth caring about. A suppression that never turns back off looks fine
in every netplay test and silently breaks legitimate progression.

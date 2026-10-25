# OmniBonk

Peer-to-peer multiplayer mod for **Megabonk** (IL2CPP Unity, BepInEx 6). Up to 6 players,
host-authoritative, LiteNetLib UDP + MemoryPack, with a WebSocket matchmaking server that
relays when a direct connection can't be established.

Renamed from Megabonk Together to **OmniBonk** at 1.0.0: plugin GUID, assemblies, namespaces, project
files and the solution all say `OmniBonk`. Only `[BepInIncompatibility("MegabonkTogether")]` in
`Plugin.cs` keeps the old name, on purpose: it names the old plugin so the two never load together. Rename record:
[`docs/netplay/19-session-handover.md`](docs/netplay/19-session-handover.md), section 3.
The docs index, the CONFIRMED / LIKELY / UNVERIFIED status legend, and standing ground rules:
[`docs/README.md`](docs/README.md).

## Layout

| Project | Path | Constraint |
|---|---|---|
| `Common` | `src/common/` | The wire format. **No `UnityEngine` types** — this compiles into the server. |
| `Plugin` | `src/plugin/` | BepInEx 6 IL2CPP plugin. Ships as `OmniBonk.dll`. |
| `Server` | `src/server/` | ASP.NET matchmaking + UDP relay. Dockerized. |

Inside the plugin: `Patches/` detect, `Services/` decide, `Scripts/` are injected
MonoBehaviours that tick, `Helpers/` are stateless utilities. A patch that grew logic belongs
in a service.

## Never

**Break the wire format.** `[MemoryPackUnion(N, ...)]` tags in
`src/common/Messages/GameNetworkMessages/GameNetworkMessage.cs` are append-only — never
renumber, reuse, or remove one. MemoryPack serializes positionally and peers on different mod
versions still handshake, so a changed tag corrupts sessions silently rather than failing
loudly. Adding a field to an existing message is the same hazard.

**Downgrade reliability for speed.** `Unreliable` is correct only where a later message
supersedes this one. One-shot transitions — died, opened, added, started, stopped — are always
reliable, because "applied zero times" is a permanently divergent state. Anything carrying a
list, string, or snapshot must be reliable regardless: LiteNetLib fragments reliable channels
only, so past ~1400 bytes an unreliable send silently fails. Policy and the full message map:
[`docs/netplay/02-delivery-method-reference.md`](docs/netplay/02-delivery-method-reference.md).

**Write an unguarded patch.** Every Harmony patch opens with a `HasNetplaySessionStarted()`
check and an ownership check (local player, or `IsHost`). Without the first, the mod changes
singleplayer. Without the second, every peer rebroadcasts the same event.

**Claim something works.** There is no test suite. Nothing is verified until it has been run
in-game — say that plainly rather than implying coverage. The interop assemblies in
`src/plugin/stripped-libs/` are signature-only stubs with no method bodies, so any behaviour
inferred from a game method's signature is an assumption: mark it **UNVERIFIED**, as
`docs/netplay/01-critical-fixes.md` does. Netcode bugs are mostly invisible at 0% loss — test
at 2–5%, not on LAN. A local two-copy run is possible without a person: see **Testing in game**.

## Build

```bash
dotnet build OmniBonk.sln -c Debug
```

Set `MegabonkPath` to a Megabonk install to compile against that install's interop assemblies
and auto-copy Debug output into `BepInEx/plugins/OmniBonk`:

```powershell
$env:MegabonkPath = "<path-to>\steamapps\common\Megabonk"
```

**The trap:** if that path doesn't contain `BepInEx/interop`, the csproj's `Exists()` guard
silently falls back to the committed `src/plugin/stripped-libs/`, which may be stale. It does
not error. Check this first when a game type looks wrong or missing. The auto-copy is
Debug-only and is skipped when `CI` or `GITHUB_ACTIONS` is set.

## Testing in game (hands-off)

Two copies of the game on this PC can play a session with no one at the keyboard: an **auto-host**
(`[Bot] AutoHost`) and a follow bot (`[Bot] BotMode`). Both are Debug-only (`#if DEBUG`); a Release
build has neither. Run it after a change that needs in-game evidence, before telling the user it works:

```powershell
pwsh scripts/test/run-local-session.ps1 -Minutes 12 -StageMinutes 4 -ClientItems "SoulHarvester, EnergyCore"
```

It builds Debug, copies the DLL into **both** copies and checks the hashes match, writes test
settings over each copy's `OmniBonk.cfg`/`BepInEx.cfg` (backed up, restored at the end — and at the
start of the next run if one was killed), starts the matchmaker if port 5000 is free, launches both
windowed, and leaves both logs and a `summary.txt` in `artifacts\local-session\<timestamp>`. The host
lobbies, starts, picks **Desert** (`-Map`; crypt rooms and Forest trap a bot), wanders, and takes the
stage portal every `-StageMinutes`. Defaults: host `C:\Program Files (x86)\Steam\steamapps\common\Megabonk`,
client `D:\Games\Megabonk` (`-HostPath`, `-ClientPath`).

How to run it from a session:

- **Launch it with `run_in_background`** and send output to a file in an existing folder
  (`*> file`). A broken output pipe kills the script before it restores the user's configs.
- **Do not poll with sleeps.** Wait on log markers with an `until grep -q …` loop, and keep any
  Monitor filter to milestones: the `[bot] stuck` line repeats every few seconds.
- **Check progress in the logs** (`BepInEx/LogOutput.log` in each copy): `[bot] hosting`, `joining`,
  `selected the Desert map`, `run started`, `on stage N`, `entering the portal`; feature markers such
  as `[item-fx] session so far` and `[rocket] Drew the first explosion`.
- **Look at the game** when the logs are not enough: `pwsh scripts/test/capture-game-windows.ps1`
  saves a PNG of each window (works unfocused), which you can then read.
- **Stop early** by closing the game windows (`CloseMainWindow`); the script notices, restores the
  configs and saves the logs. Never kill the script process itself.
- **Do not start a session while the user is playing** — it fails fast if a copy is already running.

What it cannot prove: anything at real packet loss (it runs at 0%), anything Steam-dependent, or how
something looks — report those as not verified. Random characters mean a given weapon may not appear;
use `-HostItems`/`-ClientItems` and `-HostWeapons`/`-ClientWeapons` (`"Mine, Rockets"`), and check the
log shows the feature actually ran. `-HostCharacter`/`-ClientCharacter` (`"TonyMcZoom"`) pick a
character for its passive. Both bots are invincible, so anything that reacts to its owner being hit
(Mirror, Electric Plug, Cactus, Aegis) never fires unless you pass `-ClientVulnerable`. `-ThirdPath
"D:\Games\Megabonk - Copy"` adds a second client (with `-ThirdItems`/`-ThirdWeapons`/`-ThirdCharacter`),
for anything a third player must see. `-SeekBurgers` makes following bots walk into Borgor burgers. `-NoEnemyCap` sets the lobby setting on
the host, and `-TestSpawnEnemies 1200` has the host request that many skeletons through the capped
spawn path. Prefer a **count on both sides** to a single "it ran" line: OB-58's
first fix logged its marker fine while drawing 17 times too many mine explosions, which only the
`[explosions]` host/client counts exposed.

**Trap:** OmniBonk refuses to load beside the old `MegabonkTogether` plugin
(`[BepInIncompatibility]`). A Debug build from a worktree cut before the rename auto-copies one into
`MegabonkPath`'s `plugins` folder. Build such worktrees with `CI=true`, which skips the auto-copy. The
script refuses to start while one is present.

Full detail: [`docs/netplay/05-local-testing.md`](docs/netplay/05-local-testing.md), "Hands-off session".

## Releasing

Three files move together, then a tag:

1. `src/plugin/OmniBonk.Plugin.csproj` → bump `<Version>`
2. `CHANGELOG.toml` (repo root) → new `[version."X.Y.Z"]` block. **Shown in-game** — write it
   for a player, not a developer.
3. `README.md` → matching entry in the changelog `<details>` block
4. Tag `X.Y.Z` and push it; `.github/workflows/build-plugin.yml` builds and publishes

Nothing enforces that the tag and `<Version>` agree — a mismatch ships a build whose in-game
version differs from its release. Thunderstore is published separately and does not follow the
GitHub release.

## Where to look

| Working on | Load skill | Then read |
|---|---|---|
| A synced mechanic, a message, a delivery method | `netcode` | [`docs/netplay/02-delivery-method-reference.md`](docs/netplay/02-delivery-method-reference.md) |
| A Harmony patch | `harmony`, `il2cpp` | neighbouring files in `src/plugin/Patches/` |
| Injected MonoBehaviour, Update loop, pooling | `unity`, `il2cpp` | `src/plugin/Scripts/` |
| Plugin startup, config, logging | `bepinex` | `src/plugin/Plugin.cs` |
| Build, references, CI, Thunderstore/Proton | `build-and-release` | [`THUNDERSTORE_BUILD.md`](THUNDERSTORE_BUILD.md), [`docs/PROTON_SETUP.md`](docs/PROTON_SETUP.md) |
| Where a file goes, service and DI conventions | `csharp` | — |
| FPS, stutter, GC | `unity`, `netcode` | [`docs/netplay/04-performance-and-gc.md`](docs/netplay/04-performance-and-gc.md) |
| "What does the game actually do here?" | `il2cpp` | **Decompile it — see below** |
| "Does it work in game?" | — | **Run a hands-off session — see Testing in game** |
| Game formulas, enums, item/weapon behaviour | — | [`lukeod/megabonk_research`](https://github.com/lukeod/megabonk_research) — see below |

## Answering "what does this method actually do?"

The interop stubs and `dump.cs` both give **signatures and fields, never bodies**. Anything about
behaviour has to be decompiled. Two tools, and the distinction matters:

| Question | Tool |
|---|---|
| Does this member exist? What type is it? Is it virtual? | `scripts/re/interop_members.py Pickup` — offline, seconds |
| What is this type's **field layout**? | `megabonk-re/build-21750826/dump.cs` — has fields, offsets, RVAs and VAs |
| What does the body **do**? | decompile it |

```bash
"$APPDATA/ghidra/ghidra_12.1.2_PUBLIC/venv/Scripts/python.exe" scripts/re/decompile_headless.py 0x1804D7800 DamageContainer
```

Arguments are hex **VAs** (the `VA:` field in `dump.cs`, not `RVA:`) and/or case-insensitive name
substrings. Output lands in `megabonk-re/decompiled/<Function>.c`, cached — re-running is free.

**`analyzeHeadless.bat` cannot do this.** It launches Ghidra without the PyGhidra provider, so any
`.py` post-script dies with "Ghidra was not started with PyGhidra". The script above starts Ghidra
*from* Python instead, which is why it needs that venv interpreter and not your system one. Ghidra
must not have the project open — it takes an exclusive lock.

**Three traps that have each cost real time:**

- **Use the VA, not the RVA.** `dump.cs` prints both. An RVA silently resolves to an unrelated
  function rather than failing — a lookup for `Enemy.set_target` once landed on
  `TMP_FontAsset$$get_usedGlyphRects`. If the decompiled function's name doesn't match what you
  asked for, you used the wrong number.
- **Ghidra's applied struct field names can be off by a slot.** `Pickup$$Update.c` prints `.target`
  where the `pickedUp` byte is and `.speed` where the `target` pointer is read. `dump.cs`'s offsets
  are authoritative; treat a field *name* in decompiled output as a hint, and confirm it against the
  layout.

- **IL2CPP folds identical method bodies into one function.** Before patching, check the method's VA
  is not shared: `grep -c "VA: 0x..." dump.cs` must print 1. `ToxicBarrelEffect.OnUse` and
  `HolyBookEffect.OnUse` share one address; a patch on one made every Holy Book use recurse until the
  client died of a stack overflow (exception 0xC00000FD, no log line, no `Player.log` entry). The
  Windows Application event log (Id 1000) is where such a crash shows.

Worth doing early rather than late: a field layout from `dump.cs` settled in one lookup what four
playtests could not, and three separate UNVERIFIED markers in `docs/netplay/` existed only because
nobody had decompiled the twenty lines that answered them.

Full workflow: [`docs/reverse-engineering/00-decompilation-guide.md`](docs/reverse-engineering/00-decompilation-guide.md).

## External reference: `lukeod/megabonk_research`

Third-party decompilation notes covering ground this repo does not: damage and crit formulas,
the complete enum reference, per-weapon and per-item behaviour. Complementary rather than
overlapping — our own findings in `docs/reverse-engineering/` are netplay-specific
(`BaseSummoner`, `giveCreditsTimer`, `isGolden`, `GetNumMaxEnemies`), none of which it covers,
while it documents item/weapon internals we have not touched.

Worth trusting as a starting point: its `DamageContainer` field list matches ours field-for-field,
derived independently.

**Two rules when using it.**

1. **Addresses are build-specific and will not match.** It cites
   `WeaponUtility$$GetDamageContainer` at `0x180435010`; on buildid 21750826 the two overloads
   are at `0x180434A50` and `0x180434FF0`. Re-derive every address against the local dump —
   never paste one from an external source into a patch or a doc.
2. **It is a reference, not a source of truth.** Same UNVERIFIED discipline as everything else:
   a claim there is a strong hypothesis, not a verified fact, until checked against
   `megabonk-re/build-21750826/dump.cs` or a decompiled body.

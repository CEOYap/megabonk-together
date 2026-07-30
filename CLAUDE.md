# MegabonkTogether

Peer-to-peer multiplayer mod for **Megabonk** (IL2CPP Unity, BepInEx 6). Up to 6 players,
host-authoritative, LiteNetLib UDP + MemoryPack, with a WebSocket matchmaking server that
relays when a direct connection can't be established.

Fork of [`Fcornaire/megabonk-together`](https://github.com/Fcornaire/megabonk-together).
Fork goals, the CONFIRMED / LIKELY / UNVERIFIED status legend, and standing ground rules:
[`docs/README.md`](docs/README.md).

## Layout

| Project | Path | Constraint |
|---|---|---|
| `Common` | `src/common/` | The wire format. **No `UnityEngine` types** — this compiles into the server. |
| `Plugin` | `src/plugin/` | BepInEx 6 IL2CPP plugin. Ships as `MegabonkTogether.dll`. |
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
at 2–5%, not on LAN.

## Build

```bash
dotnet build MegabonkTogether.sln -c Debug
```

Set `MegabonkPath` to a Megabonk install to compile against that install's interop assemblies
and auto-copy Debug output into `BepInEx/plugins/MegabonkTogether`:

```powershell
$env:MegabonkPath = "<path-to>\steamapps\common\Megabonk"
```

**The trap:** if that path doesn't contain `BepInEx/interop`, the csproj's `Exists()` guard
silently falls back to the committed `src/plugin/stripped-libs/`, which may be stale. It does
not error. Check this first when a game type looks wrong or missing. The auto-copy is
Debug-only and is skipped when `CI` or `GITHUB_ACTIONS` is set.

## Releasing

Three files move together, then a tag:

1. `src/plugin/MegabonkTogether.Plugin.csproj` → bump `<Version>`
2. `src/plugin/CHANGELOG.toml` → new `[version."X.Y.Z"]` block. **Shown in-game** — write it
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
| "What does the game actually do here?" | `il2cpp` | [`docs/reverse-engineering/00-decompilation-guide.md`](docs/reverse-engineering/00-decompilation-guide.md) |
| Game formulas, enums, item/weapon behaviour | — | [`lukeod/megabonk_research`](https://github.com/lukeod/megabonk_research) — see below |

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

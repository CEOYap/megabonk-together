---
name: build-and-release
description: |
  Building, referencing and shipping MegabonkTogether — MegabonkPath vs stripped-libs, the THUNDERSTORE and PROTON compile constants, the four GitHub workflows, and the release checklist.
  Use when: a build fails to find a game assembly, adding a new Unity/game DLL reference, changing a csproj or workflow, cutting a release or bumping a version, or setting up Docker/self-hosting for the server.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Build and Release

Solution: `MegabonkTogether.sln` — plugin, server, common. All `net6.0`. SDK 6.0.x in CI.

```bash
dotnet build MegabonkTogether.sln -c Debug
```

```bash
dotnet build src/plugin/MegabonkTogether.Plugin.csproj -c Release
```

## Game assembly references

The plugin needs Megabonk's Il2CppInterop proxy assemblies. Two sources, resolved in the csproj:

| Condition | `AssemblyPath` / `UnityLibPath` |
|---|---|
| Default | `src/plugin/stripped-libs/{interop,unity-libs}` — committed to the repo |
| `MegabonkPath` set **and** `$(MegabonkPath)/BepInEx/interop` exists | the real game folder, and `PluginPath` becomes `$(MegabonkPath)/BepInEx/plugins` |

Set `MegabonkPath` to your game install to get a Debug post-build auto-copy into
`BepInEx/plugins/MegabonkTogether` (Windows `xcopy`, Linux `cp`). The copy is **Debug-only** and
is skipped when `GITHUB_ACTIONS` or `CI` is set.

```powershell
$env:MegabonkPath = "C:\Program Files (x86)\Steam\steamapps\common\Megabonk"
```

### Adding a new game or Unity DLL reference

Three steps, all required:

1. Add a `<Reference>` with `<Private>false</Private>` under `$(AssemblyPath)` (interop) or
   `$(UnityLibPath)` (unity-libs) in `MegabonkTogether.Plugin.csproj`.
2. Add a line to `src/plugin/strip-dlls.bat` producing the stripped copy.
3. Run `strip-dlls.bat` with `MegabonkPath` set, and commit the new stripped DLL.

`strip-dlls.bat` runs `assembly-publicizer --strip-only`, which keeps signatures and discards
bodies. Committing a **stripped** assembly is what keeps the repo legal and small — never commit
a full game DLL.

Currently referenced: `Assembly-CSharp`, `Il2Cppmscorlib`, `Il2CppSystem(.Core)`,
`UnityEngine.{Core,Animation,Physics,ParticleSystem,Audio}Module`, `UnityEngine.UI`,
`Unity.TextMeshPro`, `Rewired_Core`, `Unity.Localization`, `Coffee.UIParticle`.

## Compile constants

| Constant | Set by env var | Effect |
|---|---|---|
| `THUNDERSTORE` | `THUNDERSTORE_BUILD=true` | Thunderstore/r2modman distribution — disable the in-mod auto-updater; users update through the platform |
| `PROTON` | `PROTON_BUILD=true` | Linux / Steam Deck via Proton (v5.0.0, experimental); cross-play with Windows must be preserved |

```csharp
#if THUNDERSTORE
    // no auto-update path
#endif
```

**Cross-play constraint:** these constants may change *distribution and platform plumbing only*.
If a `#if` ever changes the wire format or message handling, Proton and Windows players can no
longer play together. Guard build/IO/update concerns, never netcode. See `docs/PROTON_SETUP.md`
and `THUNDERSTORE_BUILD.md`.

## CI workflows

`.github/workflows/`:

| Workflow | Trigger | Produces |
|---|---|---|
| `build-plugin.yml` | push of any tag | `Megabonk-Together-<version>.zip` (DLLs+PDBs under a `MegabonkTogether/` folder), GitHub Release with commit-diff notes |
| `build-plugin-thunderstore.yml` | — | Thunderstore package |
| `build-plugin-proton.yml` | — | Proton/Linux build |
| `build-server.yml` | — | matchmaking server image |

The release version comes from the tag with a leading `v` stripped. Tag and csproj `<Version>`
must agree — nothing enforces this, and a mismatch ships a build whose in-game version doesn't
match its release.

## Release checklist

1. `src/plugin/MegabonkTogether.Plugin.csproj` → bump `<Version>`
2. `src/plugin/CHANGELOG.toml` → new `[version."X.Y.Z"]` block with a `changes = [...]` array
3. `README.md` → new section in the changelog `<details>` block (emoji-prefixed, matching style)
4. Commit, then tag `X.Y.Z` and push the tag — `build-plugin.yml` does the rest
5. Thunderstore is a separate publish; it does not auto-update from the GitHub release

Skipping step 2 means players see no changelog after updating; skipping step 3 means the README
diverges from what shipped.

## Server

`src/server/` is `Microsoft.NET.Sdk.Web`: WebSocket matchmaking at `/ws`, a UDP
`RendezVousServer` for NAT introduction and relay, Prometheus metrics at `/metrics`.
`Dockerfile` at the repo root; `DockerfileContext` is `..\..\..`.

Config comes from the `Config` section of `appsettings.json` / `appsettings.Production.json`
bound to `ConfigOptions`. It runs behind nginx in production, which is why `ForwardedHeaders`
clears `KnownNetworks`/`KnownProxies` — **if you self-host without a trusted proxy, remove those
two lines** (the code says so inline). Self-hosting guide: `docs/Setup-Own-Server.md`.

`src/server/MegabonkTogether.Server.csproj` carries an `xunit` PackageReference with no tests
behind it — it is dead weight, not a signal that a test suite exists.

## Common mistakes

1. **Build can't find `Assembly-CSharp`** → `MegabonkPath` points somewhere without
   `BepInEx/interop`; it silently falls back to `stripped-libs`, which may be stale.
2. **New reference added but not stripped/committed** → builds locally, fails in CI.
3. **Committing an unstripped game DLL** → licensing problem and repo bloat.
4. **Tag and csproj `<Version>` disagree** → mismatched release.
5. **Expecting the post-build copy in Release** → it is Debug-only by design.
6. **Putting netcode behind `#if PROTON`/`#if THUNDERSTORE`** → breaks cross-play.

## Related skills

- **bepinex** — `MyPluginInfo`, changelog and auto-updater services
- **il2cpp** — what the stripped proxies can and can't tell you

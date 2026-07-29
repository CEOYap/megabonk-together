# Decompiling Megabonk

Toolchain and workflow for inspecting the IL2CPP game build. The concrete list of what to
look at is in [`01-investigation-targets.md`](01-investigation-targets.md).

---

## Why this is needed

Megabonk ships as an **IL2CPP** Unity build: C# is compiled to C++ and then to native code.
There is no managed assembly to open in ILSpy. What the mod compiles against —
`src/plugin/stripped-libs/interop/Assembly-CSharp.dll` — is an Il2CppInterop-generated
*proxy*: it has the right type names, member names, and signatures, but **every method body
is a stub**. It tells you a field exists; it cannot tell you what writes to it.

That distinction is why several items in
[`../netplay/01-critical-fixes.md`](../netplay/01-critical-fixes.md) are marked UNVERIFIED.
The most consequential:
`__instance.giveCreditsTimer *= multiplier` in a `Tick` prefix is either a mild balance tweak
or a mechanic that shuts the summoner down within seconds, and the proxy assembly cannot
distinguish the two.

## What ships in this repo

`src/plugin/stripped-libs/` contains the interop proxies the build needs:

```
interop/     Assembly-CSharp.dll, Il2Cppmscorlib.dll, Il2CppSystem*.dll,
             Coffee.UIParticle.dll, Rewired_Core.dll, Unity.Localization.dll,
             Unity.TextMeshPro.dll, UnityEngine.UI.dll                    (9 files)
unity-libs/  UnityEngine.CoreModule.dll, .PhysicsModule, .AnimationModule,
             .AudioModule, .ParticleSystemModule                          (5 files)
```

Signatures only. Good for "does this field exist and what is its type", useless for
behaviour.

Note the game also ships `Il2Cppcom.rlabrecque.steamworks.net.dll` — Steamworks.NET is
already present in the build. See
[`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md#gotcha-1).

---

## Toolchain

### 0. Local paths

Fill these in for your machine; the commands below assume them.

| What | Path used in this guide |
|---|---|
| Game install (`MegabonkPath`) | `C:\Program Files (x86)\Steam\steamapps\common\Megabonk` |
| Il2CppDumper | `D:\01 Coding\Il2CppDumper\Il2CppDumper.exe` |
| AssetRipper | `D:\01 Coding\AssetRipper\AssetRipper.GUI.Free.exe` |
| UnityExplorer | installed into `<game>\BepInEx\plugins` |

Set `MegabonkPath` once at user scope so both `dotnet build` and VS Code see it:

```powershell
[Environment]::SetEnvironmentVariable("MegabonkPath", "C:\Program Files (x86)\Steam\steamapps\common\Megabonk", "User")
```

> **The csproj only honours `MegabonkPath` once `<game>\BepInEx\interop` exists.** BepInEx 6
> generates the Il2CppInterop proxies on the game's **first launch** with BepInEx installed —
> until you have run the game once, the guard in
> `src/plugin/MegabonkTogether.Plugin.csproj` fails and the build silently falls back to the
> committed `src/plugin/stripped-libs/`. It does not error. Verify with:
>
> ```powershell
> Test-Path "$env:MegabonkPath\BepInEx\interop"
> ```

### 1. Il2CppDumper — start here

<https://github.com/Perfare/Il2CppDumper>

Produces `dump.cs`: every type, field, method signature, plus RVAs (offsets into the native
binary). This is the index you use to find things.

Both inputs ship with the game and are present in a stock install. Il2CppDumper does not need
BepInEx, so this works before you have ever launched the game.

> **It ignores the output-directory argument.** Whatever you pass as a third argument, this
> build writes its output **next to `Il2CppDumper.exe`**. Dump, then move — don't waste a run
> expecting the path to be honoured.

```powershell
$tool  = "D:\01 Coding\Il2CppDumper"
$build = "build-21750826"          # Steam buildid — see below

& "$tool\Il2CppDumper.exe" `
  "$env:MegabonkPath\GameAssembly.dll" `
  "$env:MegabonkPath\Megabonk_Data\il2cpp_data\Metadata\global-metadata.dat"

# then collect the output into the versioned folder
$dest = "megabonk-re\$build"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
foreach ($f in @("dump.cs","il2cpp.h","script.json","stringliteral.json","DummyDll")) {
  Move-Item "$tool\$f" $dest -Force -ErrorAction SilentlyContinue
}
```

`config.json` beside the exe sets `RequireAnyKey: true`, so the process waits for a keypress
before exiting. That is normal, not a hang.

Find the build id — this is the identifier that matters, since `Megabonk.exe` reports the
*Unity* version (2023.2.22f1), not the game version:

```powershell
Select-String -Path "C:\Program Files (x86)\Steam\steamapps\appmanifest_3405340.acf" -Pattern '"buildid"'
```

Outputs (sizes measured on buildid `21750826`):

| File | Size | Use |
|---|---|---|
| `dump.cs` | 28.5 MB | Human-readable listing of everything. Grep this first. |
| `script.json` | 85 MB | Import into IDA/Ghidra to name functions |
| `il2cpp.h` | 44 MB | Struct definitions for the native decompiler |
| `stringliteral.json` | 1.4 MB | Every string literal in the binary — useful for finding a mechanic by its UI text |
| `DummyDll/` | 74 assemblies | Proxy assemblies — same content as `stripped-libs/interop`, regenerated |

> **Never commit any of it.** That is ~159 MB per dump.
> `Sea-Bass-cmd/optimized-netplay` committed `dump.cs` in `45ce3f5` and removed it in
> `8628e71`, permanently adding its size to that repo's history — a removal commit does not
> shrink history. `/megabonk-re/` is gitignored as a whole directory to make this
> unrepeatable; verify with `git check-ignore -v` before your first dump.

### 2. Ghidra or IDA Free — for method bodies

<https://ghidra-sre.org/> (free) · <https://hex-rays.com/ida-free/> (free, x64 decompiler)

Load `GameAssembly.dll`, apply `script.json` so functions get real names, then read the
decompiled C for the RVA you found in `dump.cs`.

Local install: `D:\01 Coding\ghidra_12.1.2_PUBLIC` (headless at
`support\analyzeHeadless.bat`). `GameAssembly.dll` is 52 MB.

> **Ghidra 12 dropped Jython — patch the Il2CppDumper script before using it.**
> Ghidra 11.4+ replaced Jython with **PyGhidra** (CPython 3); there is no `jython*.jar` in a
> 12.x install. Il2CppDumper's `ghidra_with_struct.py` is a Python 2 script and fails to parse.
>
> It needs exactly **one** change — line 156:
>
> ```python
> print 'Script finished!'      # Python 2, fails under PyGhidra
> print('Script finished!')     # works
> ```
>
> Everything else in the script is already Python-3 compatible; its `from ghidra.app...` Java
> imports work fine under PyGhidra via JPype. A patched copy is at
> `megabonk-re/ghidra_with_struct_py3.py` so the tool install stays untouched.

Ghidra workflow:
1. Import `GameAssembly.dll`, let auto-analysis finish (slow — 20+ min for a Unity game).
2. Run `megabonk-re/ghidra_with_struct_py3.py` and point it at `script.json` and `il2cpp.h`
   from the same build folder.
3. Navigate to the **VA** and read the Decompiler pane.

Headless, if you would rather not sit through the GUI:

```powershell
& "D:\01 Coding\ghidra_12.1.2_PUBLIC\support\analyzeHeadless.bat" `
  "megabonk-re\ghidra" MegabonkProject `
  -import "$env:MegabonkPath\GameAssembly.dll" `
  -postScript "megabonk-re\ghidra_with_struct_py3.py"
```

Multibonk's README confirms this is the practical route for this specific game:
*"Inspected original methods by decompiling with Il2CppDumper and then converting back to C
with Ghidra."*

### 3. UnityExplorer — runtime inspection

<https://github.com/sinai-dev/UnityExplorer> (BepInEx IL2CPP build)

Often faster than static analysis for the questions that actually matter. Live scene
hierarchy, live component inspector, live field values, and a C# console you can evaluate in.

Use it to answer:
- Does this GameObject carry more than one net-relevant component? (the GameObject-vs-component
  keying question — see
  [`../netplay/03-cherry-pick-guide.md`](../netplay/03-cherry-pick-guide.md#netentity))
- Is this object destroyed or pooled on despawn? Watch the hierarchy during a pickup cycle.
- What is `giveCreditsTimer` actually doing frame to frame? Watch the value.

**A watched field beats a decompiled function** for most of the open questions in
[`01-investigation-targets.md`](01-investigation-targets.md). Do this first; decompile only
what runtime inspection cannot answer.

### 4. AssetRipper — data and prefabs, not code

<https://github.com/AssetRipper/AssetRipper> · `D:\01 Coding\AssetRipper\AssetRipper.GUI.Free.exe`

Extracts Unity assets from the built game: scenes, prefabs, ScriptableObjects, and the component
lists attached to each GameObject. It reads `global-metadata.dat` to resolve script references,
so exported prefabs show *which* MonoBehaviour types sit on an object even though method bodies
are unrecoverable.

Complementary to `dump.cs`, not a substitute: `dump.cs` tells you the shape of the **code**,
AssetRipper tells you the shape of the **data and the scene graph**. Point it at the game folder
and export.

Use it for:
- **Which components share a GameObject** — the GameObject-vs-component keying question in
  [`../netplay/03-cherry-pick-guide.md`](../netplay/03-cherry-pick-guide.md#netentity). A prefab
  export answers this statically; UnityExplorer answers it at runtime.
- **Balance and item data** held in ScriptableObjects, where a field's *value* matters as much
  as its existence.
- **Prefab and asset names** to match against the spawn paths the mod hooks.

Same caution as everything else here: use it to understand structure, don't ship extracted
assets.

### 5. dnSpy / ILSpy — limited use

Will open the interop proxies (`stripped-libs/interop/*.dll`) but every body is a stub. Fine
for browsing type shapes; useless for behaviour. `Il2CppDumper`'s `dump.cs` is easier to grep.

---

## Workflow

For any open question:

```
1. Grep dump.cs for the type/member.
     -> confirms existence, gives the RVA, shows the real signature
2. Watch it live in UnityExplorer.
     -> answers most behavioural questions in minutes
3. If still ambiguous, decompile the RVA in Ghidra.
     -> definitive, slow
4. Record the answer in 01-investigation-targets.md, with the game version.
```

Step 4 matters. Every answer here is tied to a specific game build; a patch invalidates it.
Note the version alongside the finding.

## Setup

```bash
# .gitignore — before running anything
dump.cs
script.json
il2cpp.h
DummyDll/
ghidra_projects/
re-notes/local-*
```

RE artifacts live in `megabonk-re/` at the **repo root**, one folder per game build, named by
Steam buildid:

```
megabonk-re/                  <- gitignored wholesale
  build-21750826/
    VERSION.txt               # appid, buildid, Unity version, dump date
    dump.cs
    script.json
    il2cpp.h
    stringliteral.json
    DummyDll/
    GameAssembly.dll          # copy here before Ghidra; do not analyse in place
    ghidra/
```

`VERSION.txt` is not optional bookkeeping. Every RVA and most field layouts change on a game
update, so a dump without its buildid is a dump you cannot trust six weeks later.

`/megabonk-re/` is ignored as a whole directory, not by per-file pattern. That is deliberate:
the entries above cover `dump.cs`, `script.json`, `il2cpp.h` and `DummyDll/` wherever they
appear, but **not** `GameAssembly.dll` (a licensed game binary) and **not** a folder named
`ghidra/` — only `ghidra_projects/`. A whole-directory ignore means anything a tool decides to
drop in there is covered without maintenance.

Verify before your first dump:

```bash
git check-ignore -v megabonk-re/v1/GameAssembly.dll
```

Keep one folder per game version. Findings expire on a game update, and having the old dump
beside the new one is what lets you tell whether a signature actually changed.

## Grep recipes for `dump.cs`

```bash
# Find a type and everything in it
grep -n -A 60 "^public class BaseSummoner" dump.cs

# Every field of a type
grep -n -A 200 "^public class ChargeShrine" dump.cs | grep -E "public|private|protected" | head -40

# Who else has a field with this name
grep -n "giveCreditsTimer" dump.cs

# Method with its RVA — the offset for Ghidra
grep -n -B2 -A2 "public void Tick()" dump.cs

# All types matching a prefix
grep -nE "^public (sealed )?class Pool" dump.cs
```

RVAs appear as comments above each method:

```csharp
// RVA: 0x1A2B3C0 Offset: 0x1A2B3C0 VA: 0x1801A2B3C0
public void Tick() { }
```

Use the **VA** in Ghidra after applying `script.json`.

---

## Cautions

- **Do not ship anything derived from decompiled code.** Use it to understand behaviour so
  you can write your own patch. Record findings as prose and field semantics in
  [`01-investigation-targets.md`](01-investigation-targets.md), not as pasted decompiler
  output.
- **Findings expire.** Every RVA and most field layouts change on a game update. Always
  record the game version with the finding.
- **Obfuscation.** Megabonk does not appear to be obfuscated (Multibonk's approach worked and
  the interop proxies have readable names), but verify per version.
- **`stripped-libs` is generated.** If a game update changes signatures, regenerate the
  interop assemblies rather than hand-editing them. See `src/plugin/stripped-libs/README.md`.

---

## Recording findings

Use this shape in [`01-investigation-targets.md`](01-investigation-targets.md) so answers stay
auditable:

```markdown
### `BaseSummoner.giveCreditsTimer`

**Question:** Is it a fixed interval or a per-tick countdown?
**Blocks:** whether the credit multiplier patch can be enabled at all
**Method:** UnityExplorer field watch during a stage, 60 s
**Game version:** <version>
**Finding:** ...
**Consequence:** ...
```

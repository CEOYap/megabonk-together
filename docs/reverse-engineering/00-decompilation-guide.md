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

> **Ghidra 12 dropped Jython — the Il2CppDumper script needs porting, not just patching.**
> Ghidra 11.4+ replaced Jython with **PyGhidra** (CPython 3); there is no `jython*.jar` in a
> 12.x install. Il2CppDumper's `ghidra_with_struct.py` is written for Jython/Python 2.
>
> A ported copy is at `megabonk-re/ghidra_with_struct_py3.py`, leaving the tool install
> untouched. Three distinct breakages, only one of which is a syntax error:
>
> | Problem | Lines | Why it breaks |
> |---|---|---|
> | Bare `ghidra.` package root | 17, 52, 67 | Jython had `ghidra` implicitly in scope. PyGhidra needs an explicit `import ghidra`, or you get `NameError: name 'ghidra' is not defined`. **This is the first thing you hit.** |
> | `.encode("utf-8")` | 7 sites | Returns `str` on Python 2 but `bytes` on Python 3. JPype will not marshal `bytes` to a Java `String`, so every symbol name and comment breaks. Python 3 strings are already Unicode — the calls are simply removed. |
> | `print '…'`, `"\)"` | 156, 73 | Python 2 print statement; invalid escape sequence (`SyntaxWarning` on 3.12+). |
>
> The `.encode` one is the dangerous one: it is not a syntax error, so a grep for Python-2
> *syntax* misses it entirely and the script fails only at runtime, mid-run, after you have
> already waited through analysis.
>
> Verify any future port compiles before running it:
>
> ```powershell
> & "C:\Python312\python.exe" -W error::SyntaxWarning -m py_compile "megabonk-re\ghidra_with_struct_py3.py"
> ```

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

## Ghidra walkthrough

**Use the Code Browser, not the Debugger.** The Debugger attaches to a *running* process; this
is static analysis of a file on disk. You will never launch Megabonk from Ghidra.

### Step 0 — one-time setup (two traps)

**Launch with `support\pyghidraRun.bat`, not `ghidraRun.bat`.** Only the PyGhidra launcher
registers the CPython 3 script provider. Under plain `ghidraRun.bat` the Il2CppDumper script
will not appear as runnable.

**PyGhidra must use Python 3.12, not 3.14.** Ghidra bundles `jpype1-1.5.2` wheels for cp39–cp313
only — there is no cp314 wheel, so a 3.14 interpreter fails to install PyGhidra. On this machine
`python` is 3.12.10 (`C:\Python312\python.exe`) and the `py` launcher resolves to 3.14. Force the
right one before first launch:

```powershell
$env:PYGHIDRA_PYTHON = "C:\Python312\python.exe"
& "D:\01 Coding\ghidra_12.1.2_PUBLIC\support\pyghidraRun.bat"
```

First run prints a prompt offering to install PyGhidra into a venv — accept it. Java 21+ is
required and is already present (21.0.11).

### Step 1 — project

`File → New Project… → Non-Shared Project`. Put it in `megabonk-re/ghidra/`, name it after the
build (`build-21750826`). One project per game build, mirroring the dump folders.

### Step 2 — import

`File → Import File…` → `%MegabonkPath%\GameAssembly.dll`.

Ghidra should detect **PE / x86:LE:64:default**. Accept the defaults; do not change the image
base — the VAs in `dump.cs` assume the default `0x180000000`, and changing it silently
invalidates every address in this repo's docs.

### Step 3 — analyse (the slow part)

Double-click the imported file to open the **Code Browser**. It offers to analyse — say yes,
accept the default analyzers, `Analyze`.

**This takes 20–60 minutes for 52 MB.** The progress bar is bottom-right. Let it finish
completely before running any script; a script applied mid-analysis produces garbage. Analysis
results are saved into the project, so this cost is paid once per build.

### Step 4 — register the script directory

`Window → Script Manager` → the **Manage Script Directories** icon (top-right, looks like a
bulleted list) → `+` → add `megabonk-re/`.

Then find `ghidra_with_struct_py3.py` in the list. If it shows a red error icon, PyGhidra is not
active — you launched the wrong `.bat`, go back to Step 0.

### Step 5 — parse `il2cpp.h` first (optional, but do it before Step 6)

**The script does not load `il2cpp.h`.** It prompts for `script.json` and nothing else — the
header must already be in Ghidra's data type manager, as the script's own comment says
(*"Requires types (il2cpp.h) to be imported first"*). Load it via
`File → Parse C Source…`:

1. Add `megabonk-re/build-21750826/il2cpp.h` to the source-files list
2. Clear the parse options / include paths (the defaults target real system headers)
3. `Parse to Program`

**This is heavy — the header is 44 MB.** Expect a long parse, and expect some errors in its
log; partial success is normal and still useful. If Ghidra runs out of memory, raise
`MAXMEM` in `support\launch.properties` and restart.

**You can skip this step.** Without it you still get every function *name*, which is most of the
value; you lose typed struct fields, so the decompiler shows `*(float *)(param_1 + 0x38)`
instead of `this->procCoefficient`. Since reading IL2CPP output means mapping offsets against
`dump.cs` by hand anyway, skipping is a reasonable trade on a first pass.

### Step 6 — run the script

Select `ghidra_with_struct_py3.py` → **Run** → choose
`megabonk-re/build-21750826/script.json` at the single prompt.

Six passes run in order — `Methods`, `Strings`, `Metadata`, `Metadata Methods`, `Addresses`,
`Signatures` — ending with `Script finished!` in the console.

**Before this step**, every function is `FUN_1804a64d0`. **After it**, the same function is
`DamageContainer$$Reuse`. That rename is the entire point of the exercise.

> **A wall of `Warning: Unable to parse` in the `Signatures` pass means Step 5 was skipped.**
> Ghidra cannot resolve `Unity_Hierarchy_HierarchyViewModel_o*` if the header was never parsed,
> so every signature is rejected — often thousands of them.
>
> **This is not a failure.** The `Signatures` pass runs *last*; names and function boundaries
> were already applied by the earlier passes and are intact. You can navigate and decompile
> immediately. Re-run the script after parsing the header if you want typed signatures — it is
> safe to run repeatedly.

### Step 7 — go to an address and read

Press **`G`** (Go To), paste a VA from `dump.cs` — e.g. `0x1804A64D0` — and press Enter.

The **Decompiler** pane on the right shows reconstructed C. If it is not visible:
`Window → Decompiler`.

Reading tips for IL2CPP output:

- The first parameter is the instance pointer (`this`), even on methods that look static.
- Field accesses appear as offsets: `*(float *)(param_1 + 0x38)` is `procCoefficient`, because
  `dump.cs` lists it at `0x38`. **Keep `dump.cs` open beside Ghidra and map offsets by hand** —
  this is the core skill.
- Calls to `il2cpp_*` runtime helpers are boilerplate; skim past them.
- Right-click a variable → `Retype Variable` to improve output as you learn what it is.

### Step 7b — skip the GUI entirely (recommended once the project exists)

The GUI is needed exactly once — to build the project (Steps 1–6). After that the analysis and
the Il2CppDumper names are persisted on disk (~2 GB in `megabonk-re/ghidra-re/`), and
`megabonk-re/decompile.py` batch-decompiles straight to text with no GUI at all:

```powershell
$env:PYGHIDRA_PYTHON = "C:\Python312\python.exe"
& "D:\01 Coding\ghidra_12.1.2_PUBLIC\support\analyzeHeadless.bat" `
  "megabonk-re\ghidra-re" Megabonk `
  -process -noanalysis `
  -scriptPath "megabonk-re" `
  -postScript decompile.py 0x18046D990 0x1803EBDE0 DamageContainer
```

Arguments are hex VAs (`0x...`) and/or case-insensitive substrings of a function name. Output
lands in `megabonk-re/decompiled/<FunctionName>.c`, one file per function, each with a header
recording its entry point.

`-noanalysis` is what makes this fast — it reuses the existing analysis rather than redoing the
20–60 minute pass.

> **`LockException: Unable to lock project!` means Ghidra still has the project open.** Close it
> in the GUI (`File → Close Project`) or quit Ghidra, then re-run. The GUI and headless cannot
> hold the same project simultaneously.

**Do not switch to r2ghidra or ghidra-cli for this.** r2ghidra needs radare2 and does not import
Il2CppDumper's `script.json`, so every function reverts to `fcn.1804a64d0` and the naming work is
lost. `ghidra-cli` needs a Rust toolchain and a full JDK to build, and is WSL-oriented. Neither
buys anything the script above does not already do with zero extra dependencies.

### Step 8 — record the finding

Write the answer into
[`01-investigation-targets.md`](01-investigation-targets.md) using the shape under
[Recording findings](#recording-findings), with the buildid. An unrecorded finding will be
re-derived from scratch in three weeks.

### First target

Start with **`DamageContainer.Reuse` at VA `0x1804A64D0`**. The question is narrow and the
answer is visible in a short function: does it *reset fields on an existing instance*, or
allocate? Reset confirms pooling, which upgrades
[#4](01-investigation-targets.md#4-damagecontainer--important) from STRONG to CONFIRMED and
settles two other conclusions that currently rest on it.

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

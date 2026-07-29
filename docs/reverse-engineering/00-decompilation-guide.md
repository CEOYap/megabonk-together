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

### 1. Il2CppDumper — start here

<https://github.com/Perfare/Il2CppDumper>

Produces `dump.cs`: every type, field, method signature, plus RVAs (offsets into the native
binary). This is the index you use to find things.

```
Il2CppDumper.exe "Megabonk\GameAssembly.dll" "Megabonk\Megabonk_Data\il2cpp_data\Metadata\global-metadata.dat" out\
```

Outputs:

| File | Use |
|---|---|
| `dump.cs` | Human-readable listing of everything. ~11.5 MB for Megabonk. Grep this first. |
| `script.json` | Import into IDA/Ghidra to name functions |
| `il2cpp.h` | Struct definitions for the native decompiler |
| `DummyDll/` | Proxy assemblies — same content as `stripped-libs/interop`, regenerated |

> **Never commit `dump.cs`.** It is ~11.5 MB. `Sea-Bass-cmd/optimized-netplay` committed it
> in `45ce3f5` and removed it in `8628e71`, permanently adding 11.5 MB to that repo's
> history. Add it to `.gitignore` before you run anything.

### 2. Ghidra or IDA Free — for method bodies

<https://ghidra-sre.org/> (free) · <https://hex-rays.com/ida-free/> (free, x64 decompiler)

Load `GameAssembly.dll`, apply `script.json` so functions get real names, then read the
decompiled C for the RVA you found in `dump.cs`.

Ghidra workflow:
1. Import `GameAssembly.dll`, let auto-analysis finish (slow — 20+ min for a Unity game).
2. Run the Il2CppDumper Ghidra script (`ghidra_with_struct.py`) with `script.json` and
   `il2cpp.h`.
3. Navigate to the RVA and read the Decompiler pane.

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

### 4. dnSpy / ILSpy — limited use

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

Suggested local layout (outside the repo, or ignored):

```
~/megabonk-re/
  v<version>/
    dump.cs
    script.json
    il2cpp.h
    GameAssembly.dll        # copy, do not analyse in place
    ghidra/
```

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

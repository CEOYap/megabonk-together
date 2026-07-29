---
name: il2cpp
description: |
  Il2CppInterop rules for talking to Megabonk's IL2CPP runtime — type registration, Il2CppSystem collections, delegates, casting, and null semantics.
  Use when: writing or reviewing any plugin code that touches a game type, injecting a MonoBehaviour, subscribing to a game Action, hitting a NullReferenceException or hard crash with no managed stack trace, or wondering why a proxy method does nothing.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# IL2CPP Interop

Megabonk is an **IL2CPP** Unity build: its C# was compiled to C++ then to native code. The plugin does not reference the game's managed assemblies — it references Il2CppInterop-generated *proxies* in `src/plugin/stripped-libs/interop/`, whose method bodies are **empty stubs**. Every call across that boundary is a marshalled call into native code, and most crashes in this mod come from breaking one of the rules below.

**The single most important consequence:** the proxy tells you a member *exists and its signature*. It cannot tell you what the method *does*. Never reason about game behaviour from the proxy — see the **Reverse engineering** section.

## Quick reference

| Situation | Rule |
|---|---|
| Your own `MonoBehaviour` | Must be registered with `ClassInjector.RegisterTypeInIl2Cpp<T>()` in `Plugin.Load()` **before** first `AddComponent` |
| Game field is `List<T>` | It's `Il2CppSystem.Collections.Generic.List<T>` — not BCL `List<T>`. No LINQ, no `foreach` over it without care |
| Game callback is `Action` | It's `Il2CppSystem.Action` — build with `DelegateSupport.ConvertDelegate` or assign an `Il2CppSystem.Action` instance |
| Casting a `GameObject`/component | Use `.TryCast<T>()`, never a C# `(T)` cast |
| Checking for null | Unity null is not managed null — check `obj == null` **and** be aware a destroyed object is non-null managed-side |
| Reflection over game types | `AccessTools` (Harmony) + `Il2CppType.Of<T>()`, not `typeof(T)` where the runtime wants an Il2Cpp type |
| Strings crossing the boundary | Marshalled on every access. Don't read a game string per-frame |

## Type injection

Any managed `MonoBehaviour` you intend to `AddComponent` must be registered first. `Plugin.Load()` does this for all of them in one block:

```csharp
ClassInjector.RegisterTypeInIl2Cpp<NetPlayer>();
ClassInjector.RegisterTypeInIl2Cpp<CoroutineRunner>();
ClassInjector.RegisterTypeInIl2Cpp<MainThreadDispatcher>();
ClassInjector.RegisterTypeInIl2Cpp<NetworkHandler>();
ClassInjector.RegisterTypeInIl2Cpp<PlayerInterpolator>();
// ...
```

**Adding a new injected MonoBehaviour is a two-file change:** the class under `Scripts/`, and its registration line in `Plugin.Load()`. Forgetting the registration produces a component that is added without error and then never ticks — no exception, no log line.

Injected types also need an IL2CPP-visible constructor. The pattern the codebase relies on is a parameterless class; if you need an `IntPtr` ctor for a type Unity may re-instantiate, add `public T(IntPtr ptr) : base(ptr) { }`.

## Collections

`Il2CppSystem.Collections.Generic.List<T>` and BCL `System.Collections.Generic.List<T>` are unrelated types. Mixing them is the most frequent compile-then-crash mistake.

```csharp
// Game gives you an Il2Cpp list — copy it into a managed one before doing anything clever.
var managed = new List<StatModifier>();
foreach (var mod in weapon.upgradeOffer)   // Il2CppSystem list: iterate directly
{
    managed.Add(mod);
}
// now LINQ, serialization, storing across frames are all safe
```

Never store a raw Il2Cpp collection reference across frames — the native object can be freed underneath it.

## Delegates and game events

The game exposes callbacks as `Il2CppSystem.Action<T>`. `Plugin.cs` keeps the *original* delegate so it can restore it on teardown:

```csharp
private Il2CppSystem.Action originalDiedAction = null;
private Il2CppSystem.Action<WeaponBase> originalWeaponAddedAction = null;
```

Swap-and-restore, don't blindly overwrite: leaving your delegate installed after a run ends leaks into the next run and into singleplayer.

## Casting

```csharp
// WRONG — throws or corrupts
var enemy = (Enemy)collider.gameObject.GetComponent<Enemy>();

// RIGHT
var enemy = collider.gameObject.GetComponent<Enemy>()?.TryCast<Enemy>();
if (enemy == null) return;
```

`TryCast<T>()` returns null on failure. `Cast<T>()` throws. Prefer `TryCast` on anything derived from game data.

## Reverse engineering: the proxy lies by omission

When you need to know what a game method actually *does* — not just that it exists — you must go to the dump. The workflow is documented in the repo:

- `docs/reverse-engineering/00-decompilation-guide.md` — Il2CppDumper / Ghidra toolchain
- `docs/reverse-engineering/01-investigation-targets.md` — the current list of unverified assumptions

`dump.cs`, `script.json`, `il2cpp.h` and `DummyDll/` are **gitignored** (dump.cs is ~11.5 MB). They live locally only.

Any change that assumes semantics you inferred from a proxy signature must be labelled UNVERIFIED in the PR/commit until confirmed against the dump. `docs/netplay/01-critical-fixes.md` carries several such markers already — follow that convention.

## Common mistakes

1. **New MonoBehaviour without `RegisterTypeInIl2Cpp`** → silent dead component.
2. **`(T)x` instead of `x.TryCast<T>()`** → hard crash, no managed stack.
3. **LINQ on an Il2Cpp collection** → doesn't compile, or worse, resolves to an extension that marshals per element.
4. **Storing a native reference across frames** → use-after-free, crash minutes later somewhere unrelated.
5. **Overwriting a game `Action` without saving the original** → behaviour leaks into singleplayer after leaving a session.
6. **Reading a game string every frame** → marshalling cost; cache it.
7. **Trusting the proxy for behaviour** → the `giveCreditsTimer` class of bug. Check the dump.

## Related skills

- **harmony** — patching those same game types
- **unity** — MonoBehaviour lifecycle and pooling under IL2CPP
- **csharp** — where injected scripts and services live

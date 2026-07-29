---
name: unity
description: |
  Unity-side rules for the MegabonkTogether plugin — main-thread marshalling, injected MonoBehaviour lifecycle, coroutines, object pooling, and per-frame cost.
  Use when: writing or editing anything in src/plugin/Scripts/, spawning or despawning GameObjects, adding an Update loop, running work from a network callback, or chasing an FPS drop or pool-related exception.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Unity (under IL2CPP)

Only three UnityEngine modules plus UI/TMP are referenced (`CoreModule`, `PhysicsModule`,
`AnimationModule`, `AudioModule`, `ParticleSystemModule`, `UnityEngine.UI`, `Unity.TextMeshPro`).
If you need an API from another module you must add the reference *and* a stripped copy — see
**build-and-release**.

## Main thread is not optional

Network receive runs on LiteNetLib/socket threads. **Every Unity API call must happen on the main
thread.** Touching a `Transform` from a socket thread is an immediate hard crash with no managed
stack trace.

`Scripts/MainThreadDispatcher.cs` is the only bridge:

```csharp
public class MainThreadDispatcher : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> _executionQueue = new();

    public void Update()
    {
        while (_executionQueue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Plugin.Log.LogWarning(ex); }
        }
    }

    public static void Enqueue(Action action) => _executionQueue.Enqueue(action);
}
```

```csharp
// From a network handler / async continuation:
MainThreadDispatcher.Enqueue(() => Object.Destroy(go));
```

Note the dispatcher swallows exceptions into a warning — a bug inside an enqueued action will not
crash, it will silently not happen. When something "just doesn't apply", check for a warning here
first.

Two rules that follow:

- **Don't enqueue heavy work.** The queue drains fully every frame; a long action is a frame spike.
- **Don't enqueue per-packet closures in a hot path.** Each closure is an allocation. See
  `docs/netplay/04-performance-and-gc.md`.

## Injected MonoBehaviours

Everything in `Scripts/` is a managed MonoBehaviour injected into the IL2CPP runtime. Two-step:
write the class, then register it in `Plugin.Load()` with
`ClassInjector.RegisterTypeInIl2Cpp<T>()`. Missing the registration gives a component that is
added without error and never ticks. (Details in the **il2cpp** skill.)

Lifecycle conventions in this codebase:

- `Awake()` — resolve DI services, subscribe to `EventManager`. Never per-frame work.
- `Update()` — accumulator-driven; see the tick-rate pattern in `Scripts/NetworkHandler.cs`.
- Interpolators (`PlayerInterpolator`, `EnemyInterpolator`, `ProjectileInterpolator`,
  `BossOrbInterpolator`, `TumbleWeedInterpolator`) smooth remote state between network ticks.
  **Raise interpolation quality before raising a send rate** — it's free bandwidth-wise.
- Unsubscribe from `EventManager` on destroy, or a stale delegate keeps a dead object alive.

## Update loops

Never do work every frame that doesn't need to be every frame. The established pattern:

```csharp
private const float X_UPDATE_TICK_RATE = 20f;
private const float xUpdatetickInterval = 1f / X_UPDATE_TICK_RATE;
private float xUpdateAccumulator = 0f;

public void Update()
{
    xUpdateAccumulator += Time.deltaTime;
    if (xUpdateAccumulator < xUpdatetickInterval) return;
    xUpdateAccumulator = 0f;
    // ...
}
```

Guard clauses go first and cheapest-first — `NetworkHandler.Update()` bails on null services, no
match, and loading state before it touches `GameManager.Instance`.

## Distance throttling

`Helpers/DistanceThrottler.cs` classifies entities as `Close` / `Medium` / `Far` relative to the
local player and both skips updates and toggles renderers:

| Distance | Client | Host (`isServer: true`) |
|---|---|---|
| `Far` | renderer off, no update | still updates |
| `Medium` | renderer on, throttled to interval | updates every tick |
| `Close` | renderer on, every tick | every tick |

Any new synchronized entity type with a per-frame cost should route through a `DistanceThrottler`
instance. This mechanism was introduced in v3.0.0 specifically to fix FPS.

## Object pooling

Megabonk pools projectiles and objects itself. Returning an object to a pool that the game never
created throws — `Helpers/PoolHelper.cs` exists solely to construct the missing
`UnityEngine.Pool.ObjectPool<GameObject>` reflectively when that happens.

**When spawning a networked projectile or object on a client, route the despawn through
`PoolHelper` rather than `Object.Destroy`.** Destroying a pooled object leaves the game's pool
counter wrong and eventually starves spawning.

## Coroutines

`Helpers/CoroutineRunner.cs` is an injected MonoBehaviour that hosts coroutines for non-
MonoBehaviour code (services). Use it rather than adding a coroutine host to an arbitrary game
object — game objects get destroyed between runs and take the coroutine with them.

## Common mistakes

1. **Unity call off the main thread** → instant crash. Use `MainThreadDispatcher.Enqueue`.
2. **Silent failure inside an enqueued action** → the dispatcher logs a warning and continues.
3. **Missing `RegisterTypeInIl2Cpp`** → dead component, no error.
4. **`Object.Destroy` on a pooled object** → pool desync, spawn starvation later.
5. **Per-frame work without an accumulator** → across ~70 patches and several interpolators this
   is where FPS goes.
6. **Not unsubscribing from `EventManager`** → leaked objects across runs.
7. **Allocating in `Update()`** (new lists, closures, string interpolation) → GC spikes; see
   `docs/netplay/04-performance-and-gc.md`.

## Related skills

- **il2cpp** — type injection, casting, marshalling cost
- **netcode** — tick rates, interpolation vs send rate
- **harmony** — patching the game's own `Update`/`OnTriggerStay`

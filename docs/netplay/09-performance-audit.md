# Performance re-audit — where the frame time still goes

A fresh pass over the per-frame surface, after
[`04-performance-and-gc.md`](04-performance-and-gc.md)'s items 1A, 1C, 3 and 4 were fixed. That
document is the original analysis and its history; this one is what a second look found, ordered by
expected impact rather than by where it lives.

**Method, and its limit.** Static: every Harmony hook on an `Update`/`FixedUpdate`/`Tick`, then the
call graph out of each one, counting native interop crossings, allocations and per-item work.
**No profiler capture has ever been taken on this project**, so every number below is a count of
operations, not milliseconds. Where a claim is structural it says so; where it is a guess it says
that too.

---

## The five that matter

### 1. Three globally patched Unity properties — the largest single cost, and it may now be removable

`src/plugin/Patches/Unity/UnityComponent.cs` patches:

| Patch | Target |
|---|---|
| `UnityComponentPatches.get_transform_Prefix` | **`UnityEngine.Component.get_transform`** |
| `TransformPatches.get_position_Prefix` | **`UnityEngine.Transform.get_position`** |
| `TransformPatches.get_rotation_Prefix` | **`UnityEngine.Transform.get_rotation`** |

These are not patches on a game type. They are patches on the three most-called properties in
Unity, so **every `.transform`, every `.position` and every `.rotation` read anywhere in the
process** — the game's code, the mod's code, and Unity's own C# layer — takes a Harmony detour and
runs our prefix. At 600 enemies plus projectiles that is easily tens of thousands of detours per
frame.

Per call, while a session is up, each prefix does at minimum:

1. an interface call to `HasNetplaySessionStarted()` (cheap, a field compare);
2. `__instance == null` — which is **`UnityEngine.Object.op_Equality`, a native call**, not a
   managed reference compare;
3. `PeakNetplayerPositionRequest()` (see item 5).

**Why they exist:** two different reasons, both possibly spent.
- The `__instance == null` branch is the dangling-reference fallback from
  [P2-1](01-critical-fixes.md#p2-1). **Both of its root causes are now fixed** — the host-side
  `enemy.target` and the spectator camera's transform. `01-critical-fixes.md` already says "do not
  delete the fallback hack yet; confirm it goes quiet first". The counters are the confirmation.
- The `PeakNetplayerPositionRequest()` branch redirects the local player's `"Player"`, `"Hips"` and
  `"Renderer"` transforms to a netplayer while a remote weapon or attack is being simulated. That
  one is load-bearing and cannot simply be deleted — but it is only ever needed for **three named
  objects**, and it currently pays a queue check on every transform read in the process to find
  them.

**Two ways forward, in order of confidence:**

1. **Narrow the null-fallback out.** Once a 3-player session with mid-run disconnects reports zero
   `Transform fallbacks fired`, that branch is dead code and the `op_Equality` native call goes with
   it — from all three prefixes.
2. **Make the pending-request check free when nothing is pending.** Item 5 does half of this. The
   rest is to avoid entering the prefix body at all: Harmony cannot unpatch cheaply per-frame, but
   the mod *can* keep a single `volatile bool` set while any request is queued and return
   immediately when it is false, before touching the queue or Unity's `==`.

**Do not attempt either before the current branch is played.** Both depend on evidence this branch
is designed to produce.

### 2. `EnemyInterpolator` had one `MonoBehaviour.Update` per enemy — **fixed here**

The exact defect [`04` item 1A](04-performance-and-gc.md) fixed for `TargetSwitcher`, missed on the
interpolator. One is added to every enemy on the **client** (`OnReceivedSpawnedEnemy`), so a
600-enemy swarm meant ~600 injected-MonoBehaviour `Update()` calls per frame, each an IL2CPP
managed↔native crossing, and most returned immediately with fewer than two snapshots buffered.

Now ticked from a single `EnemyInterpolatorManager.Update`, mirroring `TargetSwitcherManager`
exactly — same registry, same O(1) swap-remove, same idempotent registration for pooled enemies,
same `Clear()` on teardown. `Time.timeAsDouble` is read once per frame for the whole sweep instead
of once per enemy, and a throwing tick can no longer stop every other enemy interpolating.

**This is the highest-confidence win in this document**, because it is the same change that 1A
already made on the host side, on the peer that is doing more work.

### 3. Remote projectiles re-applied their opacity every frame — **fixed here**

`ProjectileBasePatches.Update_Prefix` runs per projectile per frame and called
`UpdateProjectileOpacity`, which did:

- `GetComponentInChildren<ParticleOpacity>()` — a native hierarchy walk, **every frame**, for a
  component that cannot change; and
- `particleOpacity.Refresh(true)` on **both** branches — so a visible remote projectile spent the
  entire run re-applying an opacity it already had, plus two writes to
  `SaveManager.Instance.config.cfVisualsSettings.particle_opacity` on the hidden branch.

Now the component is cached per projectile (by instance id, dropped when the projectile is done and
on teardown) and `Refresh` fires only when the hide state actually changes — the same transition
tracking `DistanceThrottler` already uses for renderers.

### 4. `GetNetPlayerByWeapon` — per projectile, per frame — **not fixed**

The same `Update_Prefix` opens with:

```csharp
if (playerManagerService.GetNetPlayerByWeapon(__instance.weaponBase) == null) return;
```

and that method is:

```csharp
return spawnedPlayers.Values.FirstOrDefault(np => np.Inventory.weaponInventory.weapons.ContainsValue(weapon));
```

Per projectile per frame: a LINQ `FirstOrDefault` with a closure, and for each spawned player a
**`ContainsValue` scan of an Il2Cpp dictionary** — O(values), with every comparison crossing the
interop boundary. It is also called from `WeaponAttackPatches` on both the prefix and the postfix of
`SpawnProjectile`.

**Fix:** the answer is a property of the projectile, not something to re-derive every frame. Cache
it per projectile instance id at `TryInit` (where the owner is already being resolved) and read the
cache in `Update`. Left out of this pass because it wants the same instance-id cache the opacity fix
just introduced, and doing both in one commit would make a bad interaction hard to attribute.

### 5. `Time.frameCount` on the hottest path — **a regression this branch introduced, fixed here**

[P1-11](01-critical-fixes.md#p1-11) added a frame stamp to the netplayer-position queue and read
`Time.frameCount` at the top of `PeakNetplayerPositionRequest`. That method is called from all three
prefixes in item 1 — so a **native interop call** was added to every transform read in the process,
to answer a question that is "no" on the overwhelming majority of them.

Now the method returns immediately on `getNetplayerPositionRequestQueue.IsEmpty` (a managed field
read) and only reads the frame when something is actually queued.

Worth stating plainly: this is what happens when a correctness fix lands on a path nobody measured.
The fix itself was right; its placement was not.

---

## Smaller, all real

| Where | What | Status |
|---|---|---|
| `Enemy.ToModel` | Read `enemy.transform` twice — two patched-property detours per enemy, 40×/s on the host | **fixed** (cached local) |
| `EnemyMovementRb.GetTargetPosition_PostFix` | `GetComponent<Rigidbody>()` per enemy per movement tick, purely to reach `rigidbody.transform.position` — which is the player's own transform | **fixed** (removed) |
| `ProjectileBase.ToModel` | Three `GetComponent<T>()` calls per projectile per projectile tick (20 Hz) just to identify the subtype | not fixed — cache the subtype at spawn |
| `EnemyManagerService.GetAllEnemiesDeltaAndUpdate` | Allocates a `Dictionary<uint, EnemyModel>` **and up to 600 `EnemyModel` objects every tick, 40×/s** — ~24,000 allocations/second on the host, all immediately garbage | not fixed — see below |
| `EnemyMovementRb` | `IsServerMode()` in seven prefixes, per enemy per tick | not worth fixing — it is a nullable-bool field read behind one interface call |
| `DynamicData.For(...)` | 128 call sites, 59 of them inside patches. `GetTargetPosition_PostFix` does `DynamicData.For(enemy).Get<uint?>("targetId")` **per enemy per movement tick** — a MonoMod table lookup, a string-keyed dictionary hit and a boxed `uint?` | not fixed — see below |

### The two structural ones

**`DynamicData` as per-enemy hot storage.** It is the right tool for attaching data to a game object
you do not own, and the wrong tool for reading that data every tick. Replacing the `"targetId"` read
with a `Dictionary<int, uint>` keyed by instance id — the pattern already used by
`EnemiesDistanceThrottler` and now by the projectile opacity cache — removes a string hash, a table
walk and a box from the per-enemy movement path. The write sites can keep `DynamicData` for
compatibility, or move wholesale; the read is what matters.

**`EnemyModel` churn.** `GetAllEnemiesDeltaAndUpdate` rebuilds the world every tick to compute a
delta and throws the result away. A pooled model array, or a struct `EnemyModel`, or keeping the
previous snapshot in a reusable buffer, all remove the same 24k allocations/second. **This is the
one item in this document that a GC-Alloc capture would settle immediately** — and no capture has
ever been taken (`04` item 2's verification is still outstanding for the same reason).

---

## What to measure first, and why in this order

1. **A Unity Profiler capture on the host during a final swarm at 3+ players.** It settles the
   `EnemyModel` churn and confirms or kills the `DynamicData` claim, and it is the outstanding
   verification for `04` items 1A, 1C and 2 as well. One capture answers five open questions.
2. **The `Transform fallbacks fired` counters over a 3-player session with mid-run disconnects.**
   Zero across the session is the licence to delete the null-fallback branch from three globally
   patched properties, which is the largest single change available (item 1).
3. **A client-side frame-time comparison across the interpolator change** (item 2). It is the
   cleanest before/after available, because the change is confined to one component and the client
   is where the FPS reports come from (upstream #77).

Until at least (1) exists, everything here is arithmetic on operation counts. It is good arithmetic
— an unconditional `Refresh(true)` per projectile per frame is wrong regardless of what a profiler
says — but it is not measurement, and this document should not be read as if it were.

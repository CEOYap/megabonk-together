# Performance and GC

Where the frames actually go at 400–600 enemies, ordered by expected payoff.

Everything here is from source reading — **no profiling was performed**. Treat the ordering
as a hypothesis to validate with the Unity Profiler, not as measured fact. The allocation
sites themselves are confirmed by inspection.

---

## Context

`NETPLAY_CHANGES.md` sets the mod's enemy caps:

| Players | Cap | Note |
|---|---|---|
| 1 | 400 | described as "original game cap" — **incorrect, see below** |
| 2–4 | 500 | |
| 5–6 | 600 | *"untested, you have been warned"* |
| any, final swarm | 400 | *"keeping the original cap"* — **also incorrect** |

Every managed-side per-enemy cost is multiplied by that number, every frame.

> **The vanilla baseline in that table is wrong.** `EnemyManager.GetNumMaxEnemies`
> (VA `0x180419D60`) returns **550** normally, **400** during the final swarm, and **300** past
> a time threshold within it — confirmed by decompilation. 400 is the final-swarm value, not
> the general cap.
>
> Two consequences for this document's premise:
>
> - **The 2–4 player cap of 500 is *below* vanilla's 550.** If the intent was "more players,
>   more enemies", that setting does the opposite. Only the 5–6 cap of 600 actually raises it.
> - The per-frame multiplier this whole document reasons about is therefore **550 at baseline**,
>   not 400 — so single-player costs are ~37% higher than assumed here, and the gap between
>   single-player and 6-player density is much smaller than the table implies.
>
> Re-tune against 550 / 400 / 300 before acting on any density-derived estimate below.

> `Sea-Bass-cmd/optimized-netplay` raises the final-swarm cap to 700/800. Rejected — and the
> game *lowers* the cap at that moment deliberately. See
> [`03-cherry-pick-guide.md`](03-cherry-pick-guide.md#final-swarm-cap-400--700800).

---

## What upstream already fixed

`bd9518c` ("feat: more code optimizations") is genuinely good work and is already on our
`main`. Do not regress it:

**`Patches/Enemies/Enemy.cs`** — `AllowedDamageSource` changed from `string[]` to
`HashSet<string>`:

```csharp
- public static readonly string[] AllowedDamageSource = Enum.GetNames(typeof(EItem));
+ public static readonly HashSet<string> AllowedDamageSource = new(Enum.GetNames(typeof(EItem)));
```

O(n) linear scan → O(1) hash lookup, on a per-damage-event path. At 600 enemies taking
damage this is the difference between a scan of every `EItem` name per hit and a single hash.

**`Services/EnemyManagerService.cs`** — `GetAllEnemiesDeltaAndUpdate` rewritten:

```csharp
- var currentEnemies = spawnedEnemies.Select(kv => kv.Value.ToModel(kv.Key)).ToList();
- previousSpawnedEnemiesDelta = currentEnemies.ToDictionary(e => e.Id);
+ var currentEnemies = new Dictionary<uint, EnemyModel>(spawnedEnemies.Count);
+ foreach (var (id, enemy) in spawnedEnemies) currentEnemies[id] = enemy.ToModel(id);
+ previousSpawnedEnemiesDelta = currentEnemies;
```

Removes a `List` + a `Dictionary` + two LINQ iterators per network tick, on the per-enemy
delta path. Also pre-sizes the dictionary, avoiding rehash growth at 600 entries.

Also adds a `DistanceThrottler` and moves a `GetComponentInChildren<Renderer>()` call out of a
boss-only branch into `InitEnemy`.

---

<a name="targetswitcher"></a>
## 1. `TargetSwitcher` — the dominant managed cost

**Files:** `src/plugin/Scripts/Enemies/TargetSwitcher.cs`,
`src/plugin/Scripts/Enemies/TargetSwitcherManager.cs`
**Status:** CONFIRMED (allocation sites), LIKELY (that this dominates) — **A and B FIXED, C open.
Neither fix is measured.**

Not touched by any fork. This was the biggest remaining win.

> **No profiler capture has been taken, before or after.** Both fixes are justified structurally
> — removing ~600 interop crossings per frame, and removing four allocations per switch — but
> "this dominates" remains LIKELY, and the improvement is unquantified. A GC Alloc + CPU capture
> during a final swarm at 4+ players is still the outstanding task for this whole document.

### Problem A — one `MonoBehaviour.Update` per enemy

`TargetSwitcher` is added to every enemy on the host (`Enemy.cs`, `init_PostFix`). At 600
enemies that is **600 managed `Update()` calls per frame**, each crossing the IL2CPP
managed↔native boundary. In Il2CppInterop-injected MonoBehaviours this crossing is
substantially more expensive than a native Unity `Update`.

Most of those calls do nothing — `Update` accumulates a timer and returns:

```csharp
private void Update()
{
    if (enemy == null) return;
    timer += Time.deltaTime;
    if (timer >= delay) { /* ... */ }
}
```

**Fix:** replace the per-enemy `Update` with a single manager that ticks all switchers from
one `Update`, ideally slicing the work across frames (e.g. 1/4 of the registered switchers per
frame — the switch interval is 2–6 s, so quarter-rate resolution is imperceptible).

```csharp
// Sketch: one MonoBehaviour, N plain objects
internal sealed class TargetSwitcherManager : MonoBehaviour
{
    private readonly List<TargetSwitcherState> switchers = new(700);
    private int cursor;

    private void Update()
    {
        if (switchers.Count == 0) return;

        // process ~1/4 of the list per frame
        int budget = Mathf.Max(1, switchers.Count / 4);
        float dt = Time.deltaTime * 4f;   // compensate for the slice rate

        for (int i = 0; i < budget; i++)
        {
            cursor = (cursor + 1) % switchers.Count;
            switchers[cursor].Tick(dt);
        }
    }
}
```

This also removes 600 injected-MonoBehaviour instances, which is itself a memory and
`AddComponent` cost at spawn.

### FIXED — not measured

`TargetSwitcherManager` (`src/plugin/Scripts/Enemies/TargetSwitcherManager.cs`) ticks every
registered switcher from one `Update`. `TargetSwitcher.Update` became `internal void Tick(float)`,
so the managed↔native crossing happens once per frame instead of once per enemy.

**Three deliberate departures from the sketch above.**

**1. No frame-slicing.** The sketch proposed 1/4 of the list per frame with a compensated delta.
The cost being removed is the interop crossing, not the arithmetic — `Tick` is an add and a
compare for all but a handful of entries, and delays are randomised 2–6s so firings already
spread themselves. Slicing would add cursor bookkeeping and an approximated delta for no measured
gain. Slice later if profiling says the loop itself matters.

**2. `TargetSwitcher` stays a MonoBehaviour** rather than becoming a plain object. Keeping it a
component means Unity's own enable/disable still governs it, so a pooled enemy being deactivated
stops ticking exactly as before. As a plain object in a list, that would need an explicit
`activeInHierarchy` check per entry per frame — a marshalled call, reintroducing the cost being
removed. The consequence is that the sketch's secondary win (600 fewer component instances) is
**not** realised.

**3. Registration is doubled up.** `OnEnable` is the intended hook, but **no other injected
MonoBehaviour in this repo uses `OnEnable`/`OnDisable`**, so there is no local evidence
Il2CppInterop wires them. If `OnEnable` silently did not fire, nothing would register and target
switching would stop entirely — a silent dead component. So `StartSwitching` also registers
(it is called unconditionally from `Enemy.init_PostFix`), and `Register` is idempotent.
`OnDestroy` unregisters as well; unlike `OnEnable`, private `OnDestroy` is already relied on by
`NetPlayer` and `NetPlayersDisplayer`, so that path is known-good. The manager also drops
Unity-null entries when it ticks.

Registry removal is an O(1) swap-remove using an index stored on the switcher — a linear
`List.Remove` would be O(n) per despawn, and despawns are frequent at a swarm.
`NetworkHandler.ResetNetworking()` clears the registry between sessions.

### Found while fixing: switchers accumulated on pooled enemies

`Enemy.init_PostFix` called `gameObject.AddComponent<TargetSwitcher>()` **unconditionally**, and
nothing ever removed one. Enemies are pooled — the same method opens by calling
`EnemiesDistanceThrottler.Cleanup(GetInstanceID())` and force-re-enabling the renderer, neither
of which makes sense except for a recycled GameObject — so every time an enemy came back out of
the pool it gained *another* `TargetSwitcher`, each ticking independently and each fighting the
others to set `targetId`.

That makes the "600 `Update` calls" figure a floor rather than the real number, and it means the
enemy's target was being reassigned several times per switch interval by competing switchers.

Now guarded with a `GetComponent` first. **Pooling is inferred, not confirmed against the dump —
LIKELY, not CONFIRMED.** The guard is correct either way; if enemies are not pooled, it is a
no-op costing one `GetComponent` per spawn.

### Problem B — allocations per target switch

```csharp
// TargetSwitcher.cs:60 and :80
var alives = playerManagerService.GetAllPlayersAlive().ToList();
```

Per switch, per enemy, this allocates:
- a `Player[]` inside `GetAllPlayersAlive()` (see [P1-4](01-critical-fixes.md#p1-4))
- two LINQ iterator objects (`Where` + `Select`)
- a `List<Player>` from `.ToList()`

With a 2–6 s interval and 600 enemies that is roughly 150–300 switches/second × 4 allocations.

**Fix:** use the non-allocating accessor from [P1-4](01-critical-fixes.md#p1-4) and drop the
`.ToList()`:

```csharp
var alives = playerManagerService.GetAllPlayersAliveNonAlloc();
if (alives.Count == 0) return;
var selectedPlayer = alives[Random.Range(0, alives.Count)];
```

**FIXED** alongside [P1-4](01-critical-fixes.md#p1-4), in both `PickANewTarget` and
`PickACloseTarget`. Not measured.

### Problem C — `PickACloseTarget` is O(enemies × players)

```csharp
foreach (var player in alives)
{
    // ... resolves netplayer, then:
    var distance = Vector3.Distance(enemy.transform.position, target.transform.position);
}
```

`Vector3.Distance` computes a square root. Comparing distances does not need one — use
`(a - b).sqrMagnitude` and compare against a squared threshold. Same for `CanSwitch()`:

```csharp
private bool CanSwitch()
{
    if (enemy.transform == null) return false;
    float sqrDistance = (enemy.transform.position - currentTarget.transform.position).sqrMagnitude;
    return sqrDistance <= switchMaxDistance * switchMaxDistance;
}
```

Also: each `enemy.transform` access is a native property call through interop. Cache the
`Transform` once in `StartSwitching` rather than re-fetching per comparison.

### FIXED — not measured

All three changes are in: `enemyTransform` cached in `StartSwitching` (the only place `enemy`
changes; a `Transform` outlives pooling and deactivation, so the pair cannot drift), the enemy's
position hoisted out of the `PickACloseTarget` loop, and both distance comparisons switched to
`sqrMagnitude` against a squared threshold.

**A correctness bug was sitting in the same loop.** Both `PickANewTarget` and `PickACloseTarget`
did `netplayer.Model.transform` on the result of `GetNetPlayerByNetplayId` without a null check. A
player can be in the alive set with no spawned `NetPlayer` — most obviously in the window where one
peer has processed a disconnect and another has not, which is [P1-8](01-critical-fixes.md#p1-8)'s
race. That is an NRE on a path that runs per enemy every 2-6 seconds. Both now skip the candidate
and keep the existing target rather than clearing it.

No profiler capture. The claim here is "fewer native calls and no square roots", which is
structural; whether it shows up in a frame-time capture at 600 enemies is unmeasured.

---

<a name="getallplayersalive"></a>
## 2. `GetAllPlayersAlive()` — allocation at the source

**File:** `src/plugin/Services/PlayerManagerService.cs:133-136`
**Status:** CONFIRMED

```csharp
public IEnumerable<Player> GetAllPlayersAlive()
{
    return [.. players.Where(p => p.Value.Hp > 0).Select(p => p.Value)];
}
```

Three allocations per call, and it is called from a lot of places that do not need a
materialised collection — most of them only want `.Count()`.

Call sites:

| Caller | Frequency |
|---|---|
| `GameBalanceService.PlayersCount` | every balance query |
| `TargetSwitcher.PickANewTarget` / `PickACloseTarget` | per enemy per switch |
| `FinalFightController` L111/136/161 | per final-fight tick |
| `SynchronizationService` L2939-2940 | **twice on consecutive lines** |

Full fix in [P1-4](01-critical-fixes.md#p1-4). Add `GetAlivePlayerCount()` (no allocation at
all) and `GetAllPlayersAliveNonAlloc()` (reused buffer).

The `SynchronizationService` double-call is worth fixing on its own:

```csharp
// current — allocates twice
var rand = UnityEngine.Random.Range(0, playerManagerService.GetAllPlayersAlive().Count());
var randomPlayer = playerManagerService.GetAllPlayersAlive().ElementAt(rand);

// fixed
var alives = playerManagerService.GetAllPlayersAliveNonAlloc();
if (alives.Count == 0) return;
var randomPlayer = alives[UnityEngine.Random.Range(0, alives.Count)];
```

`.ElementAt()` on an `IEnumerable` also walks the sequence; on the array it is O(1), but only
because of a runtime type check.

---

## 3. `GameBalanceService` — recomputed per call

**File:** `src/plugin/Services/GameBalanceService.cs`
**Status:** CONFIRMED

Every getter recomputes from scratch:

```csharp
private int PlayersCount => playerManagerService.GetAllPlayersAlive().Count();
private static int StageIndex => MapController.runConfig?.mapData.stages.IndexOf(MapController.currentStage) ?? 0;
```

`StageIndex` does an `IndexOf` over a list on every access. `PlayersCount` allocates.

These change **only** when a player joins, a player dies, or the stage changes. There is
already an `Initialize()` method and an `EventManager`. Cache:

```csharp
private int cachedPlayersCount;
private int cachedStageIndex;
private DifficultyLevel cachedDifficulty;

public void Initialize()
{
    RecomputeCache();
    // subscribe to player-joined / player-died / stage-changed and call RecomputeCache()
}

private void RecomputeCache()
{
    cachedPlayersCount = playerManagerService.GetAlivePlayerCount();
    cachedStageIndex   = MapController.runConfig?.mapData.stages.IndexOf(MapController.currentStage) ?? 0;
    cachedDifficulty   = ComputeDifficultyLevel(cachedPlayersCount);
}
```

### FIXED — `StageIndex` only; `PlayersCount` deliberately left alone

`StageIndex` is now memoised against the current stage's native pointer: one pointer comparison
replaces an `IndexOf` over an Il2Cpp list (an interop equality call per element) on every access.

**Keyed on the pointer, not the managed wrapper.** Il2CppInterop usually hands back the same proxy
instance for a given pointer, but `Il2CppObjectBase` does not overload `==`, so a wrapper-identity
comparison would have silently never hit and left the cost exactly where it was — while looking
fixed.

**The game may already have this.** `MapController.GetStageIndex()` — static, returns `Int32` —
is present in `Assembly-CSharp`'s interop metadata. If it indexes the same list, it replaces the
memo entirely. **UNVERIFIED and therefore not used:** the stripped assemblies carry no method
body, and if it indexes global progression rather than position within the map's stage list, every
difficulty multiplier shifts silently. Resolve it against `dump.cs` — this is a cheap, high-value
check for whoever next has the dump open.

**Not the event-driven cache this doc proposed.** A subscription that misses one stage change
serves a wrong difficulty silently; memoising on the value's own identity cannot go stale. The same
argument does not rescue `PlayersCount`, which has no cheap identity to compare — but after
[P1-4](01-critical-fixes.md#p1-4) it is a non-allocating loop over at most six players, so it was
left as it is. Caching it would need invalidation on join, death, disconnect and revive, and a
missed one means enemies spawn with the wrong HP.

This matters most if `BaseSummoner` is ever re-enabled — that patch calls
`GetCreditsTimerMultiplier()` on every `Tick`, **on every summoner**. `SummonerController` holds
a `List<BaseSummoner>` with five subclasses (`dump.cs:372136`), so the allocation is per-summoner
per-frame, not once per frame.

Decompilation also shows `Tick` already calls `EnemyManager.HasMaxEnemies()` and
`GetMultiplier()` on the credit-grant path, so the game does its own per-tick work here — one
more reason to make our additions cheap. See
[`../reverse-engineering/01-investigation-targets.md`](../reverse-engineering/01-investigation-targets.md#basesummoner)
before enabling it at all.

---

## 4. `EnemyManagerService` — remaining LINQ

**File:** `src/plugin/Services/EnemyManagerService.cs`
**Status:** CONFIRMED

`bd9518c` fixed the delta path. These remain:

**`GetEnemyByReference` (L199)** — linear scan with a closure, per call:

```csharp
return spawnedEnemies.FirstOrDefault(kv => kv.Value == enemy);
```

O(n) over up to 600 entries, plus a `ConcurrentDictionary` snapshot enumerator. Called from
the retarget path. **Fix:** maintain a reverse index `Dictionary<Enemy, uint>` alongside
`spawnedEnemies`, updated in `AddSpawnedEnemy` / removal. Or, better, read the ID from the
per-enemy net state you already store rather than searching for it.

**Retarget path (L52-86)** — runs when a player dies:

```csharp
var oldTargetEnemies = spawnedEnemies.Values.Where(enemy => { ... }).ToList()  // implied
var randomIndex = Random.Range(0, currentPlayersAliveExcludingOldOneId.Count());
var randomNewTargetId = currentPlayersAliveExcludingOldOneId.ElementAt(randomIndex);
var playerRigidbody = playerId_rigidbody.FirstOrDefault(pr => pr.Item1 == newTargetId).Item2;
```

`.Count()` then `.ElementAt()` on the same sequence walks it twice. `FirstOrDefault` inside a
loop over enemies is O(enemies × players). This fires once per player death, so it is a spike
rather than sustained cost — but at 600 enemies it is a visible hitch at exactly the wrong
moment. Materialise once outside the loop, and build a `Dictionary<uint, Rigidbody>` instead
of scanning `playerId_rigidbody`.

### FIXED — and `GetEnemyByReference` was already done

**`GetEnemyByReference`** already takes the reverse-lookup route this entry asks for: it reads
`netplayId` off the enemy's `DynamicData` and only falls back to the linear scan if that is missing
or stale. This entry was out of date; the fallback is kept as a safety net, not a hot path.

**`ApplyRetargetedEnemies`** is fixed: `playerId_rigidbody` is indexed into a
`Dictionary<uint, Rigidbody>` once, instead of a `FirstOrDefault` with a fresh closure per enemy.
That was the O(enemies × players) half, on the death and disconnect paths.

**`ReTargetEnemies`'s `.Count()`/`.ElementAt()`** was already fixed by
[P1-6](01-critical-fixes.md#p1-6), which materialises the candidate list once and indexes it.

---

## 5. Network payload

**Status:** CONFIRMED (thresholds), UNVERIFIED (byte counts — nothing measures them today)

The dominant traffic is the per-tick enemy delta from `SendEnemiesUpdate`.

**Thresholds** — `EnemyManagerService.cs:42-43`:

```csharp
private const float POSITION_TRESHOLD = 0.1f;
private const float YAW_TRESHOLD = 5.0f;
```

An enemy is included in the delta if it moved 0.1 units or rotated 5°. At 600 enemies in a
swarm, essentially all of them qualify every tick, so the delta is effectively a full snapshot.
Raising `POSITION_TRESHOLD` is the cheapest bandwidth lever available and carries **zero
correctness risk** — the tradeoff is purely visual smoothness on clients, and the
interpolators (`EnemyInterpolator`) already smooth between updates.

**Quantization** — enemy positions are full-precision `float`. Multibonk sends `short`
quantized values: 6 bytes per position instead of 12. Halving the dominant payload is a
larger win than anything the Sea-Bass branch attempted, and it costs no reliability.

**Distance culling** — `bd9518c` added a `DistanceThrottler`. Extend it to be *per-peer*:
an enemy 200 units from client B does not need per-tick updates sent to B. Requires
`SendToAllClients` to become a per-peer loop, which is a real refactor — but it is the change
with the largest headroom at 6 players.

**Before tuning any of this, add byte counters.** `UdpClientService` already tracks latency
(`GetLatency(uint connectionId)`); add per-message-type byte totals so changes can be
measured rather than guessed.

---

## 6. Things not to do

| Anti-fix | Why |
|---|---|
| Downgrade event RPCs to `Unreliable` | Trades correctness for bandwidth. See [`02-delivery-method-reference.md`](02-delivery-method-reference.md) |
| `NetEntity` as implemented in the Sea-Bass fork | Slower than `DynamicData` at most call sites; `AddComponent`/`Destroy` churn on pooled objects — **pooling now confirmed**: 40+ `ObjectPool<GameObject>` and `DespawnPickup` calling `Release`. See [`03-cherry-pick-guide.md`](03-cherry-pick-guide.md#netentity) |
| Raise the final-swarm cap | Makes every item above worse, at the worst moment — and the game deliberately *lowers* it there (400, then 300) |
| Re-enable `BaseSummoner` unmodified | Adds per-summoner-per-tick allocation, and compounds credit income to ~2–3× rather than the advertised few percent. Postfix `GetMultiplier()` instead if the lever is wanted |
| Add `is ICollection<T>` tests at call sites | Saves one enumerator while leaving the array and iterators. Fix the source |

---

## Measurement checklist

Before claiming any of this works:

1. **Unity Profiler → GC Alloc**, host machine, final swarm at 4+ players. Record bytes/frame
   before and after each change individually.
2. **Deep Profile** for one capture to attribute costs to `TargetSwitcher.Update` vs
   `SendEnemiesUpdate` vs game code. Deep profiling distorts absolute numbers — use it for
   attribution only.
3. **Frame-time histogram, not average.** Micro-stutter is a p99 problem; a mean FPS number
   will hide it entirely.
4. **Bandwidth counters** per message type, both directions, at 2 / 4 / 6 players.
5. **Under packet loss** (`clumsy` / `tc netem` at 3%) — some "performance" problems are
   retransmit storms, and some correctness problems only appear here.

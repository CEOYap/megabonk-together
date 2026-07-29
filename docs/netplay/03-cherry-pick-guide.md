# Cherry-Pick Guide: `Sea-Bass-cmd/optimized-netplay`

Hunk-level guidance for extracting the useful parts of that fork. The full reasoning is in
[`../AUDIT_optimized-netplay.md`](../AUDIT_optimized-netplay.md); this document is the
operational version.

**Bottom line: do not merge, and do not cherry-pick at commit granularity.** Every commit
that contains something worth having also contains something that must not ship.

---

## Setup

The fork is a standalone repository, not a branch of upstream. Add it as a read-only remote —
do not track it, do not merge it.

```bash
git remote add seabass https://github.com/Sea-Bass-cmd/optimized-netplay.git
git fetch seabass
git log --oneline 50b30a4..seabass/main
```

Merge base is `50b30a4` — the same commit our `main` builds on, minus our two Fcornaire
commits.

To read a specific change side by side:

```bash
git difftool 50b30a4 seabass/main -- src/plugin/Services/SynchronizationService.cs
```

---

## Two mechanical blockers

### 1. An 11.5 MB blob in the history

`45ce3f5` adds `dump.cs` (12,088,070 bytes); `8628e71` removes it. Cherry-picking `45ce3f5`
alone **permanently adds 11.5 MB to our history**. If you ever take it:

```bash
git cherry-pick -n 45ce3f5
git rm --cached dump.cs
git commit
```

or take both commits and squash. In practice you should take neither — see the table below.

### 2. Five files conflict with our `main`

`bd9518c` and `041881b` touch the same files:

| File | Ours | Theirs |
|---|---|---|
| `src/plugin/Services/EnemyManagerService.cs` | `bd9518c` — real hot-path optimization | mechanical `DynamicData` → `NetEntity` |
| `src/plugin/Patches/Enemies/Enemy.cs` | `bd9518c` — `HashSet` lookup, renderer/throttler move | collapses the target branch, **drops a line** |
| `src/plugin/Services/SynchronizationService.cs` | `041881b` — chest diagnostics | the bulk of their diff |
| `src/plugin/Patches/Unity/UnityComponent.cs` | `bd9518c` | adds a `LogWarning` |
| `src/plugin/Plugin.cs` | `bd9518c` | `ClassInjector.RegisterTypeInIl2Cpp<NetEntity>()` |

**Ours are better** for `EnemyManagerService.cs` and `Enemy.cs`. Fcornaire's `bd9518c`
replaces `string[]` with `HashSet<string>` for `AllowedDamageSource` (O(n) → O(1) on a
per-damage-event lookup) and rewrites `GetAllEnemiesDeltaAndUpdate` from
`Select().ToList().ToDictionary()` to a pre-sized `Dictionary` + `foreach` — the actual
per-tick, per-enemy hot path. Do not let a cherry-pick overwrite that.

---

## Commit-by-commit disposition

| # | Commit | Contains | Disposition |
|---|---|---|---|
| 1 | `b00accb` Add NetEntity component | `NetEntity.cs` (36 lines) | **REJECT** — see [NetEntity](#netentity) |
| 2 | `7acbc94` Refactor GameBalanceService | LINQ tweak (no-op) + final swarm cap 400 → 700/800 | **REJECT** both |
| 3 | `96e1230` Refactor SynchronizationService | charging fixes **+** the `Unreliable` downgrade | **SPLIT** — hand-port the charging fixes only |
| 4 | `2b4191a` Global NetEntity refactor | 33 files, mechanical; drops `__instance.target` in `Enemy.cs` | **REJECT** — but take the null guard by hand |
| 5 | `0c7e313` Fix 4 remaining TODOs | fake concurrency fix, `Task.Run` removal, `DataManager.cs` deletion, stray `.py` | **REJECT** — implement the real fix yourself (P0-3) |
| 6 | `8e02879` Fix 5 edge cases | `BaseSummoner` re-enable **+** the `LogWarning` | **SPLIT** — take the log line only, rate-limited |
| 7 | `79c8bfb` Delete fix_concurrency.py | — | skip |
| 8 | `8a06bfc` Delete global_refactor.py | — | skip |
| 9 | `269396f` Fix encounter desyncs, legendary shrines | host relays (echo bug) + golden shrine (wire break) + more charging fixes | **SPLIT** — take all three, each with a correction |
| 10 | `74c21d1` Register NetEntity in IL2CPP | one line | **REJECT** — meaningless without `NetEntity` |
| 11 | `45ce3f5` NetEntity IL2CPP fix | also adds 11.5 MB `dump.cs` | **REJECT** |
| 12 | `8628e71` Remove accidental dump.cs | — | skip |

Net: **zero commits are safe to cherry-pick.** Everything worth taking is hand-ported.

---

## What to take, and how

### TAKE — charging state machine fixes

From `96e1230` and `269396f`. These are the highest-value changes in the fork.

Do **not** apply their version — it ships with the `Unreliable` downgrade in the same hunks,
adds the helpers to the public interface, and keeps the O(n) `FirstOrDefault` scan. Implement
them from the descriptions in [`01-critical-fixes.md`](01-critical-fixes.md), fixes
[P0-1](01-critical-fixes.md#p0-1) and [P0-2](01-critical-fixes.md#p0-2), which capture the
same logic with those three problems corrected.

```bash
git checkout -b fix/charging-state-machine main
```

Keep every `DeliveryMethod` as `ReliableOrdered`.

### TAKE (with correction) — host relay for XP / gold / encounter-close

From `269396f`. The hole is real: client-originated XP, gold, and encounter-close never reach
other clients.

Their fix uses `SendToAllClients`, which echoes to the sender. `ChangeGold` applies a delta,
so the sender double-applies it — a gold duplication exploit. Use `SendToAllClientsExcept`.
Full detail in [P1-1](01-critical-fixes.md#p1-1).

```bash
git checkout -b fix/host-relay-xp-gold main
```

Test explicitly for the duplication: client picks up gold, assert their total increases
exactly once.

### TAKE (with prerequisite) — legendary shrine sync

From `269396f`. Real gap — `ChargeShrine.isGolden` is never transmitted.

Their change adds `bool? IsGoldenShrine` to `Specific` with no version gate. `MemoryPack` is
positional, so this silently corrupts `SpawnedObject` between mismatched builds. Land
[P1-3](01-critical-fixes.md#p1-3) (protocol version gate) **first**, then
[P1-2](01-critical-fixes.md#p1-2).

> **Their `ChargeShrine.Start` postfix: RESOLVED — do not take it.** `Start` was decompiled in
> full (VA `0x1804C2A60`) and **never touches `isGolden`, nor reads `goldChance`**. It only
> does visual setup: rotation, alpha, two `MaterialPropertyBlock`s, a `Random.ColorHSV` call for
> the rune-stone's *cosmetic* colour, and the spawn action. The ordering hazard the postfix
> guards against does not exist, so the message change alone is sufficient. See
> [`../reverse-engineering/01-investigation-targets.md`](../reverse-engineering/01-investigation-targets.md#chargeshrine).

```bash
git checkout -b feat/sync-golden-shrine main
```

### TAKE — null guard on `GetNetPlayerByNetplayId`

From `2b4191a`, `Patches/Enemies/Enemy.cs`. Their rewrite adds a genuine null check —
`GetNetPlayerByNetplayId` can return `null` during a join or after a disconnect, and the
current code dereferences it unguarded.

**But their rewrite also deletes `__instance.target = randomPlayer.Rigidbody;`**, leaving the
enemy's physics target on the host while the network says otherwise. `TargetSwitcher.Update`
repairs it, but only after a random 2–6 s delay, so every freshly-spawned enemy beelines at
the host first.

Take the guard, keep the assignment. See [P0-4](01-critical-fixes.md#p0-4). Apply on top of
our `bd9518c` version of the file, not theirs.

### TAKE (rate-limited) — dangling transform warning

From `8e02879`, `Patches/Unity/UnityComponent.cs`. Replaces a `¯\_(ツ)_/¯` comment with a
diagnostic. Useful — the hack is unexplained and produces no evidence today.

Their version logs unconditionally. That path can fire per-frame per-affected-object, making
it a per-frame string allocation plus BepInEx disk I/O. Rate-limit it. See
[P2-1](01-critical-fixes.md#p2-1).

### IMPLEMENT YOURSELF — atomic ID allocation

`0c7e313`'s message claims this. It did not happen — the automated script's second regex
failed to match, so only the `//TODO: concurrency?` comments were deleted while
`currentEnemyId++` remained. `git grep Interlocked` across `seabass/main`'s `src/plugin`
returns nothing.

Write it properly: [P0-3](01-critical-fixes.md#p0-3).

```bash
git checkout -b fix/atomic-netid-allocation main
```

---

## What to reject, and why

### The `Unreliable` downgrade (17 send sites)

The single largest liability in the fork. Every affected message is a one-shot,
non-idempotent state transition with no resend and no reconciliation path. It also directly
reverts upstream `24f5004`, which tuned these deliberately.

Full analysis: [`02-delivery-method-reference.md`](02-delivery-method-reference.md#the-sea-bass-cmd-downgrade--do-not-apply).

<a name="netentity"></a>
### The `NetEntity` refactor

Commits `b00accb`, `2b4191a`, `74c21d1`, `45ce3f5`. Replaces MonoMod `DynamicData` with a
`NetEntity` MonoBehaviour plus a static `Dictionary<int, NetData>` keyed on
`gameObject.GetInstanceID()`.

The *idea* is good — typed fields beat string-keyed reflection. Four problems with the
execution:

1. **The access path is slower than what it replaces.** `GetOrAddNetEntity()` is
   `GetComponent<NetEntity>()` → possible `AddComponent` → instance-ID lookup. For an
   IL2CPP-*injected* type, `GetComponent<T>` goes through Il2CppInterop generic resolution
   and allocates a wrapper.
2. **The refactor deleted every cached accessor.** The old code hoisted
   `var dyn = DynamicData.For(obj);` and reused it. `global_refactor.py` rewrote each
   `.Get`/`.Set` independently, so read-then-write sites now run the whole chain twice — 5×
   in `GenerateTileObjects.cs`, 4× in `SpawnInteractables.cs`, ~15× in the projectile spawn
   switch in `SynchronizationService.cs`.
3. **Component-level keying collapsed to GameObject-level.** `DynamicData.For(component)`
   keyed the component. The `Component` overload of `GetOrAddNetEntity` forwards to
   `comp.gameObject`, so **every component on a GameObject now shares one `NetData`**. Check
   `PoolManager.cs`, which reads `weaponBase`'s `OwnerId` and writes `__result`'s.

   > **Correction from decompilation:** the `WeaponUtility` half of this concern was
   > mis-stated. `DamageContainer` is a **plain managed class, not a `Component`**
   > (`dump.cs:374151`), so it can never share a GameObject and GameObject-level keying is not
   > what breaks it. The real defect is worse and applies to `DynamicData` too — see
   > [P1-5](01-critical-fixes.md#p1-5): `WeaponUtility.GetDamageContainer` ignores its
   > `recycleDc` argument and returns a **static** container, so *every* weapon attack shares
   > one instance and any identity-keyed store collapses regardless of implementation.

4. **Lifetime is wrong for a pooled game — now CONFIRMED, not inferred.** Cleanup only happens
   in `NetEntity.OnDestroy`, but Megabonk pools aggressively: `PoolManager` holds **40+
   `UnityEngine.Pool.ObjectPool<GameObject>`** covering enemies, pickups, projectiles and
   attacks (`dump.cs:363290`), and `PickupManager.DespawnPickup` was decompiled calling
   **`ObjectPool<GameObject>.Release`** with no `Destroy` path at all (VA `0x1804DDB60`).
   Pooled objects are disabled and reused, never destroyed, so `OnDestroy` fires only at scene
   teardown and stale `NetId`/`OwnerId` survives every recycle. The static dictionary is never
   swept, and the `this.gameObject != null` guard in `OnDestroy` fails during scene teardown,
   so entries leak across stages.

   The correct reset point is `actionOnRelease` on the pool — the 7-argument
   `ObjectPool<T>` constructor exposes it, and `src/plugin/Helpers/PoolHelper.cs` already
   reconstructs that constructor reflectively, so it is demonstrably reachable.

Worst individual site, in `Pickup.cs` and three others:

```csharp
var netEnt = __instance.GetComponent<NetEntity>(); if (netEnt != null) UnityEngine.Object.Destroy(netEnt);
PickupManager.Instance.DespawnPickup(__instance);
```

`Destroy` is deferred to end of frame; the next line returns the pickup to the pool. If it is
re-issued before `OnDestroy` runs, it carries the previous owner's `OwnerId`. And because it
is pooled, the next access re-runs `AddComponent<NetEntity>()` — continuous
`AddComponent`/`Destroy` churn on the hottest object class in the game, in a change whose
stated purpose is eliminating GC stutter.

**If you want to revisit this later,** do it on its own branch with: pooling-aware lifetime
(clear on pool return, not on destroy), component-level keying preserved, accessors cached at
call sites, a bounded or weak-keyed store, and a benchmark against `DynamicData`. Not bundled
with netcode changes.

### Final swarm cap 400 → 700/800

`7acbc94`. **REJECT — and the decompilation strengthens the case while correcting the numbers.**

`EnemyManager.GetNumMaxEnemies` (VA `0x180419D60`) is the actual population cap:

```c
if (<final swarm active>) return (MyTime.<t> >= DAT_18262EDF4) ? 300 : 400;
return 0x226;   // 550 — the normal cap
```

So the vanilla cap is **550**, and 400 is the **final-swarm** value, which the game lowers
again to **300** past a time threshold. The game deliberately *reduces* density at its densest
moment. Raising it to 700–800 pushes against a staged reduction rather than correcting an
oversight — and multiplies `OnEnemyDied` volume, which the same fork made unreliable.

> **Two corrections this forces on our own documentation:**
>
> - `NETPLAY_CHANGES.md` calling 400 "the original cap" is **wrong**; 400 is final-swarm only.
> - **Our own 500/600 caps were chosen against that wrong baseline.** A 500 cap is *below*
>   vanilla's 550, so the 2–4 player setting may be reducing density rather than raising it.
>   The *"untested, you have been warned"* note is well earned. Re-tune against 550 / 400 / 300.

### `BaseSummoner` re-enable

`8e02879`. Upstream disabled it with `//TODO: re enable again when no more FPS drops`. The
fork re-enables it verbatim, fixing nothing that caused the disable, and the body has a
compounding shape:

```csharp
[HarmonyPrefix]
[HarmonyPatch(nameof(BaseSummoner.Tick))]
private static void Tick_Postfix(BaseSummoner __instance)
{
    __instance.giveCreditsTimer *= gameBalanceService.GetCreditsTimerMultiplier();
}
```

`GetCreditsTimerMultiplier()` returns 1.01–1.05 × 1.00–1.07, always > 1, and this runs on
**every** `Tick`. Two supporting signals: `Initialize()` still logs
`"Credits Timer Multiplier (Disabled)"`, and `GetCreditsTimerMultiplier()` → `PlayersCount` →
`GetAllPlayersAlive()` allocates a `Player[]` plus two LINQ iterators per summoner per tick.

> **RESOLVED (was UNVERIFIED) — `Tick` decompiled at VA `0x18046D990`.** `giveCreditsTimer` is
> an **accumulator**: it counts up by `deltaTime`, and on reaching a threshold of `1.0`
> (`DAT_18262EC7C`, read as `1.0f`) it grants credits and **resets to `0.0`**.
>
> That inverts the feared failure mode. Multiplying an up-counter by >1 makes it reach the
> threshold *sooner*, so credits arrive **faster** and **more** enemies spawn — the patch does
> not starve spawning. But the compounding is real *within* each accumulation window, so the
> effect is roughly **2–3× credit income**, not the 1–5% the multiplier's name suggests.
>
> **Still reject as written**, for two reasons that survive: the FPS regression that caused the
> original disable is unaddressed, and the per-tick allocation above is real. If the balance
> lever is wanted, the grant line is `credits += GetCreditsPerSecond() * GetMultiplier()` — so
> **postfix `GetMultiplier()` (VA `0x46CD20`)** instead: linear, non-compounding, and it
> composes with the game's own multiplier rather than fighting it.

### `Task.Run` removal in `WebsocketClientService`

`0c7e313`. Removes a 100 ms deferred teardown on host disconnect, based on a TODO that said
the task *"might be useless now"*. `HandleHostDisconnected` runs on the transport receive
thread, so `ResetNetworking()` would now execute synchronously inside the disconnect callback
— a plausible re-entrancy hang, for no measurable gain.

Moot anyway: this file is deleted by the Steamworks migration.

### `PlayersCount` `ICollection` test

`7acbc94`. Cosmetic. `GetAllPlayersAlive()` already returns a `Player[]`, so the test succeeds
and one enumerator allocation is saved — while the array and both LINQ iterators, which
dominate, are untouched. Fix the source instead: [P1-4](01-critical-fixes.md#p1-4).

---

## Suggested branch sequence

Land these as separate small PRs so each reverts independently, and so the parts worth
upstreaming to `Fcornaire/megabonk-together` are already isolated.

Two branches come **before** anything from this fork, because neither originates here:

```bash
# 0a. Steam upload suppression audit — ban risk, independent of everything else
git checkout -b fix/verify-steam-suppression main
#    P0-0 — nothing from Sea-Bass; see 01-critical-fixes.md#p0-0

# 0b. Dangling-transform diagnostic — XS, and every later playtest then collects data on it
git checkout -b chore/dangling-transform-logging main
#    P2-1 — the rate-limited version of the Sea-Bass log line
```

Then the fork-derived work:

```bash
# 1. Charging state machine — highest value, transport-independent
git checkout -b fix/charging-state-machine main
#    P0-1, P0-2, P2-2 together (same six methods)

# 2. Enemy target null guard — on top of our bd9518c version of Enemy.cs
git checkout -b fix/enemy-target-null-guard main
#    P0-4

# 3. Atomic ID allocation
git checkout -b fix/atomic-netid-allocation main
#    P0-3

# 4. Protocol version gate — unblocks any wire-format change
git checkout -b feat/protocol-version-gate main
#    P1-3

# 5. Host relay, sender excluded
git checkout -b fix/host-relay-xp-gold main
#    P1-1

# 6. Damage ownership attribution — requires #4 if it changes a message shape
git checkout -b fix/damage-ownership-attribution main
#    P1-5 — nothing from Sea-Bass; their NetEntity makes this worse, not better

# 7. Golden shrine sync — requires #4
git checkout -b feat/sync-golden-shrine main
#    P1-2 — message change only, no Start postfix
```

Items 1 and 5 are the best value-to-risk in the entire fork. Note that **P0-0, P2-1 and P1-5
take nothing from Sea-Bass at all** — they came out of decompilation, and P1-5 in particular is
a defect their `NetEntity` refactor would have deepened rather than fixed.

Once done, clean up:

```bash
git remote remove seabass
```

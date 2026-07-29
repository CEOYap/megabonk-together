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

Their `ChargeShrine.Start` postfix is a retry, not a fix, and its value depends on whether
`Start()` writes `isGolden`. Decompile before adding it — see
[`../reverse-engineering/01-investigation-targets.md`](../reverse-engineering/01-investigation-targets.md#chargeshrine).

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
   `PoolManager.cs` (reads `weaponBase`'s `OwnerId`, writes `__result`'s) and
   `WeaponUtility.cs` (whose "already assigned?" guard breaks if the `DamageContainer` shares
   a GameObject).
4. **Lifetime is wrong for a pooled game.** Cleanup only happens in `NetEntity.OnDestroy`,
   but Megabonk pools enemies, pickups, and projectiles — pooled objects are disabled and
   reused, never destroyed. Stale `NetId`/`OwnerId` survives recycling. The static dictionary
   is never swept, and the `this.gameObject != null` guard in `OnDestroy` fails during scene
   teardown, so entries leak across stages.

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

`7acbc94`. `NETPLAY_CHANGES.md` documents the 400 cap as deliberate ("keeping the original
cap"), and the existing 500/600 multiplayer caps are already flagged *"untested, you have
been warned"*. This raises worst-case concurrent enemies by 75–100% at the single densest
moment in the game — and multiplies `OnEnemyDied` volume, which the same fork made unreliable.

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

**UNVERIFIED:** whether `giveCreditsTimer` is an interval or a countdown determines the exact
failure mode, but both compound. Decompile before enabling under any circumstances —
[`../reverse-engineering/01-investigation-targets.md`](../reverse-engineering/01-investigation-targets.md#basesummoner).

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

# 6. Golden shrine sync — requires #4
git checkout -b feat/sync-golden-shrine main
#    P1-2
```

Items 1 and 5 are the best value-to-risk in the entire fork.

Once done, clean up:

```bash
git remote remove seabass
```

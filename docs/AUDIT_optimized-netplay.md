# Audit: `Sea-Bass-cmd/optimized-netplay` vs `Fcornaire/megabonk-together`

**Merge base:** `50b30a4` (Merge PR #92, `chore/proton-finish`)
**Audited head:** `8628e71` (seabass/main), 12 commits, 43 files, +565/−489
**Fork head (`CEOYap/megabonk-together`):** `041881b`, 2 commits ahead of the same base
**Auditor note:** no .NET toolchain in the audit environment — nothing here was compiled or run. All findings are from source reading.

---

## 1. Executive Summary

The branch has one real theme and one advertised theme, and they are not the same. The **real** theme is a mechanical, script-driven replacement of MonoMod `DynamicData` with a custom `NetEntity` MonoBehaviour + static dictionary across ~40 files, bundled with a handful of genuine logic fixes in the shrine/pylon/lamp charging state machine. The **advertised** theme — performance, GC, and bandwidth — is largely not delivered: the headline "bandwidth" change is a blanket downgrade of ~17 one-shot, non-idempotent event RPCs from `ReliableOrdered` to `Unreliable`, which trades bandwidth for permanent desync on any packet loss; the "GC" changes are a no-op LINQ tweak next to newly-introduced `AddComponent`/`Destroy` churn on pooled objects; and the claimed concurrency fix was never actually applied — only the `//TODO: concurrency?` comments were deleted.

There is real value in here, but it is a small fraction of the diff and it is tangled with the damaging parts inside the same commits.

**Recommendation: Selective cherry-pick, manual, hunk-level.** Do not merge the branch. Take the charging-state-machine fixes and two small null-safety fixes by hand. Reject the `Unreliable` downgrade, the `GameBalanceService` cap change, the `BaseSummoner` re-enable, and the whole `NetEntity` refactor as-is. Note also that your `main` is 2 commits ahead with Fcornaire's own `bd9518c` optimization work, which is better-targeted than this branch and conflicts with it in 5 files.

---

## 2. Detailed File-by-File Analysis

### `src/plugin/Services/SynchronizationService.cs` (+192/−215, the core of the diff)

**Primary purpose of change:** Extract duplicated shrine/pylon/lamp charging logic into shared `HandleChargingStart`/`HandleChargingStop`; add host-side relays for XP/gold/encounter-close; swap `DynamicData` for `NetEntity`; downgrade ~17 send sites to `Unreliable`.

**Technical evaluation:**

*The good — genuine, verifiable bug fixes in the charging state machine:*

The original `OnStartingToChargingShrine` wrote the charger list **before** checking whether someone was already charging:

```csharp
shrineChargingPlayers[shrineNetplayId] = [playerManagerService.GetLocalPlayer().ConnectionId];
if (chargers != null && chargers.Any()) { /* bail */ }   // too late — already clobbered
```

So a second player touching an occupied shrine bailed out *after* overwriting the first player's claim, orphaning it. The refactor checks first, then writes. Same class of fix applied to `OnReceivedStartingToChargingShrine` and `OnReceivedStartingToChargingLamp`.

The original stop paths did `shrineChargingPlayers[id].Remove(...)` — an indexer read on a `ConcurrentDictionary` with no key guard, i.e. a live `KeyNotFoundException` whenever a stop arrives with no start recorded (packet reorder, late join, host migration). The new `HandleChargingStop` guards `chargers == null || !chargers.Any()` first. This is a real crash fix.

The three near-identical ~30-line blocks (shrine/pylon/lamp) collapsing into one shared method also resolves the author's own `//TODO: this is ass, pylon and lamp should be refactored to use the same logic`.

*The bad — the `Unreliable` downgrade.* 17 methods moved from `ReliableOrdered` to `Unreliable`:

| Method | Consequence of a single dropped packet |
|---|---|
| `OnEnemyDied` | Enemy never dies on peers — permanent ghost, never despawns, accumulates |
| `OnChestOpened` | Chest state diverges; contents mismatch |
| `OnWeaponAdded` / `OnTomeAdded` | Player permanently missing an item on remote clients |
| `OnPickupSpawned` / `OnPickupOrbSpawned` / `OnPickupApplied` | XP orb never appears / XP never granted — level drift |
| `HandleChargingStart` / `HandleChargingStop` (shrine, pylon, lamp) | Dropped *stop* leaves `chargingDict[id]` non-empty forever → shrine permanently locked with "Another player is already charging this object" |
| `OnInteractableUsed` | Interactable state diverges |
| `OnProjectileDone` | Projectile never cleaned up on peers — leaked objects |
| `OnWantToStartFollowingPickup` / `SendPickupFollowingPlayer` | Orphaned pickups |
| `OnEnemyExploder` / `OnSpawnedEnemySpecialAttack` | Missed damage event (tolerable) |

Every one of these except the last two is a **one-shot, non-idempotent state transition**. There is no resend, no sequence number, no reconciliation snapshot to recover from a loss. `Unreliable` is the correct channel for *continuous* state (positions, HP, the existing `OnEnemyDamaged`) precisely because the next packet supersedes the lost one — there is no next packet for "this chest was opened."

Three aggravating factors:

1. **It is highest-risk exactly where the PR claims benefit.** Packet loss rises with send rate. At 600 enemies the `OnEnemyDied` volume is at its peak, so ghost-enemy accumulation is worst in the scenario this branch targets.
2. **`Unreliable` is unordered.** A `stop` overtaking its `start` leaves a shrine stuck in the charging state with no recovery path.
3. **LiteNetLib does not fragment unreliable packets.** Anything over MTU (~1400 B) on an unreliable channel is rejected/throws rather than fragmenting. `OnWeaponAdded` carries an `Il2CppSystem.Collections.Generic.List<StatModifier> upgradeOffer`; late-run inventory messages are plausible MTU-crossers. Reliable channels fragment; unreliable ones do not. This needs measurement before any of these downgrades ship.

This also directly reverts the intent of upstream `24f5004` — *"fix: update packet delivery methods to improve performance and prevent desync issues"* — which is already in the merge base. Fcornaire tuned these deliberately; this branch overwrites that tuning wholesale.

*The mixed — host relays (`269396f`).* Three handlers gained a rebroadcast:

```csharp
private void OnReceivedChangeGold(GoldChanged changed)
{
    if (IsServerMode() == true) udpClientService.SendToAllClients(changed, DeliveryMethod.ReliableOrdered);
    GameManager.Instance.player.inventory.ChangeGold(changed.Amount);
}
```

The *idea* is right — client→host messages previously never reached other clients, a genuine hole. The *implementation* echoes the message back to its own sender. `SendToAllClients` fans out to every peer in `gamePeers` with no sender filter, and `IUdpClientService` already exposes `SendToAllClientsExcept(int netPlayerId, uint sender, T data)` which was not used, even though `GoldChanged` carries an `OwnerId`.

For `AddXp` the echo is harmless — `playerXp.xp = xp.Xp` is an absolute assignment, idempotent. For **`GoldChanged` it is a duplication exploit**: `ChangeGold(changed.Amount)` is a *delta*. Client A applies gold locally → sends to host → host relays to all including A → A applies it a second time. Trivially farmable.

**Pros:** real charging-state fixes; relay closes a real propagation gap; removes ~200 lines of triplicated code.
**Cons:** `Unreliable` downgrade is a systemic desync source; gold relay is an exploit; `HandleChargingStart/Stop` are added to the *public* `ISynchronizationService` interface taking a `ConcurrentDictionary` parameter, leaking internal state through the API surface; both helpers use `chargingDict.FirstOrDefault(p => p.Key == netplayId)` — an O(n) LINQ scan with a closure allocation and a snapshot enumerator where `TryGetValue` is O(n⁰) and allocation-free, in a file whose commit message claims LINQ removal.

---

### `src/plugin/Scripts/NetEntity.cs` (new, 86 lines)

**Primary purpose of change:** Replace MonoMod `DynamicData` with a typed store. The docstring claims *"O(1) retrieval and zero garbage collection overhead."*

**Technical evaluation:** The typed `NetData` struct-of-fields is a genuine improvement over `DynamicData`'s string-keyed, reflection-backed dictionary — no more `Get<uint?>("netplayId")` typo risk, no string hashing, no boxing. That part is sound and worth keeping as a concept.

The implementation has four problems:

1. **The access path is not cheap.** `GetOrAddNetEntity()` is `GetComponent<NetEntity>()` → possible `AddComponent<NetEntity>()` → `gameObject.GetInstanceID()` → `Dictionary.TryGetValue`. On IL2CPP, `GetComponent<T>` for an *injected managed type* goes through Il2CppInterop generic resolution and allocates a wrapper — it is materially more expensive than a plain dictionary lookup, and it is now on the projectile-spawn and enemy-target paths.

2. **The mechanical refactor deleted every cached accessor.** The old code consistently hoisted `var dyn = DynamicData.For(obj);` and reused it. `global_refactor.py` rewrote each `.Get`/`.Set` independently, so read-then-write sites now call the whole chain twice:

   ```csharp
   var hasBeenSet = obj.GetOrAddNetEntity().hasBeenSetByServer;   // GetComponent #1
   if (!hasBeenSet.HasValue) {
       synchronizationService.OnSpawnedObject(obj);
       obj.GetOrAddNetEntity().hasBeenSetByServer = true;          // GetComponent #2
   }
   ```

   This pattern repeats 5× in `GenerateTileObjects.cs`, 4× in `SpawnInteractables.cs`, and ~15× in the projectile spawn switch in `SynchronizationService.cs` (`NetId` then `OwnerId` on consecutive lines, two full lookups each). Net: the refactor **increased** per-call cost at most sites.

3. **Component→GameObject key collapse — a semantic change, not a port.** `DynamicData.For(component)` keyed the *component*. The `Component` overload of `GetOrAddNetEntity` forwards to `comp.gameObject`, so **every component on a GameObject now shares one `NetData`**. Any GameObject carrying two components that each stored `OwnerId`/`NetId`/`TargetId` now silently aliases them. `PoolManager.cs` is the site to check first — it reads `weaponBase`'s `OwnerId` and writes `__result`'s (an `Attack`); if those sit on the same GameObject, the two are the same record. Same question for `WeaponUtility`'s "has this `DamageContainer` already been assigned an owner" guard, which becomes wrong if the container shares a GameObject with anything else that sets `OwnerId`.

4. **Lifetime is wrong for a pooled game.** Cleanup only happens in `NetEntity.OnDestroy`, but Megabonk pools enemies, pickups, and projectiles (`PoolManager` is patched precisely for this) — pooled objects are *disabled and reused*, never destroyed. Stale `NetId`/`OwnerId` therefore survives recycling. The static `NetDataManager.Data` dictionary is also never bounded or swept, and `OnDestroy` guards on `this.gameObject != null` — during scene teardown that guard fails and the entry leaks. Over a multi-stage run with hundreds of enemies this grows monotonically. (`DynamicData` used a `ConditionalWeakTable`, which at least let the GC reclaim entries; the instance-ID dictionary gives that up. Unity may also reuse instance IDs after destruction, which would leak one object's net state onto an unrelated one.)

The `ConditionalWeakTable` fallback for `Il2CppObjectBase` inherits `DynamicData`'s existing reference-identity caveat (Il2CppInterop wrapper churn can produce a second managed wrapper for the same native pointer). That is **not a regression** — the old code had the same property — but it is also not the "O(1), zero-GC" story the docstring tells.

**Pros:** typed fields beat string keys; removing the MonoMod dependency is a legitimate goal.
**Cons:** slower at most call sites than what it replaced; changes component-level keying to GameObject-level keying; unbounded static dictionary; broken under pooling.

---

### `src/plugin/Patches/Pickup.cs`, `PickupManager.cs` (+ 2 sites in `SynchronizationService.cs`)

**Primary purpose of change:** Port `DynamicData.For(x).Data.Clear()` to the `NetEntity` model.

**Technical evaluation:** The script emitted this at 4 sites:

```csharp
var netEnt = __instance.GetComponent<NetEntity>(); if (netEnt != null) UnityEngine.Object.Destroy(netEnt);
PickupManager.Instance.DespawnPickup(__instance);
```

Three defects in two lines:

- `Object.Destroy` is **deferred to end of frame**. `Data.Clear()` was synchronous. The very next statement returns the pickup to the pool — if it is re-issued before `OnDestroy` runs, it carries the previous owner's `OwnerId`. That is a **pickup mis-attribution / XP-to-wrong-player** window.
- `OnDestroy` removes the entry for the *whole GameObject*, so this also wipes any other component's net state (see the key-collapse issue above).
- Because the object is pooled, the next `GetOrAddNetEntity()` re-runs `AddComponent<NetEntity>()`. Every pickup cycle is now an `AddComponent` + `Destroy` pair on an IL2CPP-injected MonoBehaviour — among the most expensive operations in Unity, and a per-frame managed allocation during any horde that drops XP orbs. This is a direct GC regression in a PR whose stated purpose is eliminating GC stutter.

**Pros:** none.
**Cons:** deferred clear under pooling; cross-component wipe; sustained component churn on the hottest object class in the game.

---

### `src/plugin/Services/GameBalanceService.cs`

**Primary purpose of change:** "Avoid LINQ allocation in hot paths"; change the final-swarm enemy cap.

**Technical evaluation:** Two changes, both problematic.

*The LINQ "fix" is ~a no-op:*

```csharp
var players = playerManagerService.GetAllPlayersAlive();
return players is ICollection<Player> collection ? collection.Count : players.Count();
```

`GetAllPlayersAlive()` is `return [.. players.Where(p => p.Value.Hp > 0).Select(p => p.Value)];` — it materializes a `Player[]` on **every call**. The array is an `ICollection<Player>`, so the type test does succeed and one enumerator allocation is avoided; the array plus two LINQ iterators that actually dominate the cost are untouched. Removing the allocation would mean fixing `GetAllPlayersAlive`, which the branch does not do.

*The cap change is a straight performance regression:*

```csharp
// before: return 400 during Final Swarm — "Keep the original cap"
// after:
if (GameManager.Instance.IsFinalSwarm()) return baseCap + 200;   // → 700 or 800
```

`NETPLAY_CHANGES.md` documents the 400 cap as deliberate ("keeping the original cap"), and the pre-existing 500/600 caps are already flagged *"untested, you have been warned"*. This raises the worst-case concurrent-enemy count by 75–100% in the single densest moment of the game — the moment the PR title claims to optimize. It also multiplies the `OnEnemyDied` volume, which this same branch just made unreliable.

**Pros:** the typed `ICollection` check is harmless and marginally correct.
**Cons:** mislabeled as a GC fix; the cap change is undocumented, contradicts the design note, and pushes hard against the stated goal.

---

### `src/plugin/Patches/Summoner/BaseSummoner.cs`

**Primary purpose of change:** Uncomment and re-enable the credit-timer patch.

**Technical evaluation:** Upstream disabled this deliberately: `//TODO: re enable again when no more FPS drops`. The branch re-enables it **verbatim**, fixing nothing that caused the original disable, and the body has a compounding-multiplier shape:

```csharp
[HarmonyPrefix]
[HarmonyPatch(nameof(BaseSummoner.Tick))]
private static void Tick_Postfix(BaseSummoner __instance)
{
    ...
    __instance.giveCreditsTimer *= gameBalanceService.GetCreditsTimerMultiplier();
}
```

`GetCreditsTimerMultiplier()` returns 1.01–1.05 × 1.00–1.07, i.e. **always > 1**, and this runs on *every* `Tick`, not once per credit grant. Unless `giveCreditsTimer` is reassigned from a constant every tick before the prefix observes it, the value compounds geometrically — 1.05^N — and the summoner stops issuing credits within seconds. (I could not decompile `Assembly-CSharp.dll` here to confirm `giveCreditsTimer`'s exact semantics; this needs a decompiler check before the patch is enabled either way.)

Two supporting signals: `Initialize()` still logs `"Credits Timer Multiplier (Disabled)"`, so the log now lies about a live patch; and `GetCreditsTimerMultiplier()` → `PlayersCount` → `GetAllPlayersAlive()` allocates a `Player[]` plus two LINQ iterators **per summoner per tick**, which is a textbook per-frame allocation in a branch that advertises GC reduction.

**Pros:** the balance intent (more players → more credits) is reasonable.
**Cons:** re-enables a patch that was disabled for measured FPS reasons; compounding-multiplier shape is unaddressed; adds per-tick LINQ allocation; stale "Disabled" log.

---

### `src/plugin/Patches/Enemies/Enemy.cs`

**Primary purpose of change:** Collapse the host-side target-assignment branch.

**Technical evaluation:** The rewrite **dropped a line**. Original:

```csharp
var randomPlayer = playerManagerService.GetNetPlayerByNetplayId(id);
__instance.target = randomPlayer.Rigidbody;                       // ← physics target
DynamicData.For(__instance).Set("targetId", randomPlayer.ConnectionId);
```

New:

```csharp
var targetPlayer = playerManagerService.GetNetPlayerByNetplayId(id);
__instance.GetOrAddNetEntity().TargetId = targetPlayer != null ? targetPlayer.ConnectionId : playerManagerService.GetLocalPlayer().ConnectionId;
// __instance.target is never assigned
```

The network `TargetId` now says "remote player" while the enemy's actual physics target is whatever `InitEnemy` left it at — the host. `TargetSwitcher.Update` does reassign `enemy.target` (line 129), but only after `delay`, a random 2–6 s. So every freshly-spawned enemy beelines toward the host for 2–6 seconds regardless of its advertised target. Under continuous spawning at 600 enemies this is a permanent aggro bias toward the host and a visible host↔client divergence in enemy movement.

The added `targetPlayer != null` guard is a legitimate null-safety improvement — the original would have NRE'd on `randomPlayer.Rigidbody`. Keep the guard, restore the assignment.

**Pros:** null guard on `GetNetPlayerByNetplayId`.
**Cons:** silently dropped `__instance.target` assignment — a behavioral regression, not a port. Note this file also conflicts with your `bd9518c`.

---

### `src/plugin/Services/{Enemy,Pickup,SpawnedObject}ManagerService.cs` — commit `0c7e313`

**Primary purpose of change:** Commit message: *"Fix 4 remaining TODOs (Concurrency IDs, ...)"*.

**Technical evaluation:** **The fix was never applied.** `fix_concurrency.py` was written to convert `currentEnemyId++` into `Interlocked.Increment`, but its second regex (matching the `++` / `TryAdd` / `return` block) did not match. The only surviving effect is the first regex, which deleted the `//TODO: concurrency?` comments. Verified on `seabass/main`:

```
src/plugin/Services/EnemyManagerService.cs:41:  private uint currentEnemyId = 0;      // still uint, no Interlocked
src/plugin/Services/EnemyManagerService.cs:162: currentEnemyId++;                     // still non-atomic
```

`git grep Interlocked` across `src/plugin` on that branch returns nothing. The race — non-atomic increment feeding `TryAdd` on a `ConcurrentDictionary` reachable from the network receive thread and the main thread — is fully intact, and the marker warning future maintainers about it is gone. This is worse than leaving it alone.

**Pros:** none.
**Cons:** commit message asserts a fix that does not exist; removes the warning comment for a live race condition.

---

### `src/plugin/Services/WebsocketClientService.cs`

**Primary purpose of change:** Remove the deferred host-disconnect teardown.

**Technical evaluation:**

```csharp
- _ = Task.Run(async () => { await Task.Delay(100); ResetNetworking(); GoToMainMenu(); });
+ ResetNetworking();
+ GoToMainMenu();
```

The removed TODO said the task *"might be useless now (was preventing a race condition before)"* — "might" is not a basis for removal without a repro. `HandleHostDisconnected` runs on the transport's receive thread; `ResetNetworking()` now executes synchronously inside the disconnect callback, re-entering the networking stack while it is mid-teardown. That is a plausible deadlock or double-teardown on host disconnect. Removing a 100 ms defer buys nothing measurable and risks a hang on a path that is hard to test and very visible when it breaks.

**Pros:** removes a `Task.Run` allocation on a once-per-session path (negligible).
**Cons:** re-entrancy risk on the disconnect path for no measurable gain.

---

### `src/plugin/Patches/ChargeShrine.cs` + `src/common/Messages/GameNetworkMessages/SpawnedObject.cs`

**Primary purpose of change:** Sync the legendary (golden) shrine flag, which previously was host-only state.

**Technical evaluation:** The gap is real — `chargeShrine.isGolden` was never transmitted, so clients rendered/treated legendary shrines as normal. The fix captures it in `SendSpawnedObject` and reapplies it in a new `Start` postfix on the client.

Two caveats:

- **Ordering.** `Start_Postfix` reads `NetData.IsGoldenShrine`, which is only populated by `OnReceivedSpawnedObject` (line 469). If Unity runs `Start()` before the spawn message is processed, the value is null and the postfix no-ops. Line 467 does set `chargeShrine.isGolden` directly on the receive path, so the common case is covered by that, and the postfix is a belt-and-braces retry — but the postfix alone is not a fix, and it is the part being sold as one.
- **Wire-format break.** `Specific` gains `public bool? IsGoldenShrine { get; set; }`. `Specific` is `[MemoryPackable]`, which serializes members positionally. **Any client on a build without this field will mis-deserialize every `SpawnedObject` message from a host that has it**, and vice versa. This is a hard cross-version incompatibility for a publicly distributed mod — it needs a protocol/plugin version bump and a join-time version gate, neither of which is in this branch.

**Pros:** closes a genuine sync gap for legendary shrines.
**Cons:** silent wire-format break with no version gate; the postfix half of the fix is race-dependent.

---

### `src/plugin/Patches/Unity/UnityComponent.cs`, `src/plugin/Plugin.cs`

**Primary purpose of change:** Replace a `¯\_(ツ)_/¯` comment with a real `LogWarning` on the dangling-transform fallback; register `NetEntity` with `ClassInjector`.

**Technical evaluation:** The logging change is a small, unambiguous win — the fallback is a known unexplained hack and now leaves a trace. **Caution:** this is the *dangling NetPlayer transform* path, which can fire per-frame per-affected-object; an unthrottled `LogWarning` there is itself a per-frame string allocation and a disk-I/O stall through BepInEx. Rate-limit it if you take it.

The `ClassInjector.RegisterTypeInIl2Cpp<NetEntity>()` line is mandatory for `NetEntity` and meaningless without it. Both files conflict with your `bd9518c`.

**Pros:** replaces a shrug with a diagnostic.
**Cons:** unthrottled logging on a potentially per-frame path.

---

### `src/plugin/Extensions/DataManager.cs` (deleted)

Removes a `GetEnemyDataByName` extension the author's own TODO says duplicates a base-game method. Fine in isolation; verify no remaining call sites before taking it.

---

## 3. Performance & Netcode Impact Breakdown

### Bandwidth & Latency

Bandwidth **does** drop, but not from anything structural. There is **no delta compression added, no packet batching added, no tick-rate change, and no serialization-format change** anywhere in this diff — the existing delta system in `EnemyManagerService` is untouched by this branch (and is being improved independently by Fcornaire in `bd9518c`). The entire bandwidth saving comes from removing LiteNetLib's reliability layer on 17 event types: no ACKs, no retransmit queue, no ordering buffers.

That is a real reduction in bytes and in head-of-line blocking. It is also the reliability layer doing its job. On a clean LAN you will see improvement. On real internet links with 1–3% loss you get one permanently-desynced entity or item per ~30–100 affected events, with no reconciliation path. The host relays add a small amount of traffic back (host→all for XP/gold/encounter events that previously went nowhere), which is correct and cheap.

Net: measurable bandwidth reduction, purchased with correctness rather than engineering.

### Frame Rate (FPS) & GC Stutters

Likely **net negative** at 400–600 enemies:

*Regressions introduced:*
- `AddComponent<NetEntity>` / `Object.Destroy` churn on every pooled pickup cycle (4 sites), during exactly the XP-orb storms that cause the stutters being targeted.
- Cached accessors deleted throughout: read-then-write sites now do two full `GetComponent<NetEntity>()` chains instead of one hoisted local — ~24 sites, including the 15-branch projectile spawn switch.
- `BaseSummoner.Tick` re-enabled: `Player[]` + two LINQ iterators allocated per summoner per tick, plus the FPS cost that caused the original disable.
- Final-swarm cap raised 400 → 700/800: 75–100% more enemy `Update`s, physics bodies, renderers, `TargetSwitcher.Update` calls, and delta-sync entries at peak.

*Improvements delivered:*
- Typed field access replaces `DynamicData`'s string-keyed reflection lookups. Real, but partly cancelled by the more expensive `GetComponent` path and entirely cancelled at the double-call sites.
- One enumerator allocation avoided in `PlayersCount`.

*Not addressed at all:* `TargetSwitcher` is a per-enemy `MonoBehaviour.Update` — at 600 enemies that is 600 managed Update calls per frame across the IL2CPP boundary, plus `GetAllPlayersAlive().ToList()` (array + List + 2 iterators) on every switch and a full O(players) distance loop in `PickACloseTarget`. This is the dominant managed-side cost in a horde and the branch does not touch it. Neither struct caching, object pooling, nor array re-use appears anywhere in the diff.

For contrast, your `main` already contains better-targeted work in `bd9518c`: `string[]` → `HashSet<string>` for `AllowedDamageSource` (O(n)→O(1) on a per-damage-event lookup), and replacement of `Select().ToList().ToDictionary()` with a pre-sized `Dictionary` + `foreach` in `GetAllEnemiesDeltaAndUpdate` — the actual per-tick, per-enemy hot path.

### Host/Client Sync Stability

**Degraded.** In rough order of severity:

1. Non-idempotent events on an unreliable channel, with no resend and no reconciliation — the dominant new desync source.
2. Unordered delivery of paired start/stop charging events — permanent stuck states.
3. Gold relay echo — divergence *and* an exploit.
4. Stale `NetData` on pooled objects (deferred `Destroy`, `OnDestroy`-only cleanup) — ownership misattribution.
5. Enemy physics target no longer assigned at spawn — 2–6 s of host-biased aggro on every spawn.
6. Non-atomic ID allocation still live, with its warning comment removed.
7. Wire-format change with no version gate — cross-build sessions corrupt `SpawnedObject`.

Genuinely improved: the charging state machine's claim/release ordering and its `KeyNotFoundException` paths; legendary shrine flag propagation; XP/gold/encounter-close now reaching non-host peers at all.

---

## 4. Risks & Desync Flags

| # | Location | Risk | Severity |
|---|---|---|---|
| R1 | `SynchronizationService.OnEnemyDied` (~L1600) | Dropped packet → permanent ghost enemy on clients; accumulates, worst at 600 enemies | **Critical** |
| R2 | `OnReceivedChangeGold` (L4366) | Host relays `GoldChanged` back to its sender; `ChangeGold` is a delta → sender double-applies. **Gold duplication exploit.** Use `SendToAllClientsExcept` with `changed.OwnerId` | **Critical** |
| R3 | `HandleChargingStart` / `HandleChargingStop` (L287, L310) | `Unreliable` + unordered on paired events → dropped/reordered `stop` leaves shrine/pylon/lamp permanently locked. Directly affects graveyard boss lamps | **Critical** |
| R4 | `OnWeaponAdded` (L2098), `OnTomeAdded` (L2167), `OnChestOpened` (L2043) | Dropped packet → permanent inventory divergence; `OnWeaponAdded` may also exceed MTU, which LiteNetLib cannot fragment on an unreliable channel | **Critical** |
| R5 | `OnPickupSpawned` / `OnPickupOrbSpawned` / `OnPickupApplied` (L1736/1765/1799) | Dropped packet → XP never granted; compounds with the shared-XP model into level drift | **High** |
| R6 | `Pickup.cs:71`, `PickupManager.cs:58`, `SynchronizationService.cs:1781,1887` | `Object.Destroy(netEnt)` is deferred; pool re-issues the object with stale `OwnerId`. Also wipes all sibling components' net state | **High** |
| R7 | `GameBalanceService.GetMaxEnemiesSpawnable` | Final swarm 400 → 700/800; contradicts `NETPLAY_CHANGES.md`, worsens every other risk here | **High** |
| R8 | `BaseSummonerPatches.Tick_Postfix` | `giveCreditsTimer *= multiplier` (>1) on every Tick — compounds; re-enables a patch disabled for FPS. Verify `giveCreditsTimer` semantics with a decompiler first | **High** |
| R9 | `Enemy.cs init_PostFix` | `__instance.target = randomPlayer.Rigidbody` deleted; 2–6 s host-aggro bias on every spawn | **Medium** |
| R10 | `EnemyManagerService.cs:162`, `PickupManagerService.cs:47`, `SpawnedObjectManagerService.cs` | `currentXId++` still non-atomic; claimed fix never applied; TODO markers deleted | **Medium** |
| R11 | `Specific.IsGoldenShrine` (`SpawnedObject.cs`) | MemoryPack positional wire break; no version gate → cross-build sessions corrupt `SpawnedObject` | **Medium** |
| R12 | `NetEntity` `Component` overload | Component-level keying collapsed to GameObject-level; all components on a GameObject share one `NetData`. Audit `PoolManager.cs` and `WeaponUtility.cs` first | **Medium** |
| R13 | `NetDataManager.Data` | Unbounded static dictionary; `OnDestroy` guard fails on scene teardown; instance IDs may be reused | **Medium** |
| R14 | `WebsocketClientService.HandleHostDisconnected` | Teardown now runs synchronously on the transport receive thread — re-entrancy / hang risk on host disconnect | **Medium** |
| R15 | `UnityComponent.cs` dangling-transform warning | Unthrottled `LogWarning` on a potentially per-frame path — string alloc + BepInEx disk I/O | **Low** |

---

## 5. Recommended Merge Plan

### Do not merge the branch, and do not cherry-pick these commits as-is

Every commit that contains something worth having also contains something that must not ship. `96e1230` holds both the charging-state fixes and the `Unreliable` downgrade. `269396f` holds the shrine fixes, the relay hole-closing, the relay echo exploit, and the wire break. There is no clean subset at commit granularity.

Two structural blockers on top of that:

- **`45ce3f5` adds an 11.5 MB `dump.cs`** which `8628e71` removes. Cherry-picking `45ce3f5` alone permanently adds 11.5 MB to your history. If you ever take it, use `git cherry-pick -n 45ce3f5 && git rm --cached dump.cs`, or take both commits together and squash.
- **5 files conflict with your `main`** (`bd9518c`, `041881b`): `Patches/Enemies/Enemy.cs`, `Patches/Unity/UnityComponent.cs`, `Plugin.cs`, `Services/EnemyManagerService.cs`, `Services/SynchronizationService.cs`. Fcornaire's versions of the `EnemyManagerService` and `Enemy.cs` changes are the better optimization work; do not let a cherry-pick overwrite them.

### Classification

**Essential improvements — port by hand (~150 lines, no `NetEntity` dependency):**

| Change | Source | Notes |
|---|---|---|
| Charging claim/release ordering (check-then-write) | `96e1230`, `269396f` | Apply to shrine, pylon, lamp, and their `OnReceived*` counterparts. Keep `ReliableOrdered` |
| `KeyNotFoundException` guards in all stop paths | `96e1230` | `chargers == null \|\| !chargers.Any()` before any indexer |
| `HandleChargingStart`/`Stop` extraction | `96e1230` | Take the dedup, but keep them `private`, don't pass the dictionary through the public interface, and use `TryGetValue` instead of `FirstOrDefault` |
| `targetPlayer != null` guard | `2b4191a` | Keep the guard, **restore** `__instance.target = targetPlayer.Rigidbody` |
| Dangling-transform `LogWarning` | `8e02879` | Add a rate limiter |

**Optional enhancements — take with fixes:**

| Change | Fix required before taking |
|---|---|
| Host relays for XP / gold / encounter-close | Use `SendToAllClientsExcept(..., changed.OwnerId, ...)`. Non-negotiable for `GoldChanged` |
| Legendary shrine sync | Bump the plugin/protocol version and add a join-time version check; `Specific` is positionally serialized |
| `Interlocked` on the three ID counters | Actually apply it — `private int currentEnemyId; var newId = (uint)Interlocked.Increment(ref currentEnemyId);` |
| Delete `Extensions/DataManager.cs` | Confirm zero remaining call sites |

**Risky / highly experimental — reject:**

- The blanket `ReliableOrdered` → `Unreliable` downgrade (all 17 sites). Undoes upstream `24f5004` and is the branch's single largest liability.
- The `NetEntity` refactor in its current form (`b00accb`, `2b4191a`, `74c21d1`, `45ce3f5`). The typed-store idea is worth revisiting later, on its own branch, with pooling-aware lifetime, component-level keying preserved, cached accessors, and a benchmark against `DynamicData`. Not now, and not bundled with netcode changes.
- `BaseSummoner` re-enable (`8e02879`).
- `GetMaxEnemiesSpawnable` final-swarm change (`7acbc94`).
- `WebsocketClientService` `Task.Run` removal (`0c7e313`).
- The `PlayersCount` LINQ "fix" — cosmetic; fix `GetAllPlayersAlive()` instead if you care about that allocation.

### Suggested sequence

```bash
# 1. Reference copy of the branch, kept out of your history
git remote add seabass https://github.com/Sea-Bass-cmd/optimized-netplay.git
git fetch seabass
git log --oneline 50b30a4..seabass/main

# 2. Charging fixes, ported by hand onto current main — NOT a cherry-pick.
#    Read the before/after side by side and transplant only the ordering
#    and null-guard changes; leave every DeliveryMethod as ReliableOrdered.
git checkout -b fix/charging-state-machine main
git difftool 50b30a4 seabass/main -- src/plugin/Services/SynchronizationService.cs
#    Test: two clients contest one shrine; one disconnects mid-charge;
#    stop arrives with no prior start. None of these should throw or lock.

# 3. Host relays, with the sender excluded.
git checkout -b fix/host-relay-xp-gold main
#    Test explicitly for the duplication: client picks up gold, assert the
#    client's total increases exactly once.

# 4. Legendary shrine sync + version gate, together in one commit.
git checkout -b feat/sync-golden-shrine main
#    Bump the plugin version and reject joins on a version mismatch;
#    the MemoryPack layout change is not backward compatible.

# 5. Real Interlocked fix on the three ID counters.
git checkout -b fix/atomic-netid-allocation main

# 6. Enemy.cs null guard — apply on top of bd9518c's version of the file,
#    keeping the __instance.target assignment.
```

Land these as separate small PRs so each can be reverted independently, and so the parts worth upstreaming to `Fcornaire/megabonk-together` are already isolated. Items 2 and 3 are the highest value-to-risk ratio in the entire branch.

### Before shipping anything from here

1. Build the branch — no .NET toolchain was available for this audit, so **nothing in it has been compile-verified**. Watch specifically for a `Player` ambiguity in `GameBalanceService.cs`, where `using MegabonkTogether.Common.Models;` was added alongside the existing game-namespace imports.
2. Decompile `Assembly-CSharp.dll` and confirm `BaseSummoner.giveCreditsTimer` semantics before enabling that patch under any circumstances.
3. Measure the serialized size of `WeaponAdded` and `ChestOpened` against MTU before considering an unreliable channel for anything.
4. Test with simulated 2–5% packet loss (`clumsy` on Windows, `tc netem` on Linux). Most of what is flagged here is invisible on a LAN.

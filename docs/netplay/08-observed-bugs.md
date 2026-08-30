# Observed bugs — backlog

Bugs seen in play that are **not yet fixed** and are not tracked elsewhere. Distinct from
[`01-critical-fixes.md`](01-critical-fixes.md) (defects found by source analysis, most now fixed)
and [`07-shared-experience-audit.md`](07-shared-experience-audit.md) (the encounter barrier's own
audit).

Status tags per [`../README.md`](../README.md): **CONFIRMED** — verified by reading the code at the
cited line. **LIKELY** — strong inference from structure, failing path not observed. **UNVERIFIED**
— depends on game internals not yet decompiled.

OB-1..OB-4 come from the 2026-08-06 session (two players, direct P2P). OB-5..OB-9 come from the
**2026-08-07** sessions — two players over the internet at ~61 ms rtt, running the round-identity
build with `WriteUnityLog = true` on both peers. OB-11 and OB-12 come from the **2026-08-13**
Steam-transport sessions and are both transport-independent — they would behave identically on the
rendezvous path. OB-13 was reported on **2026-08-16** and has no capture attached yet.

---

<a name="ob-1"></a>
## OB-1 — Aegis shield shows a different orbit count on every peer — CONFIRMED

**Reported:** the number of shield projectiles around a player differs between host and client.

**Cause, exactly.** [`Scripts/NetPlayer/NetPlayer.cs:401`](../../src/plugin/Scripts/NetPlayer/NetPlayer.cs)
builds a remote player's Aegis with a **hardcoded** count:

```csharp
case EWeapon.Aegis:
    var aegisAttack = attack.GetComponent<AegisAttack>();
    aegisAttack.Set(weapon);
    aegisAttack.currentAmount = 2;      // ← never updated afterwards
```

and [`Patches/ConstantAttacks/AegisAttack.cs`](../../src/plugin/Patches/ConstantAttacks/AegisAttack.cs)
suppresses `AegisAttack.FixedUpdate` for any attack whose `ownerId` is not the local player. So the
remote copy is frozen at 2 for the rest of the run, while the owner's own count moves with weapon
level and with the game's `A_Used` / `A_Regen` events.

Everyone therefore sees their own shield correctly and everyone else's as a permanent 2.

**This is a class, not one item.** The same `currentAmount = 2` literal is applied to Chunkers a few
lines below, and every `EWeapon` in that switch has its `FixedUpdate` suppressed the same way. Any
constant attack whose visual depends on mutable state has this bug; Aegis is just the one where the
count is countable by eye.

**Game-side facts** (buildid 21750826, from `dump.cs` — not decompiled bodies):

| Symbol | Note |
|---|---|
| `AegisAttack.currentAmount` (`0x5C`) | the state the renderer follows |
| `AegisAttack.A_Used` / `A_Regen` — `static Action<int>` | the events that change it |
| `AegisRenderer.SetAmount(int)` VA `0x18034AAD0` | what actually shows N orbs |

**Fix shape — decided 2026-08-16 by decompile. The cheap option is ruled out.**

The entry previously said "prefer deriving it locally if the formula is pure". **It is not pure**, so
that option is wrong and would have shipped a subtler version of the same bug.

```
AegisAttack.GetMaxShields()                       VA 0x1804A4590
  = WeaponUtility.GetAttackQuantity(this.weaponBase)

WeaponUtility.GetAttackQuantity(weaponBase)       VA 0x1804346F0
  if (weaponBase.weaponData.eWeapon == 29 /* Shotgun */) return 2;
  return max(weaponStats[EStat.Projectiles] + PlayerStats.GetStat(EStat.Projectiles), floor);
```

`PlayerStats.GetStat` is **static and takes no player** — the same local-player-singleton pattern as
[OB-11](#ob-11)'s spawner and [OB-2](#ob-2)'s ghosts. So the count is
`per-weapon stat + the reading peer's own global Projectiles stat`. Deriving peer A's shield count on
peer B would add **B's** Projectiles stat to A's weapon, and the two ends would still disagree.

Worse, it would look correct in testing: while both players have the base Projectiles stat the
derived number matches, and it only diverges once somebody takes the upgrade. A playtest would
plausibly pass it.

**So OB-1 needs replication.** A new append-only union tag carrying `ownerId` + `eWeapon` +
`currentAmount`, at a low rate — it changes rarely. Replicating the owner's Projectiles stat instead
would also work and is not obviously cheaper, since the count still has to be pushed on every
`A_Used` / `A_Regen` to stay live.

> **Two Ghidra field names in this chain are off by one slot**, in the same direction, and both were
> caught against `dump.cs` offsets rather than trusted. `GetMaxShields` prints `.shieldUseSfx`
> (`0x28`) where it reads `ConstantAttack.weaponBase` (`0x20`), and `GetAttackQuantity` prints
> `.icon` (`0x58`, a `Texture` pointer) where it reads `WeaponData.eWeapon` (`0x50`, an int) — a
> pointer compared against `29` is the tell. This is the trap `CLAUDE.md` records, twice in one
> chain.

**Not known:** whether the wrong count is purely cosmetic or whether the remote `AegisAttack` also
blocks or deals damage on the observing peer. `FixedUpdate` is suppressed, which suggests cosmetic,
but the collision path was not traced.

---

<a name="ob-2"></a>
## OB-2 — Ghost item's summons appear for players who do not have the item — **FIXED and CONFIRMED in play (2026-08-17)**

**Reported:** when either peer holds the Ghost item — which summons ghosts on interacting with pots,
shrines and similar — the other player sees the ghosts too, without owning the item. Whether the
non-owner also *receives the effect* (damage dealt by those ghosts, or credit for it) is unknown to
the reporter.

### Answered by decompile 2026-08-16 — and the recorded decision criterion was the wrong question

This entry said the deciding lookup was whether `SpawnGhost` routes through `EnemyManager.SpawnEnemy`
(correct-by-construction) or instantiates directly (a real divergence). **Neither branch is what
happens**, and the framing — host authority over a shared set of ghosts — does not apply at all.

```
ItemGhost.OnInteracted(interactable, success)   VA 0x180457DA0
  if (!success) return;
  for (i = 0; i < this.numGhosts; i++)
      EffectManager.SpawnGhostProjectile(
          MyPlayer.Instance.baseDamage * 2,            // 0x12C
          PlayerStats.GetStat(10) * k,
          this.damageSource);

ItemGhost.SpawnGhost()                          VA 0x180457EE0   — same call, once
ItemGhost.Init()                                VA 0x180457C60
  DetectInteractables.<static Action<BaseInteractable,bool>> += this.OnInteracted
```

Three facts, and together they are the whole bug:

1. **Ghosts are `EffectManager` projectiles, not enemies.** `SpawnEnemy` is never involved, so no
   host-authority gate applies to them and none is missing. Nothing here is host-authoritative.
2. **`SpawnGhostProjectile` takes no position**, and both of its numbers come from
   `MyPlayer.Instance` and the static `PlayerStats.GetStat` — the *local* player. So a ghost always
   appears at, and is scaled by, whoever's machine spawned it. Same local-player-singleton pattern as
   [OB-1](#ob-1) and [OB-11](#ob-11).
3. **`Init` subscribes each instance to a static event on `DetectInteractables`** — one global
   `Action<BaseInteractable, bool>`, not something per-owner.

**The mechanism.** `NetPlayer.Inventory.itemInventory.items` holds **real `ItemGhost` instances for
remote players** — `PlayerManagerService.IsRemoteItem` exists precisely because they do. Every one of
those instances has run `Init` and is therefore subscribed to the same global interaction event. So
when the *local* player interacts with anything, the event fires every subscribed instance, including
the ones belonging to remote players, and each spawns its ghosts **at the local player using the
local player's `baseDamage`**.

That is exactly the report: a player who does not own the item sees ghosts, because the copy of the
*other* player's item living on their machine fired on their interaction. It is not a shared world
being replicated; it is one peer's item running on the other peer's machine.

**The fix is already written and never wired up.** `IsRemoteItem(ItemGhost)` is declared at
`IPlayerManagerService` and implemented in `PlayerManagerService` — and **has no callers**. Its
sibling `IsANetPlayerAbility` is used exactly this way in
[`Patches/Passives/PassiveAbilityBullseye.cs`](../../src/plugin/Patches/Passives/PassiveAbilityBullseye.cs).
`OnInteracted_Prefix` should take `ItemGhost __instance` and return `false` when the instance is
remote, mirroring that patch.

### The fix has landed

`OnInteracted_Prefix` now takes `ItemGhost __instance` and returns `false` when
`IsRemoteItem(__instance)`, mirroring `PassiveAbilityBullseyePatches`. The same commit closes the
standing-rule violation noted below — the patch had neither a session guard nor an ownership check.

Two deliberate details:

- **The `!success` branch stays first and stays ungated.** It is a crash guard, not netplay logic,
  and must keep applying in singleplayer. The session check sits after it, so the ownership test
  cannot alter singleplayer — where there are no remote items anyway.
- **`IsRemoteItem` is null-hardened**, because it moved from unused to on a gameplay path.
  `NetPlayer` receives its `Inventory` in a later lifecycle step than its spawn, and an exception
  there would be thrown inside a Harmony prefix. Its `netPlayer == null` check uses Unity's operator
  on purpose: a destroyed avatar is non-null managed-side, so `?.` would not catch it.

**Verified without a playtest:** it builds against the real install's interop, and `OnInteracted`'s
parameter is named `success` in the proxy — which Harmony's by-name binding needs, and which fails
at patch time and only in the startup log when it is wrong.

**Unverified — not run in-game:**

| Question | How it would show |
|---|---|
| Do ghosts now appear only for the player who owns the item? | the direct check; interact on each peer in turn |
| Did the non-owner's ghosts also *deal* damage? | if so, this changes damage numbers and not only visuals |
| Does anything still fire per-interaction that should not? | `damageSource` is per-instance, so credit attribution is worth a look |

**A follow-up this exposes rather than causes.** Ghosts are not replicated at all — there is no
message for them. Before the fix, each peer spawned a set locally from whatever instances it held;
after it, each peer spawns only its owner's set, which is strictly more correct but means **a
client's ghosts are gameplay actors that exist only on the client**. Whether their damage registers
on the host is a separate open question, on the same footing as any other client-side damage source.
It is not made worse by this fix, and it was not visible before it.

**Separately, and regardless of the above — now fixed in the same commit:**
[`Patches/Items/ItemGhost.cs`](../../src/plugin/Patches/Items/ItemGhost.cs) had **no
`HasNetplaySessionStarted()` guard and no ownership check**, contrary to the standing rule in
`CLAUDE.md`. It only suppressed the original when `success` was false, so it was probably harmless
today — but it changed singleplayer, which that rule exists to prevent. It was also, as it turned
out, the exact patch that needed the ownership check for the bug above: the missing guard and the
reported symptom were the same gap seen from two ends.

---

<a name="ob-3"></a>
## OB-3 — "Waiting for other player(s) choices…" reappears every ~20 s — CONFIRMED

**Reported:** the waiting message appeared repeatedly for no obvious reason.

**Evidence.** Host log, 2026-08-06: **five** `Shared-experience failsafe fired after 20.0s` lines,
**zero** on the client. Three of them are consecutive, and using the 10 s `[bw]` reports as a clock
they are **~20 s apart** — i.e. exactly the failsafe interval, and each reports a *fresh* 20.0 s
wait rather than a growing one.

So the barrier is not hitting one hole and recovering. It opens, is never satisfied, is force-released
after 20 s, and re-arms — indefinitely, until the stage transition that ends the sequence in the log.
Each re-arm shows the waiting text again, which is what the player sees.

**Mechanism: [SE-6](07-shared-experience-audit.md#se-6), already CONFIRMED and unfixed.**
`IsClosable()` is `closedEncounterPerPlayer.Count >= GetAllPlayers().Count()`, counting every player
including ones that cannot report. `PopReward_Prefix` returns early *without reporting* when
`!CanInput()`, so a peer that is dead, loading or mid-teleport never joins the round and the host
waits forever.

The host/client asymmetry corroborates it: **the host was the one waiting** (5 fires vs 0), meaning
the client never reported. The failsafe converts SE-6's permanent hang into a 20-second repeating
cycle — bounded, which is what it was for, but not resolved.

**Fix: PARTIAL, UNVERIFIED in play.** SE-5's round identity has landed (union tags 69/70 — see
[SE-5](07-shared-experience-audit.md#se-5)), which stops a stale report releasing the wrong round.

**It does not fix the cause of OB-3.** The re-arm loop here is [SE-6](07-shared-experience-audit.md#se-6):
`IsClosable()` counts every player, including a peer that returned early from `PopReward_Prefix`
without reporting because `!CanInput()`. Round identity means that peer's report, when it finally
comes, is now *rejected* as stale rather than misapplied — which is more correct but does not make
the round complete. The 20-second failsafe cycle should therefore still be expected until SE-6 is
addressed, and the next capture should be read with that in mind rather than as a regression.

---

<a name="ob-4"></a>
## OB-4 — The failsafe closes an encounter window that is mid-use — CONFIRMED, **FIXED and VERIFIED in play**

**Fixed by round identity.** The failsafe's release is now stamped like every other one, and
`EncounterService.TryApplyRelease` drops a stamp for a round the peer has already applied. The
second release for a finished round is therefore ignored instead of closing whatever window is open
— see [SE-5](07-shared-experience-audit.md#se-5). Not run in-game. The chain below is the original
diagnosis and still describes what the stamp interrupts.

**Reported:** the failsafe fired while the chest-opening window was up and the window closed
instantly, losing the interaction.

**This is the failsafe working as designed, and the design is wrong in this case.** The chain is
readable end to end:

1. `ForceCloseEncounter` → `encounterService.Close()` sets `forceClose = true` → `OnCloseEncounter()`.
2. `OnCloseEncounter` sees `encounterWindows.encounterInProgress == true` and calls the game's
   `encounterWindows.RewardFinished()`.
3. That re-enters our `RewardFinished_Prefix`, where `IsClosable()` is now true *because of*
   `forceClose` → `ClearClosedEncounters()`, `return true`.
4. The game's `RewardFinished` runs and the window closes — under a player who was still choosing.

The failsafe cannot distinguish "this peer is stuck waiting for others" from "this peer is actively
using its window while *another* peer is stuck". It releases both.

**Related and already documented:** [SE-5](07-shared-experience-audit.md#se-5) describes the same
instant-closure symptom reached by a different trigger — a late report generating a second
`CloseEncounter` broadcast that lands on a window the peer has since opened. Both are the same
underlying gap: **a release carries no round identity, so it cannot be addressed to the round it was
meant for.**

**Fix shape.** Do not force-close a window whose owner is still interacting: gate the failsafe on
this peer actually being in the waiting state (`encounterService.IsWaiting` **and** the local window
having already reported), rather than on the barrier being stale globally. Round identity makes this
exact rather than heuristic.

---

<a name="ob-5"></a>
## OB-5 — A client's magnet pull often does nothing — CONFIRMED, cause narrowed

**Reported:** power-up magnets picked up by clients sometimes do not work at all.

**Evidence, 2026-08-07 session.** Both peers drop follow instructions for pickups they cannot
resolve:

| Log | Handler | Count |
|---|---|---|
| Client | `OnReceivedPickupFollowingPlayer` — *"Pickup N not found in PickupManagerService"* | **184** |
| Client | `OnReceivedPickupApplied` | 14 |
| Host | `OnReceivedPickupApplied` (all for the client's id `1296713703`) | 14 |
| Host | `HandleWantToStartFollowingPickup` | 1 |

A dropped `PickupFollowingPlayer` is exactly the reported symptom: the instruction that makes an orb
fly to a player names a pickup id the receiving peer has never registered, so nothing moves. 184 of
them in one session is not an edge case.

**Four things the numbers settle.**

1. **It is not packet loss.** `PickupFollowingPlayer` (tag 12), `PickupApplied` (11), `SpawnedPickup`
   (10) and `SpawnedPickupOrb` (9) are all `ReliableOrdered`, and the host sends them on one channel,
   so they cannot be lost and cannot overtake each other. The id is genuinely absent from the
   receiver's registry.
2. **It is not one peer's fault.** The client's 184 failures split almost evenly by owner — 93 for
   its own id, 91 for the host's. Both players' pulls fail on the client.
3. **The same pickup fails repeatedly, for both owners.** Id `3037` fails at client lines 3867 and
   3873; id `3852` fails at 3868 attributed to the host and at 3874 attributed to the client. So two
   peers are contending for one orb and the instruction is re-sent.
4. **It is bursty and worsens over a run.** Distribution by line: 2 / 8 / 1 in the early thousands,
   then **66** and **107** in the last two blocks. 173 of 184 are late-run.

**The rate is the thing to look at.** `[bw]` on the host shows `PickupFollowingPlayer` reaching
**229–233 sends/s** at 10 B each, and `PickupApplied` up to **51/s**. A per-frame re-send of "this
orb is following you" for every in-flight orb is a lot of traffic to spend on an instruction that is
being dropped 184 times.

**Most likely mechanism, UNVERIFIED.** The pickup was already consumed and removed from
`PickupManagerService` — by the other player, or by the receiver itself — before the follow
instruction for it was handled. Two players magnetising the same field is exactly when that races,
which fits the "both owners", "same id twice" and "late-run burst" observations. The competing
explanation, that the spawn was never registered on that peer, is not ruled out: **the success path
of `OnReceivedSpawnedPickup` logs nothing, so the log cannot distinguish "never registered" from
"registered then removed".**

**Next step, cheap and decisive.** Log the registry size and whether the id was ever known, at the
point of failure — a `HashSet<uint>` of every id ever registered this session, checked in the
warning branch. "Never seen" and "seen then removed" want different fixes: the first is a spawn
replication gap, the second is a lifetime/ordering problem where the fix is to ignore the pull
silently rather than warn.

**Do not fix by making the message unreliable or by suppressing the warning.** The warning is the
only visibility this path has.

---

<a name="ob-6"></a>
## OB-6 — A shrine's charge *value* is never transmitted — CONFIRMED, cause exact

**Reported:** shrine counters are not in sync between all players.

> **Scope corrected 2026-08-13, and the correction matters.** This entry was filed as "shrine
> counters differ" and that title was wrong twice over. It is about the per-shrine **charge value** —
> the bar that fills while someone stands on it — and not about the run's interactable tally in the
> HUD. And the tally evidence points the other way: **`Charge Shrines` is the one counter that does
> agree** between peers, while `Chests`, `Greed Shrines`, `Moais`, `Pots` and `Boss Curses` all
> diverge. That is [OB-12](#ob-12), and its cause is unrelated to anything below.
>
> Everything below stands as an analysis of the charge value. What is no longer claimed is that it is
> what players were seeing in the counter list.

**Cause, exactly: the charge value is never transmitted.** The two messages that exist carry
identity only —

```csharp
// src/common/Messages/GameNetworkMessages/StartingChargingShrine.cs
public uint ShrineNetplayId;
public uint PlayerChargingId;

// StoppingChargingShrine.cs — the same two fields
```

— and a repo-wide search for a charge amount, progress or percentage on the wire returns **nothing**.
So the protocol replicates *who is charging*, and every peer then accumulates the shrine's charge
**locally** from its own belief about that set. The counter is not synchronised at all; it only
happens to agree when every peer's charger set and frame timing agree.

**Which they demonstrably do not.** The host logged
`Another player is still charging this shrine. Preventing stop trigger.` — the host's charger set
held two entries when a stop arrived. The client logged no shrine lines at all, because its
equivalent paths only log on failure. Two peers, two independently-accumulated counters, and at
least one point where their charger sets differed.

**Why this is worse than a cosmetic mismatch.** Whoever reaches full charge first fires the
completion locally. If the accumulation rate scales with the number of chargers — which is the
natural reading of a "counter" that changes when a second player joins — then a peer that thinks one
player is charging and a peer that thinks two are will complete at different times, and the shrine's
reward fires on a schedule the other peer has not reached.

**The one lookup that settles the rate question.** `ChargeShrine`'s per-frame accumulation in
`dump.cs` (buildid 21750826) — specifically whether it multiplies by a charger count or is a flat
rate. Flat means the divergence is only start/stop timing and a modest correction suffices; scaled
means the counters diverge by a factor and the value has to be replicated.

**Fix shape, not yet chosen.** Either make the host authoritative for shrine charge and replicate the
value at a low rate (a new append-only union tag; the value changes continuously but is small and
tolerates `Unreliable` because a later packet supersedes it), or keep it local and replicate only the
completion event so that at least the *outcome* agrees while the bar may briefly disagree. The second
is much cheaper and is probably enough if what players notice is the reward, not the bar.

**Related:** the pylon and lamp barriers share `HandleChargingStart` / `HandleChargingStop` with the
shrine, so if their progress is displayed anywhere they have the same defect by construction. Only
the shrine was reported.

---

<a name="ob-7"></a>
## OB-7 — Desert final-boss pylons do not exist for clients — CONFIRMED, cause exact

**Reported:** on the Desert final boss the pylons do not appear or work for non-host clients. **The
Forest final boss is fine.** That contrast is the whole diagnosis.

**Evidence.** Client log, 18 × `Pylon object not found in SpawnedObjectManagerService when
processing OnReceivedStartingToChargingPylon` and 16 × the `…StoppingChargingPylon` equivalent, all
inside the final fight. The host meanwhile charges pylons normally (`Player <id> stopping charging
pylon 5 / 7 / 10 / 16 / 18 / 21 / 32 / 35`). The host has pylons; the client cannot resolve their
ids.

**Cause: pylon ids are assigned by local determinism, not replicated — and the counter they draw
from is not deterministic across peers.**

[`Patches/FinalFightController.cs:38-51`](../../src/plugin/Patches/FinalFightController.cs) runs on
**every** peer with no host gate:

```csharp
//Since we are using a fixed seed, the pylons will always spawn in the same order
foreach (var pylon in __instance.pylons)
{
    var netplayId = spawnedObjectManagerService.AddSpawnedObject(pylon.gameObject);
    DynamicData.For(pylon.gameObject).Set("netplayId", netplayId);
}
```

`AddSpawnedObject` is documented **"Server side"** and allocates from one shared counter:

```csharp
// SpawnedObjectManagerService.cs
private int currentObjectId = 0;
public uint AddSpawnedObject(GameObject obj)        // "Server side"
    => (uint)Interlocked.Increment(ref currentObjectId);
public void SetSpawnedObject(uint id, GameObject o) // "Client side" — does NOT advance the counter
```

The seed makes both peers iterate the *same pylons in the same order*. It does nothing about **where
the counter already is**, and every other spawned object shares it:

| Object | Host | Client | When |
|---|---|---|---|
| Tumbleweed (`SynchronizationService.cs:4621`, host-gated in `InteractableTumbleWeed.OnEnable_Postfix`) | `AddSpawnedObject` — counter **+1** | `SetSpawnedObject(hostId, …)` — counter **unchanged** | **Desert only**, continuously |
| Revive coffin (`SpawnReviver`, `:4265`) | `if (isHost) AddSpawnedObject` — counter **+1** | uses the replicated `ReviverId` — counter **unchanged** | **any map**, once per player death |
| Pylons (`FinalFightController.cs:48`) | `AddSpawnedObject` | `AddSpawnedObject` — **both advance, from different starting points** | final fight |

So every host-side spawn moves the host's counter and leaves the client's where it was. By the final
fight the two are far apart, both peers allocate pylon ids from their own counter, and the client's
lookup of a host-assigned pylon id finds nothing.

> **The revive coffin is not a Desert feature**, despite the code calling it `desertGraveInstance`
> and typing it `InteractableDesertGrave`. `SpawnReviver` is the coffin that appears when *any*
> player dies on *any* map; it simply reuses the game's `EffectManager.Instance.desertGraves[0]`
> prefab as its visual. An earlier revision of this entry claimed it was Desert-only and built the
> Forest/Desert contrast partly on that. It was wrong, and the corrected conclusion below is
> sharper.

**Why Desert reliably breaks, and why Forest is not actually safe.** Tumbleweeds are Desert-only —
`NetworkHandler.Update` gates their tick on `MapController.runConfig.mapData.eMap == EMap.Desert` —
and they spawn continuously throughout a run, so on Desert the counters are guaranteed to be far
apart long before the final fight. On Forest the only divergence source is the revive coffin, which
costs one id **per death**. If nobody died before the final boss the counters were still in step and
the pylon ids happened to agree.

**So Forest working is a coincidence of that run, not a property of the map.** The same failure
should be reproducible on Forest by having a player die once before reaching the final fight — which
is the cheapest way to confirm this diagnosis, and worth doing before the fix, because it would rule
out any remaining Desert-specific explanation.

**Fix shape.** Replicate pylon ids instead of deriving them. The host should allocate and broadcast
(the `SpawnedObject` path already exists), and the client should `SetSpawnedObject` with the host's
id like every other replicated object. The seed can stay — it keeps *positions* consistent — but it
must not be load-bearing for identity.

**A shared mutable counter used as an implicit protocol is the general defect here**, and pylons are
just where it surfaced. Any future object registered with `AddSpawnedObject` on both peers has the
same latent bug.

---

<a name="ob-8"></a>
## OB-8 — The Desert final boss does not move for clients — **decompile done 2026-08-17**, cause narrowed, no fix

**Reported:** alongside OB-7, the Desert final boss was completely static on the non-host peer.
Forest's final boss behaved correctly.

**What is established.** Bosses are deliberately exempted from distance throttling —
[`Patches/Enemies/Enemy.cs:372,395`](../../src/plugin/Patches/Enemies/Enemy.cs) returns `true` early
for `IsBoss() || IsStageBoss() || IsFinalBoss()`, so `MyUpdate` and `MyFixedUpdate` are never
suppressed for a boss on either peer. Throttling is therefore **not** the cause. `EnemyManagerService`
also has a dedicated `EEnemyFlag.FinalBoss` case, so the final boss is expected to be a normal
replicated enemy. Enemy replication was healthy during the fight: `SpawnedEnemy` at 13–22 sends/s
and `LobbyUpdates(enemies)` at 35–40/s.

**The suspicious asymmetry.** `FinalFightControllerPatches.SpawnBoss_Prefix` returns **false** for
clients, so a client never runs `FinalFightController.SpawnBoss` at all. Its boss can therefore only
come from the host's replication. If the host's `SpawnBoss` instantiates the boss directly rather
than going through `EnemyManager.SpawnEnemy` — the method our `SpawnEnemy_Prefix` patches to assign a
netplay id and broadcast `SpawnedEnemy` — then the boss is never registered in
`EnemyManagerService`, never appears in `GetAllEnemiesDeltaAndUpdate`, and never receives a position
update on the client. A boss that is present but frozen is exactly what that produces.

> **Answered 2026-08-17 while diagnosing [OB-16](#ob-16).** `FinalFightController.SpawnBoss`
> (VA `0x180493EC0`) **does** route through `EnemyManager.SpawnBoss`, which calls
> `EnemyManager.SpawnEnemy`. That is the branch this entry predicted would mean the cause is the
> client's `SpawnBoss_Prefix` returning false. **Re-read the section below on that basis** — the
> lookup it asks for is no longer outstanding, and no fix has been written.

**The one lookup that decided it.** Decompile `FinalFightController.SpawnBoss` and check whether it
calls `EnemyManager.SpawnEnemy` or instantiates a prefab directly:

```bash
"$APPDATA/ghidra/ghidra_12.1.2_PUBLIC/venv/Scripts/python.exe" scripts/re/decompile_headless.py FinalFightController
```

Resolve the VA from `megabonk-re/build-21750826/dump.cs` — **use the `VA:` field, not `RVA:`**.

**Do not fix before that lookup.** If the boss does route through `SpawnEnemy`, the cause is
something else entirely and the obvious "replicate the boss" patch would be a second, redundant
spawn path.

**Possibly the same root as OB-7.** Both were reported on Desert and both were absent on Forest, and
OB-7's counter divergence is *not* Desert-specific in principle — it is merely guaranteed there. If
the boss is resolved through a spawned-object id rather than an enemy id, then OB-7 and OB-8 are one
bug with two symptoms and one fix. **Check this before treating them separately**, and note that the
Forest-with-a-death reproduction suggested in OB-7 would test both at once.

---

<a name="ob-9"></a>
## OB-9 — Enemy max health is never replicated — cause corrected, **FIXED and CONFIRMED in play (2026-08-17)**

**Reported:** for summoned bosses (bush, bandit) the **current** health agrees between players but
the **maximum** does not.

**Cause: max health is not on the wire anywhere.** Both enemy-carrying messages transmit current HP
and nothing else:

```csharp
// SpawnedEnemy — the spawn message
public float Hp { get; set; }          // current only
public int Wave { get; set; }
public bool CanBeElite { get; set; }
public float ExtraSizeMultiplier { get; set; }

// EnemyModel — the 40 Hz delta stream
public float Hp { get; set; }          // current only
```

A repo-wide search for `MaxHp` / `MaxHealth` under `src/common/` returns **players only**
(`Player.MaxHp`, `PlayerUpdate.MaxHp`). There is no enemy equivalent.

### Cause corrected 2026-08-16 — it was replicated, just sampled too early

This entry said max HP is **derived locally on each peer**. It is not derived at all.
`SynchronizationService.ProcessSpawnedEnemy` assigns it outright:

```csharp
enemy.hp = spawnedEnemy.Hp;
enemy.controlHp = spawnedEnemy.Hp;
enemy.maxHp = spawnedEnemy.Hp;          // ← max comes straight off the spawn message
enemy._hp_k__BackingField = spawnedEnemy.Hp;
```

Setting max from current is correct *if* the message carries the enemy's full health — which it does
for an ordinary enemy, because a fresh spawn is undamaged. **The defect is the sampling moment.**
Every HP scaler runs in the **caller** of `EnemyManager.SpawnEnemy`, after it has returned, and
`SpawnEnemy_Postfix` fires in between:

```
caller → EnemyManager.SpawnEnemy(...)
              └── [mod] SpawnEnemy_Postfix → sends SpawnedEnemy { Hp = unscaled }
       → Enemy.SetSwarmMultiplierHp(...)          ← scaling happens only now
```

So the spawn message carries pre-scaling HP. The client writes all four fields from it, then the
40 Hz `EnemyModel` delta — which carries `Hp` and nothing else — pulls **current** HP up to the
scaled value and leaves the maximum behind forever. Hence current agrees and maximum does not.

**Which enemies, precisely.** An xref on `Enemy.SetSwarmMultiplierHp` gives five callers:

| Caller | What it spawns |
|---|---|
| `EnemyManager.SpawnBoss` | minibosses, coffin bosses, the stage boss — all confirmed to funnel here |
| `SwarmSummoner.SpendCredits` | swarm enemies |
| `InteractableCharacterFight.SpawnEnemy` | the character fight — bandit |
| `InteractableDesertGrave.SpawnEnemy` | grave spawns |
| `InteractableSkeletonKingStatue.SpawnEnemy` | statue spawns |

The three interactables call `SpawnEnemy` and scale it **themselves**, which is why the report reads
as "any interactable that spawns bosses". Note `ChallengeSummoner.SpendCredits`, which this entry
originally blamed, calls `SpawnEnemy` directly and never scales — so it was the wrong suspect.

**Why boss curses decide it.** `SpawnBoss` applies the scale only when `GameManager.bossCurses > 0`.
Boss curses accumulate over a run, so an early boss is genuinely unaffected and a late one is not —
which is a better account of "minibosses seem fine" than a per-spawner distinction.

### The fix has landed

New append-only union tag **76, `EnemyMaxHpScaled`** (`Id`, `Hp`), sent host-side from a postfix on
`Enemy.SetSwarmMultiplierHp` — the one point all five callers share — and applied on the client to
the same four fields. Reliable, because this is a one-shot transition that nothing supersedes: the
delta stream carries current HP only, which is exactly why the maximum never self-corrected.

A field on `SpawnedEnemy` was **not** an option: MemoryPack is positional, so widening a shipped
message corrupts sessions between builds silently. And the message cannot simply be sent later —
the five callers share no "done" hook.

**The client buffers a correction that outruns its spawn.** Both messages go `ReliableUnordered`,
which guarantees delivery but explicitly not order, and the scaler runs in the same frame as the
spawn, so the two are always in flight together. The buffer is cleared on `Reset`; ids come from
`Interlocked.Increment` and are never reused, so a stale entry is a leak rather than a
misapplication.

**Verified without a playtest:** builds against the real install's interop; the server still
compiles, so the new message stayed Unity-free; union tags remain unique.

**Unverified — not run in-game:**

| Question | How it would show |
|---|---|
| Do boss health bars now agree between peers? | the direct check — take a late-run boss, with boss curses accumulated |
| Is `InteractableCharacterFight` really the bush/bandit spawner? | read from the xref, not confirmed in play |
| Is the divergence cosmetic or gameplay-relevant? | still open — execute thresholds, percentage damage or phase transitions reading max HP would make it a correctness bug |

**Deliberately not covered:** `FinalFightController.SpawnGoons` scales via `SetMinibossGoon`, not
`SetSwarmMultiplierHp`, so final-boss goons keep the old behaviour. Final bosses were reported as
fine, and widening the fix to a path with no reported symptom risks changing something that works.

---

<a name="ob-10"></a>
## OB-10 — Clients emit host-only-send warnings by the thousand — CONFIRMED, **wrong source recorded; now attributed**

Not player-visible, found in the logs. The client emitted **1,841** of these in one session, first
at line 4149 and continuing to the end of the run.

The warning comes from `UdpClientService.EnsureIsHost()`, so a client is repeatedly entering a
host-only send path — `SendToAllClients`, `SendToAllClientsExcept`, `SendToClient` or
`SendStreamUpdate`. The guard does its job and the send is dropped, which is why nothing breaks. But
each rejected call did the work leading up to it, and at ~1,800 per run something is being attempted
several times a second on a peer that can never perform it.

**Why it matters beyond noise.** The standing rule in `CLAUDE.md` is that every patch opens with a
session check *and an ownership check*; a client reaching a host-only send means some path is
missing the second. `EnsureIsHost` is catching the consequence rather than the cause, and it cannot
say which caller it caught — the warning has no context at all.

**Next step.** Include the caller in the warning (`[CallerMemberName]` on `EnsureIsHost`, which costs
nothing at runtime) and re-run. That converts 1,841 anonymous lines into a ranked list of ungated
paths, which is a far better starting point than reading every call site.

### 2026-08-17 — the recorded source is not the one firing, and it is 11x worse

`UdpClientService.EnsureIsHost` logged **zero** in the run. The count came from the Steam
transport's own copy of the guard, `SteamNetTransport.EnsureIsHost`, whose message reads
`[steam-net] Only the host can perform this action.` — **20,078 of them on the client**, against the
1,841 recorded here from the matchmaker transport. `UseSteamTransport` is now the default, so this
entry was describing the guard that no longer runs.

**The recorded next step would not have helped.** `[CallerMemberName]` on the guard names one of
four send methods — `SendToAllClients`, `SendToAllClientsExcept`, `SendToClient`,
`SendToAllClients(byte[])` — and this entry already lists all four. It would have converted 20,078
anonymous lines into 20,078 lines saying something known.

**What was done instead.** Both transports now record `transport / sendMethod / messageType` into
`Services/HostGuardDiagnostics`, which reports a ranked, throttled summary. **The message type is the
identifier that matters**: `SpawnedEnemy x1200` names the gameplay path, and therefore the patch
missing its ownership check, which no method name can.

**Aggregating is not silencing**, and this entry's warning against silencing still stands. Nothing is
suppressed, the count is reported in full, and it now says *what* was rejected. What is removed is
20,078 individual log writes on a path that runs several times a second — a per-frame cost the
performance doc and the BepInEx guidance both treat as real. A healthy session prints nothing,
because it rejects nothing.

**Next step is unchanged in spirit:** read the ranked `[host-guard]` line from the next run, then fix
the named paths upstream of the guard.


---

<a name="ob-11"></a>
## OB-11 — Minibosses always spawn near the host — CONFIRMED, **first fix FAILED in play; corrected, unverified**

**Reported:** minibosses appear next to the host rather than somewhere between the players. A client
playing away from the host sees them arrive on top of their teammate, never near themselves.

**Why this is structural rather than a bug in a line somewhere.** The mod does not choose spawn
positions and never has. It observes them:

```csharp
// Patches/Enemies/EnemyManager.cs — SpawnEnemy_Postfix
var isServer = synchronizationService.IsServerMode() ?? false;
if (isServer)
{
    synchronizationService.OnSpawnedEnemy(__result, enemyData.enemyName, pos, /* … */);
}
```

`pos` is whatever the game's own spawner computed, on the host, a moment earlier — and it is
replicated verbatim. `SpawnBoss_Prefix` suppresses the client's own spawning entirely, so the client
contributes nothing to the decision.

**The game is a single-player game and its spawner knows about one player.** On the host that is the
host. So every spawn ring, every "just outside the camera" offset and every distance check is
measured from the host's position, and the result is faithfully sent to everyone. Host-authoritative
spawning is correct; host-*centred* spawning is the accident that comes with it.

### Promoted LIKELY → CONFIRMED by decompile, 2026-08-16 — and the recorded anchor was wrong

This entry previously named `GameManager.Instance.player` as the spawner's anchor. **It is not.**
The anchor is the static singleton **`MyPlayer.Instance`** (`MyPlayer` static field at offset `0x8`).
`GameManager.Instance` *is* touched in the same body, but only to fetch a layer mask for the ground
raycast — never for a position. The distinction matters for the fix: `MyPlayer.Instance` is each
peer's **own local player**, so it is not something that can be swapped for a remote peer's player.

Five bodies decompiled, all in `Assets.Scripts.Game.Spawning.SpawnPositions`, plus the miniboss
caller. Cached under `megabonk-re/decompiled/`.

| Symbol | VA | What the body does |
|---|---|---|
| `SummonerController.EventMiniboss` | `0x180487240` | the miniboss caller — see chain below |
| `SpawnPositions.GetEnemySpawnPosition` | `0x180483380` | reads `MyPlayer.Instance`, calls `MyPlayer.GetSpawnDirectionBias()`, delegates to ↓ |
| `SpawnPositions.GetEnemySpawnPositionBiased` | `0x180482390` | **the ring is centred on the local player** — see below |
| `SpawnPositions.GetPositionAroundPlayer` | `0x180483BA0` | thin wrapper: `GetEnemySpawnPositionBiased(null, 0f, 50, maxDistance)` |
| `SpawnPositions.PredictPlayerPosition` | `0x180484570` | `MyPlayer.Instance.playerMovement.rigidbody.position + velocity * time` |
| `SpawnPositions.GetPlayerMovementDirection` | `0x180483B20` | returns `MyPlayer.Instance.averageMovingDirection` (`0xF4`) |

**The centre, exactly.** `GetEnemySpawnPositionBiased` computes its ring centre as
`MyPlayer.Instance.playerMovement(0x28).rigidbody(0x48).position + velocity * Enemy.<static 0x4C>` —
an inlined `PredictPlayerPosition`. Each of `attempts` candidates is then
`centre + direction * Random.Range(minDist, maxDist)`, clamped to the map bounds from `FindBounds`,
raycast down, and resolved by `FindHitClosestToPlayerY`. `direction` is biased toward
`MyPlayer.Instance.averageMovingDirection`. **Every term is the local player.** There is no second
player anywhere in the body.

> Field names here are confirmed against `dump.cs` **offsets**, not read off Ghidra's applied structs
> — `MyPlayer.Instance` at static `0x8` and `averageMovingDirection` at `0xF4` both match the reads
> in the decompiled bodies. That check is exactly the trap `CLAUDE.md` records.

**The miniboss chain, end to end:** `SummonerController.EventMiniboss` → `MyPlayer.Instance`
null-check → `GetSpawnDirectionBias()` → `GetEnemySpawnPositionBiased(null, bias, 50, maxDistance)`
→ `EnemyManager.SpawnBoss(eEnemy, summonerId, 0x10, pos, sizeMult)`. The `0x10` is
`EEnemyFlag.SummonerMiniboss`, which is what pins this chain to minibosses specifically rather than
to enemies in general.

**One extra hazard the decompile turned up.** If the returned position lands within a small epsilon
of `SpawnPositions.INVALID_POS` — i.e. every one of the 50 attempts failed — `EventMiniboss` falls
back to **`MyPlayer.Instance.transform.position` directly**, spawning the miniboss exactly on top of
the local player. So the worst case is not merely "near the host", it is "inside the host", and any
fix has to handle that branch too rather than only the ring.

### Fix shapes, re-costed against the decompile

The decompile makes shape 3 **cheaper than shape 2**, reversing the order this entry had.

1. **Leave it.** Minibosses are an event both players can walk to. Not broken, only unfair to
   whoever is not hosting.
2. **Reposition after the fact in `SpawnEnemy_Postfix`.** ~~Cheapest~~ — now the *worst* option. The
   enemy already exists at `pos` by then, so it has to be moved, and anything the game cached from
   its original transform in `Awake` is stale. That hazard is real here: see the `SpawnedObject`
   clone-inactive-then-position note in
   [`SynchronizationService`](../../src/plugin/Services/SynchronizationService.cs), which exists
   because a component cached a prefab transform in `Awake`.
3. **Rewrite `pos` before the enemy is created — recommended.** `EnemyManager.SpawnBoss` takes
   `Vector3 pos` as a **parameter**, and
   [`EnemyManagerPatches.SpawnBoss_Prefix`](../../src/plugin/Patches/Enemies/EnemyManager.cs) already
   patches it, already runs host-only, and already runs *before* the enemy exists. Widening it to
   `ref Vector3 pos` puts the correction at the one point where it costs nothing:
   - the host picks a living player from the replicated roster,
   - re-rolls the position around that player with the game's **own public**
     `SpawnPositions.GetEnemySpawnPositionAroundPoint(Vector3 pos, float minRadius, float maxRadius,
     int attempts, bool onlyGround, float fromHeight)` (VA `0x180481F60`), which already does the
     ground raycast and bounds work,
   - and `SpawnEnemy_Postfix` → `OnSpawnedEnemy` then replicates the corrected `pos` verbatim.

   **No wire change, no new union tag, no repositioning of a live object.** The radii can be taken
   from the distance the game itself just chose (`|pos − hostPos|` in XZ, ±a few percent), which
   preserves the game's time-scaled distance curve without needing the private `GetSpawnDistances`.

### Shape 3 has landed — what to look for in play

`EnemyManagerService.RecentreBossSpawnOnRandomPlayer`, called from `SpawnBoss_Prefix` after its
host check. The host picks uniformly among alive players holding an avatar in the world, plus
itself; drawing itself leaves the position alone. Every failure path returns the game's position
unchanged, so the worst case is the old behaviour rather than a missing miniboss.

**Verified without a playtest** (these are compile- and metadata-level facts, not behaviour):

- it compiles against the **real install's** `BepInEx/interop`, not the committed `stripped-libs`,
  so both game signatures exist in build 21750826 — the `Exists()` fallback trap in `CLAUDE.md`
  did not fire here;
- `EnemyManager.SpawnBoss`'s fourth parameter is genuinely **named `pos`** in the proxy, which is
  what Harmony's by-name prefix binding requires — a renamed parameter would have thrown at patch
  time and surfaced only in the startup log;
- `GetEnemySpawnPositionAroundPoint(pos, minRadius, maxRadius, attempts, onlyGround, fromHeight)`
  matches the call argument for argument.

**Unverified — it has not run in-game, on one machine or two:**

| Question | How it would show |
|---|---|
| Is `GetEnemySpawnPositionAroundPoint` safe to call from inside a prefix here? | the `OB-11: could not re-centre` warning in the host log, or a hard crash at the first miniboss |
| Is `MIN_BOSS_RING_RADIUS = 20f` a sensible distance? | minibosses appearing on top of, or absurdly far from, the chosen player |
| Does a miniboss around a non-host player ever land somewhere the host's camera or pathing dislikes? | a miniboss that never reaches anyone, or one stuck in geometry |
| Does the share actually even out? | over a run, roughly half the minibosses should arrive near each player in a 2-player session |

The cheap check is the host log: `OB-11: no valid miniboss position around peer` means the re-roll
is running and failing, and its **absence together with minibosses appearing near the client** is
the pass.

**Do not "fix" this by spawning on each peer independently.** Two peers running their own spawner
would produce two different minibosses with different ids, which is the desync this architecture
exists to prevent — and the same reasoning that makes `SpawnBoss_Prefix` suppress the client.

---

<a name="ob-12"></a>
## OB-12 — The run's interactable counters diverge, except charge shrines — **attempt 2 WORKS for 7 of 9 rows (2026-08-17)**

### RE-DIAGNOSED 2026-08-17 — the fix was applied to branches your config never reaches

**The cause is exact and the evidence is arithmetic, not inference.**

**1. The numerator is per-peer; the denominator is shared.** `Microwaves 1/1` on one peer and `0/1`
on the other settles it: the map contains exactly **one** microwave, one peer used it, and the other
never counted it. Every divergent row is consistent with "each peer counts only what it personally
did", and the sums behave — Chests 2+6, Pots 5+8, Moais 0+1, Microwaves 1+0. Denominators agree
everywhere, so map spawn config replicates correctly.

**So the panel is not "a shared tally that diverged". It is a per-peer tally paired with a
shared-world denominator**, which is unreachable by construction: a player who opens 2 of 46 chests
in a run where 8 were consumed can never reach 46, and the remaining 38 no longer exist.

**2. Why last session's fix does nothing here.** The message is built as:

```csharp
Action = IsSharedExperienceEnabled() ? InteractableAction.Interact : GetActionByInteractable(instance),
```

**With shared experience on, every `InteractableUsed` carries `Action = Interact`.** The receive
switch in `OnReceivedInteractableUsed` has three branches:

| Branch | Counts? |
|---|---|
| `InteractableAction.Destroy` | yes — `CountInteractableAsUsed` (added last session) |
| `InteractableAction.Used` | yes — `CountInteractableAsUsed` (added last session) |
| `InteractableAction.Interact` | **no — zero calls** |

Last session's note reads "both shortcut branches now call `InteractablesStatus.OnInteractableUse`".
It was accurate and it was irrelevant: with shared experience enabled **neither of those branches is
ever reached**, and the third one was never touched.

**3. Corroboration from the capture.** The `Used` branch logs
`Net player used interactable with ID: …`. Across both peers for the whole session that line appears
**zero** times — exactly what `Action` never being `Used` predicts.

**4. Why charge shrines still agree**, unchanged from the original analysis and now the only part of
it that survives: both peers run their own `OnTriggerEnter`/`OnTriggerExit` for a charge shrine, so
both reach the counter without any message. Shady Guy agreeing at 2/4 is the same shape — and note
the send path explicitly skips `netplayId` warnings for `ShadyGuy` and `Microwave`, so those two
types are already known to travel differently.

### The fix is NOT "add the call to the third branch"

**The `Interact` branch replays the real interaction on some sub-paths and opts out on others.**
`microwave.Interact()` is called outright in two places, and the branch breaks early in several
others (dead player, cannot afford, cannot interact). Where the real interaction runs, **the game
increments the counter itself** — so a blanket `CountInteractableAsUsed` at the top of the branch
would double-count exactly those cases, turning a too-low counter into a too-high one.

Three shapes, and the choice is a real one:

1. **Count only in the opt-out sub-branches.** Correct, and matches how the branch already reasons
   — but it is many sites (microwave, shrine balance, moai, chest, …), each needing the same
   judgement, and a future sub-branch will silently miss it.
2. **Measure instead of assume: read `numUsed` for this interactable before and after the branch
   runs, and call the helper only if it did not move.** Immune to which sub-path executed, and
   stays correct when sub-branches are added. Costs one dictionary read either side and a little
   more interop surface.
3. **Decide the panel is per-peer by design and leave it.** Defensible for a personal-progress
   readout — but the denominator makes it incoherent, so this really means changing the
   denominator, not leaving it alone.

**Option 2 has landed.** `InteractableCounterHelper.Take` reads `numUsed` before dispatch and
`CountIfDispatchDidNot` counts only if it did not move, so whichever sub-path ran the interactable is
counted exactly once — and a sub-branch added later needs no matching change. The debug name is
captured up front because it comes off the live component and some paths destroy the object.

Chosen for that property rather than for elegance: this entry has had its cause corrected three
times, and the failure each time was reasoning about which path runs instead of checking. This shape
does not need that reasoning to be right.

**What confirms it: the Microwaves row.** The map holds exactly one microwave, so a working fix reads
`1/1` on **both** peers. That single row is a cleaner test than any other, because the two values
cannot legitimately differ. If chests and pots now agree too, the whole class is fixed.

**Watch for the opposite failure.** If a counter now reads *higher* than the map total — `50/46` —
the comparison is failing and something is counting twice.

### Result 2026-08-17 — it works, except for the two types the code already calls out

Both peers, sampled at two points in one run:

| Counter | early: A / B | late: A / B | |
|---|---|---|---|
| Boss Curses | 4/4 · 4/4 | 4/4 · 4/4 | agree |
| Charge Shrines | 15/15 · 15/15 | 15/15 · 15/15 | agree |
| Chests | 15/46 · 15/46 | 17/46 · 17/46 | agree |
| Greed Shrines | 7/8 · 7/8 | 8/8 · 8/8 | agree |
| Magnet Shrines | 1/3 · 1/3 | 2/3 · 2/3 | agree |
| Moais | 5/5 · 5/5 | 5/5 · 5/5 | agree |
| Pots | 45/55 · 45/55 | 49/55 · 49/55 | agree |
| **Microwaves** | **0/2 · 1/2** | 1/2 · 1/2 | lagged, then converged |
| **Shady Guy** | **2/2 · 4/2** | **2/2 · 4/2** | **over-counts, and exceeds its own total** |

**Seven of nine rows now track exactly, including chests and pots**, which were the worst offenders
(2/46 against 6/46 before). The measured before/after approach did what it was meant to.

**The two that fail are the two the code already flags.** `OnInteractableUsed`'s send path contains:

```csharp
if (!instance.name.Contains("ShadyGuy") && !instance.name.Contains("Microwave"))
{
    logger.LogWarning("Interactable does not have a netplayId ...");
}
return;   // ← both types leave here without a netplayId
```

with the comment *"those guys are so shady that they don't work on client side for some reason"*.
They travel differently from every other interactable, so it is unsurprising they are the two the
counter fix does not reach.

**Shady Guy reads 4 of 2** — double-counted, and past its own denominator. `InteractablesStatus` has
a dedicated `OnShadyGuyUsed()` handler alongside `OnInteractableUse`, and the helper's own note says
`OnInteractableUse` "skips the types that have their own handlers". So the likely shape is that the
shady guy is counted by its own handler *and* again by ours — the before/after comparison cannot see
it, because the second increment happens through a path the snapshot does not bracket.

### Shady guy — the peers were recorded the wrong way round

**Corrected by the reporter: the HOST reads 4/2; the CLIENT reads 2/2.** The first write-up here had
them inverted, and a fix was built on that inversion and then reverted.

**What the correct attribution implies.** The acting peer never receives its own `InteractableUsed`,
so `InteractableCounterHelper` never runs on it. The host used the shady guys, therefore:

- the **host's 4 is entirely game-side** — nothing the mod's counter does can reach it;
- the **client's 2/2 is correct**, and it is correct *because* the helper counts the replicated
  interaction.

Excluding shady guys from the helper — the reverted change — would have left the client at **0/2**
with the host still at 4/2. Strictly worse than the reported state.

**Two candidate causes for the host's double, and they point opposite ways:**

1. **The game counts a shady guy twice per use.** `OnInteractableUse` does *not* skip
   `InteractableShadyGuy` — decompiled (VA `0x18051B1D0`) it skips exactly one type,
   `InteractableMicrowave`, by an explicit `GetType()` comparison. So a shady guy plausibly reaches
   both `OnInteractableUse` and its own `OnShadyGuyUsed`. If so this is vanilla and the client's 2 is
   the anomaly, not the host's 4.
2. **A shady guy is usable more than once**, so 4 uses across 2 objects is the numerator counting
   *uses* against a denominator counting *objects*. Then the host is right and the client is
   undercounting, because only one use per shady guy is being replicated.

**The check that separates them needs no code and no second machine: play singleplayer, use one
shady guy twice, and read the row.** If it climbs past its own total there, it is the game's
behaviour and out of scope. If it stays at 1/2, the double is netplay-specific and candidate 1 is
live.

**Do not change the counter helper before that check.** This row has now had a fix built on a wrong
premise once.

> **Resolved 2026-08-15.** The counter was found and the fix is in. It is **not** `RunStats` —
> that was this entry's one wrong turn, and the detour is kept below because the reasoning that
> corrected it is reusable.
>
> **The counter is `InteractablesStatus`**, a static `Dictionary<string, InteractableStatusContainer>`
> keyed by an interactable's debug name, each entry holding `numUsed` and `numTotal` — the two
> halves of "Chests 4 / 46". It backs the panel behind the **Shrine Counter** setting, at
> `GameUI/GameUI/Debug/EnemyInformation/Shrines`. Identified from the object path rather than by
> more decompiling; one sentence from whoever had the game open beat an xref pass over every UI type.
>
> **The mechanism this entry described was right all along**, just attributed to the wrong class.
> `InteractablesStatus.OnInteractableUse` is raised from the interactable's own interaction path;
> the mod's `Destroy`/`Used` shortcut replaces that path; the remote peer never counts it. And
> `InteractablesStatus.OnChargeShrineCharged` is a separate handler reached by both peers running
> their own trigger callbacks — which is why charge shrines were the one row that agreed. That
> prediction held under a changed cause, which is the strongest thing this entry did.
>
> **Fix:** both shortcut branches call `InteractablesStatus.OnInteractableUse(interactable, true)`
> before taking the shortcut. See `Helpers/InteractableCounterHelper`.

**Reported, with two screenshots of the same moment in one run:**

| Counter | Peer A | Peer B |
|---|---|---|
| Charge Shrines | 14 / 15 | 14 / 15 |
| Boss Curses | 2 / 3 | 0 / 3 |
| Chests | 4 / 46 | 3 / 46 |
| Greed Shrines | 0 / 8 | 7 / 8 |
| Moais | 1 / 4 | 2 / 4 |
| Pots | 13 / 55 | 24 / 55 |
| Challenges, Magnet Shrines, Microwaves, Shady Guy | agree (all at 0, or 1/1) |

**The one that agrees is the interesting row.** Everything that diverges has a large, arbitrary gap —
`Pots` differs by eleven, `Greed Shrines` by seven — which is not drift or timing. Those peers
counted genuinely different sets of events.

**Cause: the mod replicates an interactable's *outcome* by shortcut, and the game's counter lives
inside the path that was shortcut.** From `OnReceivedInteractableUsed`
([`SynchronizationService`](../../src/plugin/Services/SynchronizationService.cs)):

```csharp
switch (used.Action)
{
    case InteractableAction.Destroy:
        GameObject.DestroyImmediate(interactableObj);   // ← the object goes away
        break;
    case InteractableAction.Used:
        logger.LogInfo($"Net player used interactable with ID: {used.NetplayId}");
        break;                                          // ← nothing happens at all
    case InteractableAction.Interact:
        // … actually calls the game's Interact() for microwaves and friends
}
```

`Destroy` removes the object without ever running the game's `Interact`, and `Used` does nothing but
write a log line. The game increments its own run tally inside the method neither of those calls, so
the remote peer sees the object vanish and never counts it. Whoever performed the interaction counts
it; nobody else does. Two players splitting a map therefore end with two tallies that add up to
roughly the right total between them and match nowhere.

That also explains the exception: **charge shrines are not replicated by outcome.** Their charging is
replicated as start/stop and *both* peers run their own `OnTriggerEnter` / `OnTriggerExit`, so both
peers reach the completion through the game's own path — and both count it. The counter agrees
because the mechanism is different, not because the shrine is special.

### What the decompile actually found — and it corrects the paragraph above

**The tally is not incremented anywhere on the chest's own interaction path.** Three functions
decompiled (cached in `megabonk-re/decompiled/`, buildid 21750826):

| Function | VA | What it calls |
|---|---|---|
| `InteractableChest$$Interact` | `0x180452FB0` | `EncounterWindows.AddEncounter`, `ChestUtility.ChestTypeToEncounter`, a localised string and a text popup |
| `InteractableChest$$OpenChestImplementation` | `0x180453990` | `ChestUtility.OpenChestNoAnimation`, `EncounterWindows.AddEncounter` |
| `InteractableChest$$OnChestWindowClose` | `0x1804532B0` | `MoneyUtility.GetChestPrice`, `PlayerInventory.ChangeGold`, `Instantiate`, `Destroy` |

**No counter, no stat call, on any of the three** — including the one that actually consumes the
chest. So the guess above ("the game increments its own run tally inside `Interact`") is wrong in its
specifics, and saying so is the point of this section: the shortcut in
`OnReceivedInteractableUsed` is real, but it is not skipping an increment that lives in `Interact`,
because there is not one.

### The xref answers it: the tally is event-driven

Run with the new `scripts/re/xrefs_headless.py` (results cached under `megabonk-re/decompiled/`):

```
RunStats$$AddValue          <- called only by TrackStats$$AddValue
TrackStats$$AddValue        <- called by ~20 handlers:
                               OnChestOpened, OnChestBought, OnInteracted, OnShrineCharged,
                               OnShadyGuyUsed, OnMicrowaveExploded, OnChallengeShrineCompleted,
                               OnPickup, OnEnemyDied, OnEvade, OnGoldChange, …
TrackStats$$OnChestOpened   <- referenced only as DATA, never called
```

**That last line is the finding.** A handler referenced only from the metadata tables and never by a
call instruction is a delegate target — something built a delegate from its method pointer and
subscribed it. `TrackStats` is a bank of subscribers to the game's static `Action`s, of which
`InteractableChest.A_ChestOpened` (`dump.cs:370556`) is one.

So the tally is raised by **events**, and the events are raised by the interactable's own code path.
That is the path the mod replaces:

- `InteractableAction.Destroy` removes the object without running it — no event, no tally.
- `InteractableAction.Used` writes a log line — no event, no tally.

The peer that performed the interaction runs the real path and counts it. Every other peer takes the
shortcut and does not. That is the whole defect, and it now has a named mechanism rather than an
assumption.

**And it explains the exception exactly.** `OnShrineCharged` is raised from the charge shrine's own
completion, and both peers reach that completion because charging is replicated as start/stop and
each peer runs its own `OnTriggerEnter` / `OnTriggerExit`. Both peers therefore raise the event and
both tally it — which is why `Charge Shrines` is the one row that agrees.

**What is still not pinned**, and it does not change the conclusion: which specific `Action` each
`TrackStats` handler subscribes to. IL2CPP delegate construction leaves only a metadata reference, so
the `+=` site is not reachable by cross-reference — it would need the subscribing function
(`TrackStats$$Init` or equivalent) decompiled and read. The mapping is only needed if a fix wants to
raise one event directly rather than replay the interaction.

**Fix shape, now that the mechanism is known.** Replaying the real interaction on the remote peer is
the only option that keeps the tally correct *and* keeps it derived — but `Interact` has side effects
the receiver must not run twice, which is presumably why the shortcut exists. Raising the specific
`TrackStats` event on the receiver is narrower and needs the mapping above. Replicating the tally
itself is cheapest and turns a derived value into a replicated one, which is a real cost of its own.

**Superseded — kept because the reasoning is the useful part.** Before the xref, this entry named two
candidates:

1. **`InteractableChest.A_ChestOpened`** — a `public static Action` at field offset `0x8`
   (`dump.cs:370556`). Something subscribes and tallies. If this is the mechanism, the mod's
   `DestroyImmediate` shortcut never raises it and the divergence follows immediately.
2. **`RunStats.AddValue(EMyStat stat, int value)`** — `RunStats` is a static class and
   `EMyStat.chestsOpened = 3`, `potsBroken = 13`, `shrineCharge = 16` exist. Someone calls it with
   those.

Both turned out to be the same mechanism seen from two ends, and the xref above joined them.

**Why an xref rather than another decompile.** Subscriptions (`+=`) and calls live in *bodies*, and
`dump.cs` lists only declarations — so grepping it cannot find a caller by construction. That gap is
what `scripts/re/xrefs_headless.py` now fills.

**What the answer changes.** If the tally is raised by `A_ChestOpened` or an equivalent per-type
event, the fix is to make the remote peer run the real interaction instead of destroying the object —
and the reason that was not done originally (side effects the receiver must not run twice) becomes
the actual design question. If instead the tally is derived by counting objects still in the scene,
then `DestroyImmediate` *would* have counted it, the counters should already agree, and the cause is
something else entirely — which would make this entry's whole premise wrong rather than merely
imprecise.

**The charge-shrine exception survives either answer**, and is the strongest evidence available:
whatever the mechanism is, both peers reach it for charge shrines because both run their own
`OnTriggerEnter` / `OnTriggerExit`, and only for charge shrines does the counter agree.

**Fix shape, not chosen.** Either replay the real interaction on the remote peer instead of
destroying the object — correct, and the reason it was not done that way originally is presumably
that `Interact` has side effects the receiver must not run twice — or replicate the tally itself as
part of the player record, which is cheap but makes the HUD a replicated value rather than a derived
one. **Neither is worth doing before the decompile**, because both assume the counter is where this
entry assumes it is.

### What a second pass found — and why "cause exact" is withdrawn

Three facts, all from `dump.cs` and one decompile on buildid 21750826. Together they say the entry
is describing a real mechanism that is **not the one behind at least half the reported rows.**

**1. `TrackStats.OnInteracted` is not a general interactable tally.** Decompiled
(`TrackStats$$OnInteracted`, VA `0x180417690`, cached under `megabonk-re/decompiled/`) it is a
two-branch dispatcher and nothing more:

| Type tested | Effect |
|---|---|
| `InteractablePot` | `AddValue(13, 1)` — `EMyStat.potsBroken` — then invokes a static `Action` (`A_PotBroken`) |
| `InteractableShrineMagnet` | one further `AddValue` |

Two `AddValue` calls in the whole function. Chests, moais, greed shrines and boss curses do not
pass through it; they must reach their tally by some other handler, or not at all.

**2. `EMyStat` has no member for three of the diverging counters.** The enum is complete at 0..50
and contains `chestsOpened`, `potsBroken`, `shrineCharge`, `shrineChallenge`, `shrineSucc`,
`chestsBought`, `microwavesExploded` and four `shadyGuys*` variants. There is **no** moai, no greed
shrine, and no boss curse. Those three rows diverged in the screenshots, so whatever counts them is
not `RunStats`.

**3. The reported counters have denominators; `RunStats` has none.** `RunStats.GetStat(EMyStat)`
returns a single `int`. "Chests 4 / 46" and "Pots 13 / 55" are a *used-of-present* pair. The
denominator looks like map spawn configuration — `RandomMapObject.amount` / `maxAmount`, the shape
behind fields such as `greedShrineSpawns` — which means the panel is a per-map completion readout
rather than a progression stat.

**So the open question is narrow and cheap to answer:** *which screen are those counters on?*
**Answered:** the Shrine Counter panel, `GameUI/GameUI/Debug/EnemyInformation/Shrines`, which is
`InteractablesStatus` — see the banner at the top. All three findings above stand and all three were
what ruled `RunStats` out; none of them found the answer, and the object path did, in one sentence.
Worth remembering the next time a counter needs identifying: ask where it is on screen before
decompiling what might produce it.

**What is still solid.** The shortcut in `OnReceivedInteractableUsed` genuinely skips the game's own
interaction path, the charge-shrine exception is genuinely explained by both peers running their own
trigger callbacks, and both remain true whatever the panel turns out to read. The tally mechanism is
also now known to be *reachable*: `RunStats.AddValue` is public, and Il2CppInterop exposes
`TrackStats`'s private statics as public on the proxy, so a fix can call either directly once it is
known which one the panel reads.

**Not a desync.** Both peers agree about which objects exist and which are gone; only the tally
disagrees. It is a scoreboard bug, not a world-state one, and nothing downstream reads it.

---

<a name="ob-13"></a>
## OB-13 — Enemies keep taking damage while an encounter window has the game paused — CONFIRMED, **FIXED at the damage path, unverified**

**Reported (2026-08-16):** with the game paused by an encounter window, bosses and enemies still
receive damage from projectiles. **Refined by the reporter: it happens only when shared experience is
enabled, where *both* players are paused by the reward pop-up.**

**That refinement is what makes it a defect rather than a design question.** The obvious benign
reading — your teammate is still playing, so their shots still land — requires somebody to be
unpaused. With shared experience nobody is: the barrier holds every peer until all have chosen. There
is no legitimate source of projectile damage during that window, so something is running that should
not be.

**And the mod deliberately holds that pause.** `EncounterWindowPatches.PopReward_Postfix` calls
`MyTime.Unpause()` **only when shared experience is off**; with it on it re-shows the window and
returns early, leaving the peer paused until the barrier releases. So the shared-experience pause is
not incidental — it is held open, potentially for many seconds ([OB-3](#ob-3) records the failsafe
firing on a 20-second cycle), and that long window is itself a clue: it is the one state that lasts
long enough for something else to interfere.

### What is established about the pause itself

`MyTime.Pause()` — VA `0x1803E5B20`, decompiled — does exactly three things:

1. sets `MyTime.paused = true` (static offset `0x0`),
2. sets `UnityEngine.Physics.simulationMode = 2` (`Script`),
3. raises `MyTime.A_Pause(true)`.

**It never touches `Time.timeScale`.** That is the important part. The pause is cooperative, not a
hard stop: it halts *automatic* physics stepping and sets a flag that the game's own update methods
are expected to check. Anything that does not check `MyTime.paused`, and anything that deals damage
from `Update` rather than from physics, keeps running at full speed.

### The game's own projectiles are not the culprit — that is now checked, not assumed

All three projectile update paths gate on the flag, decompiled on buildid 21750826:

| Symbol | VA | Body |
|---|---|---|
| `ProjectileBase.FixedUpdate` | `0x18042BBD0` | `if (!MyTime.paused) { MyFixedUpdate(); StepMovement(); … }` |
| `ProjectileBase.Update` | `0x18042D020` | `if (!MyTime.paused) { MyUpdate(); }` |
| `Rocket.FixedUpdate` | — | `if (!MyTime.paused) { StepMovement(); … }`, already recorded in [`Patches/Projectiles/Rocket.cs`](../../src/plugin/Patches/Projectiles/Rocket.cs) |

`Rocket` is the one projectile that does **not** derive from `ProjectileBase`, so between them these
cover the projectile hierarchy. **Every stock projectile therefore freezes on a paused peer, and
both peers are paused.** Whatever is dealing the damage is not the game's projectile simulation
running normally.

### The two candidates left

**1. Something clears the pause while the window is still up.** `MyTime.Unpause()` restores both
`MyTime.paused` and `Physics.simulationMode`, and the mod calls it from six places —
`ChestWindowUI`, `LevelUpScreen`, `PauseUi`, `SpawnPlayerPortal`, `EncounterWindow` and
`SynchronizationService`. Under shared experience the encounter pause is *held* rather than
released immediately, so it is the only pause long enough for one of those to interleave: a level-up
screen or chest window opening and closing during the wait would unpause the world underneath an
encounter window that is still displayed. **This fits the "shared experience only" condition exactly
and is the first thing to check** — without shared experience `PopReward_Postfix` unpauses at once,
leaving no window for the interleave.

**2. The mod's own injected components ignore the pause.** `MyTime.paused` is read in **exactly one
place in the entire plugin** — `Helpers/ScreenTextHelper.cs` — and nowhere else. Every injected
`MonoBehaviour` therefore ticks straight through: `ProjectileInterpolator.Update()` in particular
keeps teleporting replicated projectiles to host snapshots with no pause check at all. On its own,
moving a transform should not deal damage while `simulationMode` is `Script`, which is why this is
listed second — but combined with candidate 1 it would, and it is a standing hazard regardless.

### MEASURED 2026-08-17 — candidate 1 is dead

`LogEncounterPause` ran on both peers for a full session. **The flag never fired: zero
`WINDOW OPEN BUT NOT PAUSED` lines on either end.** The client's states across the whole run:

| State | Samples |
|---|---|
| `encounterWindow=False paused=False simulationMode=FixedUpdate` | 217 |
| `encounterWindow=True  paused=True  simulationMode=Script` | 213 |
| `encounterWindow=False paused=True  simulationMode=Script` | 4 |

**The pause holds for the entire window, every time.** Candidate 1 — something clearing the pause
underneath a window that is still displayed — is refuted. The four `window closed but still paused`
samples are the opposite ordering and are harmless.

**So candidate 2 is the answer by elimination**: the damage comes from something that never consults
`MyTime.paused`. The plugin reads that flag in exactly one file (`Helpers/ScreenTextHelper.cs`), so
every injected `MonoBehaviour` ticks straight through the pause — `ProjectileInterpolator.Update()`
foremost, which keeps teleporting replicated projectiles to host snapshots. Start there.

**Do not re-run the pause log.** It has answered its question; keeping it on is now noise.

### Fixed by gating the damage path, not by finding the source

Reported again on 2026-08-18, still under shared experience, and now explicitly for bosses. Rather
than keep hunting the un-gated component, the fix sits where every damage source converges:
`Enemy.DamageFromPlayerWeapon`, `DamageFromPlayerOther` and `DamageExternal` are prefixed and return
early while `MyTime.paused`.

**That covers the source without knowing it**, along with weapons, auras, damage-over-time and the
mod's own replicated damage. It also covers every pausing window — level-up, chest, microwave, moai,
shady guy — because all of them reach `MyTime.Pause()`, so the flag is read instead of the windows
being enumerated.

**`Enemy.MakeInvulnerable` was the obvious lever and is the wrong one.** Decompiled (VA
`0x1804AED80`) it calls `EnemyRenderer.SetInvulnerable`, which visibly tints every enemy, and
`QueueClearAllDebuffs`, which would **strip poison and burn stacks off the entire field every time a
reward window opened**. It also would not have worked: the flag it sets is read by
`Enemy.CanTakeDamage`, and an xref gives that exactly one caller — `KillEnemyTrigger.OnTriggerEnter`
— so it never touches weapon damage.

**Gated on shared experience.** Only that mode pauses every peer together. With it off just the
acting peer pauses, and damage is host-authoritative, so a host paused in its own chest window would
freeze damage for a teammate still fighting — a worse bug than this one.

**Stateless, which is the safety argument.** Nothing is written to an enemy and nothing has to be
cleared, so a missed unpause, a dropped peer or an exception cannot strand the field invulnerable.

**Confirms as** a boss's health bar holding still for a whole reward window on both peers.
**Watch for the opposite failure:** damage that never resumes after the window closes.

**The un-gated component is still unidentified**, and this fix does not find it. That remains open —
`ProjectileInterpolator.Update` is the standing suspect — but it no longer costs health while a
window is up.

### Superseded — the plan that produced the measurement above

1. Log `MyTime.paused` and `Physics.simulationMode` on both peers each second while an encounter
   window is open. **If either flips back before the barrier releases, candidate 1 is the answer**
   and the fix is to make the shared-experience pause own its state rather than share one global
   flag with five other screens.
2. If both stay paused for the whole window, the damage is being applied by something that never
   consulted the flag — start at `ProjectileInterpolator` and the `HitEnemy` path.

Note the reporter's condition is itself strong evidence for candidate 1: the difference between
"shared experience on" and "off" is not *what* pauses, it is *how long the pause is held*.

**Do not "fix" this by pausing every peer.** Shared experience exists so that one player choosing a
reward does not freeze the others; a global pause would undo the feature rather than fix the bug.

**Related.** [OB-4](#ob-4) and [SE-5](07-shared-experience-audit.md#se-5) concern the same window
being closed at the wrong moment, and [OB-3](#ob-3) the barrier re-arming — all three are about the
encounter window's *lifetime*. This one is about what the world does while it is open, which is a
different axis and has not been examined before.

---

<a name="ob-14"></a>
## OB-14 — The end-of-run damage panel credits you with your teammate's weapons — LIKELY

**Reported 2026-08-17, with screenshots.** The DAMAGE column on the summary screen lists ~23 damage
sources for a player whose inventory held **4 weapons and 4 tomes**. The extras are the other
player's weapons, and they appear at **LVL 0** — a level this peer never gave them, because they
were never this peer's weapons.

**LVL 0 is the tell, and it says this is attribution rather than possession.** Nothing was granted to
the wrong player: the summary's own inventory panel is correct. What is wrong is whose ledger the
damage was written into.

**Mechanism, LIKELY.** [`Patches/Projectiles/ProjectileBase.cs`](../../src/plugin/Patches/Projectiles/ProjectileBase.cs)
attributes each hit on the host:

```csharp
var owner = playerManagerService.GetNetPlayerByWeapon(__instance.weaponBase);
if (owner != null)
{
    trackerService.SetCurrentPlayerId(owner.ConnectionId);
    __state = true;
}
```

When the lookup returns **null the hit is left attributed to whoever the tracker already had — the
host**. So every client projectile whose `weaponBase` fails to match is silently recorded against the
host's own run stats. That is exactly the reported shape, and it explains why the host is the peer
who sees it.

**The lookup is already known to be unreliable, in this same file.** `RocketPatches` records that
keying on `GetNetPlayerByWeapon(__instance.weaponBase)` "was a no-op on the exact case it was written
for", because the client's spawn path sets `weaponBase` on the `ProjectileRocket` **parent** and never
on the `Rocket` child. Whatever share of weapons has the same parent/child mismatch would fail here
identically.

```csharp
// PlayerManagerService — the lookup itself
return spawnedPlayers.Values
    .FirstOrDefault(np => np.Inventory.weaponInventory.weapons.ContainsValue(weapon));
```

**Two secondary defects visible in that one line.** `np.Inventory` is dereferenced unguarded, and
`NetPlayer` receives its `Inventory` in a later lifecycle step than its spawn — the same window that
`IsRemoteItem` was hardened against in [OB-2](#ob-2). A null there throws inside a Harmony prefix.
And the enumeration allocates a closure per hit, on the host's damage path.

**Why the existing diagnostic did not catch it.** `TrackerAttributionDiagnostics` counts set/unset
*pairing* anomalies — `UNBALANCED-UNSET`, `overwrite-while-set` — and the 2026-08-17 host log reports
**none**. A lookup that simply returns null sets nothing, so it is not an anomaly by that
definition and is invisible. **The diagnostic measures the wrong half of the problem**, which is the
same shape as the trap this branch already paid for once: a diagnostic whose silence was read as
health.

**The decisive next step.** Count the null lookups. One counter in the `owner == null` branch of
`HitEnemy_Prefix`, reported alongside the existing anomalies, separates "the lookup never matches"
from "it matches for most weapons and fails for a class of them" — and those want different fixes.
If it is the parent/child mismatch, the fix is the one `RocketPatches` already uses: key on the
`ownerId` written into `DynamicData` at spawn, which is present on a received projectile and absent
on one this peer fired itself.

**Not established:** whether anything beyond the scoreboard reads this attribution. The patch's own
comment says it drives "money flying, item procs, kills", so **gold and kill credit may be
misassigned too** rather than only the end screen. Worth checking before this is filed as cosmetic.

---

<a name="ob-15"></a>
## OB-15 — The client's NullReferenceException storm — cause found, **FIXED, unverified**

**The hypothesis carried since 2026-08-15 is refuted.** The client's exception storm was recorded as
"consistent with the custom-button null-background bug but not proven". The button bug is fixed and
confirmed — and the 2026-08-17 run, the first with the client's own log in hand, gives:

| Peer | NREs |
|---|---|
| Host | **0** |
| Client | **7,927** |

The host is clean, so the button fix works. The client is not merely still throwing, it is throwing
**more** than the 1,716 that were attributed to the button bug. Whatever this is, it was never the
buttons.

**The one lead the capture provides.** The first burst begins immediately after an encounter window
closes:

```
[pause] encounterWindow=True  MyTime.paused=True  simulationMode=Script     t=353.28
[barrier] Applied release for round 32 (session 54272875).
[pause] encounterWindow=False MyTime.paused=False simulationMode=FixedUpdate t=355.47
[Error :Unity] NullReferenceException: Object reference not set to an instance of an object.   (x4)
```

That is a correlation across one burst, not a proven cause — but the encounter-window close path is
where to look first, and it is a path this project already knows is fragile
([OB-4](#ob-4), [SE-5](07-shared-experience-audit.md#se-5)).

**Still no managed frame.** `LogOutput.log` discards traces by design, and the previous session
established that `SetStackTraceLogType(ScriptOnly)` on both peers still produced no managed frame —
which points at native game code rather than managed mod code. That remains the obstacle: **the
count is now measured on both ends, and the thrower is still unidentified.**

### 2026-08-17 — the lead is weak, and the real blocker is which file gets copied

**The encounter-window correlation was tested against the capture and does not hold up.** Only
**28.4%** of the 7,927 exceptions fall within 40 lines of a window close, and there are **433 bursts
against 221 closes** — far more bursts than there are windows. The rate is event-driven and
load-correlated rather than tied to one path: 127 of 235 ten-second windows carry exceptions, median
35, peak 392. The storm begins mid-run and ends only when the run does.

**The blocker is procedural, not technical.** `[unity-exc] Script stack traces enabled` is present in
the capture, so the traces were switched on. But the file supplied as the client's log is BepInEx's
`LogOutput.log`, which **keeps the exception message and discards the stack by design**. The traces
exist; they are in Unity's player log, which was not copied.

**This is the second run lost to it.** Run 1 could not attribute 1,716 exceptions for exactly this
reason, and this document said so at the time. Run 2 could not attribute 7,927.

**So the mod now says where to look, twice.** `UnityExceptionDiagnostics` prints the *resolved*
player-log path — from `Application.persistentDataPath`, not as prose — at startup, and again when a
session ends, because that is the moment somebody is about to copy their logs and the startup line is
thousands of lines behind them.

**What this bug needs is one file**: the client's `Player.log` from a run with an exception storm.
No further code is useful until then.

### SOLVED — the file arrived and named the thrower

`client_Player.log`, 2026-08-17. **All 13,878 exceptions have one stack, and it is the same one every
time:**

```
NullReferenceException: Object reference not set to an instance of an object.
  at InteractableShrineChallenge.EnemyDied (Assets.Scripts.Actors.Enemies.Enemy enemy)
  at Assets.Scripts.Actors.Enemies.Enemy.ReleaseToPool ()
```

**That explains every observation at once.** It fires on *enemy release to pool*, so the rate tracks
enemy deaths — which is why the reporter saw it during "Swarm Incoming", why it is load-correlated,
why it peaked at 392 in a ten-second window, and why it stopped only when the run ended. It has
nothing to do with buttons or with encounter windows; both earlier hypotheses are dead, and the
28.4% window correlation was coincidence from enemies dying near windows.

**It is the same shape as [OB-2](#ob-2).** A global event — `Enemy.ReleaseToPool` — invokes an
*instance* handler on an `InteractableShrineChallenge` whose state this peer never initialised,
because challenges are host-authoritative and the client's copy never started one. Decompiled
(`InteractableShrineChallenge$$EnemyDied`, VA `0x1804CDFE0`), the body walks
`this.enemies` (a `HashSet<Enemy>` at `0x78`) and then dereferences `EffectManager.Instance` and
`enemy` on the completion path — any of which is null on a peer that is not running that challenge.

### Fixed — and the mod was closing the chain itself

`Patches/Interactables/InteractableShrineChallenge.cs` suppresses `EnemyDied` on a client, in the
same shape as `ItemGhostPatches` after OB-2.

**The last link found while writing it.** `EnemyDied` returns early unless `done` is set — and
`OnReceivedInteractableUsed` sets exactly that flag on a client as its cosmetic shortcut:

```csharp
if (isHost) shrineChallenge.Interact();   // host runs the real challenge
else { shrineChallenge.done = true; shrineChallenge.fx.SetActive(true); ... }
```

So the client never populates `enemies`, and the shortcut removes the guard that would have made
`EnemyDied` a no-op. **The mod turned a harmless handler into a null walk on every enemy death.**

**Suppressed rather than null-guarded**, deliberately: a null check would silence the exception and
leave a client-side challenge that can never complete — symptom gone, lie intact. Challenges are
host-authoritative and the completion already reaches the client as replicated spawns plus that
shortcut, so there is nothing for a client to compute here.

**Confirms as:** the client's `Player.log` going from 13,878 exception lines to **none**, with the
challenge shrine still showing completed on that peer. The second half matters as much as the first
— it is what distinguishes a fix from a mute.

**And it is two bugs, not one.** The host's own log from the same run has **63** exceptions, none of
them this one — see [OB-17](#ob-17).

---

<a name="ob-16"></a>
## OB-16 — Desert final-boss pylon charge throws on the host — **self-inflicted by OB-11; FIXED, unverified**

**Reported 2026-08-17** alongside a fresh pylon problem, and the host's `Player.log` caught it:

```
NullReferenceException: Object reference not set to an instance of an object.
  at FinalFightController.OnPylonCharged (BossPylon pylon)
  at BossPylon.Complete ()
  at BossPylon.ChargeUpdate ()
  at BossPylon.Update ()
```

**Once, on the host** — at the moment a pylon finished charging. `FinalFightController.OnPylonCharged`
is at VA `0x180493C90`.

**Why this is very likely the same root as [OB-7](#ob-7).** That entry records that Desert pylons do
not exist for clients because their ids are *derived* rather than replicated.
`FinalFightController.boss` is assigned by the mod only on the receive path
(`if (enemy.IsFinalBoss()) MusicController.Instance.finalFightController.boss = enemy;`), so the two
ends disagree about which pylons and which boss the controller knows. A pylon completing against a
controller whose state was never populated is exactly an NRE at `OnPylonCharged`.

### Decompiled — and the cause is very likely this branch's own OB-11 patch

`FinalFightController.OnPylonCharged` (VA `0x180493C90`) decodes to:

```csharp
activePylons.Remove(pylon);
if (activePylons.Count < 1)
{
    boss.MakeInvulnerable(false);   // ← throws when boss is null
    GiveWeaponBack();
}
```

Ghidra's field names are a slot out again and were checked against `dump.cs`: printed
`.orbFollowing` (`0x40`) is `activePylons` (`0x38`), printed `.pylons` (`0x30`) is `boss` (`0x28`).

**Where `boss` comes from.** `FinalFightController.SpawnBoss` (VA `0x180493EC0`) assigns it from the
**return value** of `EnemyManager.SpawnBoss`, called with flag **`0x24` = `FinalBoss` (32) |
`StageBoss` (4)** at `bossSpawnPoint` raycast to ground.

**And `EnemyManager.SpawnBoss` is exactly what OB-11's patch hooks.** That prefix re-centred `pos` on
a random player for *every* call — including this one. Moving the final boss out of its arena gives
`SpawnEnemy` a position it can fail to place, a failed place returns null, and null propagates
straight into `boss`. The run that produced this exception was the first to carry the OB-11 patch.

**OB-11 was about minibosses roaming to the host and was never about arena bosses.** The patch was
too broad from its first version; it now skips `FinalBoss` and `StageBoss`.

> **The general lesson, and it is not the first time on this branch.** A fix aimed at one spawn path
> was applied at a funnel that carries several. The same mistake in the other direction produced
> [OB-12](#ob-12) — counting added to branches the live configuration never reaches. **Check what
> else goes through a funnel before patching it.**

### It also answers OB-8's open question in passing

[OB-8](#ob-8) has been waiting on one lookup: whether `FinalFightController.SpawnBoss` routes through
the normal spawn path. **It does** — `EnemyManager.SpawnBoss`, which itself calls
`EnemyManager.SpawnEnemy`. That is the branch OB-8 predicted would mean the cause is the client's
`SpawnBoss_Prefix` returning false, and it can be re-read on that basis now rather than left held.

---

<a name="ob-17"></a>
## OB-17 — The host throws in MinimapCamera.UpdateArrows — CONFIRMED, **FIXED, unverified**

**2026-08-17 host `Player.log`: 62 occurrences**, per-frame while they last:

```
  at Assets.Scripts.Camera.MinimapCamera.UpdateArrows ()
  at Assets.Scripts.Camera.MinimapCamera.Update ()
```

**Distinct from [OB-15](#ob-15)**, which is the client's and has a different stack entirely. Filed
separately so the two are not merged again — the previous merge of "the client's storm" with "the
host's 7" cost two sessions.

**The mod already touches this.** `PlayerManagerService` carries a comment about *"the ordering
mistake StartGame already made once with the minimap arrow"*, so the minimap arrow is known
mod-adjacent territory rather than untouched game code. `MyPlayer` has both `minimapCamera` and
`minimapCameraScript` fields, and a `NetPlayer` avatar built without one would leave
`UpdateArrows` iterating something with a hole in it.

### Fixed

Decompiled (`MinimapCamera$$UpdateArrows`, VA `0x1804B2D60`): it enumerates `arrowDict.Values` and
reads **`MyPlayer.Instance.transform.position` per entry, with no null check**. Called from
`Update`, so a null local player throws every frame until one exists — a teardown/respawn window.

A prefix now skips the update while `MyPlayer.Instance` is null.

**This is a null guard, which [OB-15](#ob-15) refused — the difference matters.** There, a guard
would have hidden a client-side challenge that could never complete: a lie worth not telling. Here
there is no lie. Placing an arrow relative to a player that does not exist is not a meaningful
operation, the arrows are re-placed on the next frame that has one, and nothing is skipped. **Guard
the null when the operation is genuinely undefined; suppress the caller when the state is missing
because this peer was never supposed to compute it.**

**Not established:** whether the mod widens the window by holding arrows for remote players across a
transition where singleplayer would have had an empty dictionary. The guard is correct either way.

**Confirms as** the host's `Player.log` losing its `UpdateArrows` lines, with arrows still drawn.

---

## Not in this file

- Client `NullReferenceException` storm — live lead in
  [`12-session-handover.md`](12-session-handover.md). **2026-08-07 narrowed it decisively:** with
  `WriteUnityLog = true` on *both* peers for the first time, the host recorded **0** NREs during the
  whole run against the client's **8,422**, while the host's Unity channel was demonstrably live
  (29 `Can not play a disabled audio source` warnings spread across the session). The asymmetry is
  now measured rather than assumed. `[unity-exc]` confirms `SetStackTraceLogType(..., ScriptOnly)`
  was applied on both peers and **still no managed frame appears**, which resolves the open
  either/or: the thrower is native game code, not managed mod code.
- Remote projectile / rocket representation — same file, needs a design pass rather than a patch.
- ~~Host BepInEx logging no Unity-sourced lines~~ — **fixed and confirmed.** It was
  `[Logging.Disk] WriteUnityLog`, which defaults to `false` and is a separate gate from
  `[Logging] UnityLogListening`. See [`05-local-testing.md`](05-local-testing.md).

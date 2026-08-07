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
build with `WriteUnityLog = true` on both peers.

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

**Fix shape, not yet chosen.** Either replicate the count (a new append-only union tag carrying
`ownerId` + `eWeapon` + `currentAmount`, at a low rate — it changes rarely), or derive it locally on
each peer from the already-replicated weapon level, which costs no wire at all but needs
`AegisAttack`'s amount formula decompiled first. **Prefer the second if the formula is pure**; it
avoids a wire change entirely.

**Not known:** whether the wrong count is purely cosmetic or whether the remote `AegisAttack` also
blocks or deals damage on the observing peer. `FixedUpdate` is suppressed, which suggests cosmetic,
but the collision path was not traced.

---

<a name="ob-2"></a>
## OB-2 — Ghost item's summons appear for players who do not have the item — UNVERIFIED

**Reported:** when either peer holds the Ghost item — which summons ghosts on interacting with pots,
shrines and similar — the other player sees the ghosts too, without owning the item. Whether the
non-owner also *receives the effect* (damage dealt by those ghosts, or credit for it) is unknown to
the reporter.

**Do not assume this is a bug.** `ItemGhost` summons entities that carry `GetDamage()` and a
`damageSource`, so they are gameplay actors rather than decoration. Enemies in this project are
host-authoritative and replicated, so if `SpawnGhost` routes through the normal enemy spawn path
then **both players seeing them is correct** — one shared world, one set of ghosts. The bug would
instead be if each peer spawns its own set locally, or if a client's summon is suppressed and
silently lost.

**The one lookup that decides it:**

```bash
"$APPDATA/ghidra/ghidra_12.1.2_PUBLIC/venv/Scripts/python.exe" scripts/re/decompile_headless.py 0x180457EE0 SpawnGhost
```

`ItemGhost.SpawnGhost` is at VA `0x180457EE0`; `OnInteracted` at `0x180457DA0`. If `SpawnGhost` calls
`EnemyManager.SpawnEnemy`, our `EnemyManagerPatches.SpawnEnemy_Prefix` already gates it on host
authority and the behaviour is correct-by-construction. If it instantiates directly, it bypasses
that gate on both peers and this is a real divergence.

**Separately, and regardless of the above:** the existing patch
[`Patches/Items/ItemGhost.cs`](../../src/plugin/Patches/Items/ItemGhost.cs) has **no
`HasNetplaySessionStarted()` guard and no ownership check**, contrary to the standing rule in
`CLAUDE.md`. It only suppresses the original when `success` is false, so it is probably harmless
today — but it changes singleplayer, which that rule exists to prevent.

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
## OB-6 — Shrine charge counters differ between players — CONFIRMED, cause exact

**Reported:** shrine counters are not in sync between all players.

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

| Object | Host | Client |
|---|---|---|
| Tumbleweed (`SynchronizationService.cs:4621` / `:4639`) | `AddSpawnedObject` — counter **+1** | `SetSpawnedObject(hostId, …)` — counter **unchanged** |
| Desert grave / reviver (`:4265`) | `if (isHost) AddSpawnedObject` — counter **+1** | uses replicated `reviverId` — counter **unchanged** |
| Pylons (`FinalFightController.cs:48`) | `AddSpawnedObject` | `AddSpawnedObject` — **both advance, from different starting points** |

So by the time the final fight begins the host's counter has been advanced once per tumbleweed and
once per desert grave, and the client's has not. Both peers then allocate pylon ids from counters
that are far apart, and the client's lookup of a host-assigned pylon id finds nothing.

**Why Forest works and Desert does not.** Tumbleweeds are Desert-only — `NetworkHandler.Update`
gates the tumbleweed tick on `MapController.runConfig.mapData.eMap == EMap.Desert`, and desert graves
are likewise Desert-only. On Forest neither exists, the counters stay in step, and the accidental
id agreement holds. **The Forest case is not evidence that the design works; it is evidence that it
only works when nothing else has allocated an id.**

**Fix shape.** Replicate pylon ids instead of deriving them. The host should allocate and broadcast
(the `SpawnedObject` path already exists), and the client should `SetSpawnedObject` with the host's
id like every other replicated object. The seed can stay — it keeps *positions* consistent — but it
must not be load-bearing for identity.

**A shared mutable counter used as an implicit protocol is the general defect here**, and pylons are
just where it surfaced. Any future object registered with `AddSpawnedObject` on both peers has the
same latent bug.

---

<a name="ob-8"></a>
## OB-8 — The Desert final boss does not move for clients — LIKELY, one decompile settles it

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

**The one lookup that decides it.** Decompile `FinalFightController.SpawnBoss` and check whether it
calls `EnemyManager.SpawnEnemy` or instantiates a prefab directly:

```bash
"$APPDATA/ghidra/ghidra_12.1.2_PUBLIC/venv/Scripts/python.exe" scripts/re/decompile_headless.py FinalFightController
```

Resolve the VA from `megabonk-re/build-21750826/dump.cs` — **use the `VA:` field, not `RVA:`**.

**Do not fix before that lookup.** If the boss does route through `SpawnEnemy`, the cause is
something else entirely and the obvious "replicate the boss" patch would be a second, redundant
spawn path.

**Also unexplained, and possibly the same root:** Forest works. If OB-7's counter divergence is
Desert-specific, and the boss turns out to be resolved through a spawned-object id rather than an
enemy id, then OB-7 and OB-8 are one bug with two symptoms. Worth checking before treating them
separately.

---

<a name="ob-9"></a>
## OB-9 — Enemy max health is never replicated — CONFIRMED, cause exact

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

So current HP converges — it is re-sent at 40 Hz and the refresh sweep bounds its staleness — while
max HP is **derived locally on each peer** from whatever the game computes at spawn. Any input to
that computation which differs between peers produces a permanently different maximum, and the
health bar reads as a different fraction on each screen even though the absolute values agree.

**Why summoned bosses specifically.** `SpawnedEnemy` does replicate *some* scaling inputs — `Wave`,
`CanBeElite`, `ExtraSizeMultiplier`. A regular wave enemy therefore tends to agree by construction.
A boss summoned through `ChallengeSummoner` is scaled by state that is **not** in that message, so
its locally-derived maximum diverges. That matches the report exactly: ordinary enemies look fine,
summoned bosses do not.

**Fix shape, cheapest first.** Add a max-health field to `SpawnedEnemy` — **as a new append-only
union tag, never as a field on tag 4**, because MemoryPack is positional and widening a shipped
message silently corrupts sessions between builds. Max health changes rarely, so it belongs on the
spawn message and not in the 40 Hz delta; putting it in `EnemyModel` would cost bandwidth on every
enemy every tick to fix a value that almost never changes.

**Not established:** whether the divergence is purely cosmetic (a health bar reading wrong) or
whether anything gameplay-relevant reads max health — execute thresholds, percentage damage, or
phase transitions would all make this a correctness bug rather than a display one. Worth checking
before choosing how much to spend on it.

---

<a name="ob-10"></a>
## OB-10 — Clients emit ~1,800 "Only host can perform this action" warnings per run — CONFIRMED

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

**Do not silence it.** A guard firing 1,841 times is information; the fix is upstream of the guard.


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

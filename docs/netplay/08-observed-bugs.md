# Observed bugs — backlog

Bugs seen in play that are **not yet fixed** and are not tracked elsewhere. Distinct from
[`01-critical-fixes.md`](01-critical-fixes.md) (defects found by source analysis, most now fixed)
and [`07-shared-experience-audit.md`](07-shared-experience-audit.md) (the encounter barrier's own
audit).

Status tags per [`../README.md`](../README.md): **CONFIRMED** — verified by reading the code at the
cited line. **LIKELY** — strong inference from structure, failing path not observed. **UNVERIFIED**
— depends on game internals not yet decompiled.

OB-1..OB-4 come from the 2026-08-06 session (two players, direct P2P). OB-5 and OB-6 were reported
from the **2026-08-07** session — the first run of the round-identity build, two players over the
internet at ~61 ms rtt, with `WriteUnityLog = true` on the host for the first time.

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
## OB-4 — The failsafe closes an encounter window that is mid-use — CONFIRMED, **FIXED (UNVERIFIED in play)**

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

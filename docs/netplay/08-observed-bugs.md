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
## OB-1 — Remote players' attacks render with the *observer's* stats — **FIXED and CONFIRMED in play (2026-10-12)**

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

### Scope widened 2026-08-18 — it is not only the shield count

**Reported: aura *size* is wrong too, and anything tied to "projectile count" or "size".** That is
the same defect and the decompile explains why — every `WeaponUtility` stat accessor has the same
shape:

```
GetAttackQuantity(weaponBase)       VA 0x1804346F0
  = weaponStats[EStat.Projectiles(16)]  + PlayerStats.GetStat(16)

GetAttackSizeMultiplier(weaponBase) VA 0x1804347A0
  = weaponStats[EStat.SizeMultiplier(9)] + PlayerStats.GetStat(9)
```

**`PlayerStats.GetStat` is static and takes no player.** It reads whoever's machine is asking. So
any value of the form *per-weapon stat + global stat* is computed with the **observer's** global
stat when rendering somebody else's attack.

**The general statement, which is what should have been written the first time:**

> A remote player's attack is drawn using their weapon and **your** player stats. Every visual
> derived from a `PlayerStats` global is wrong by the difference between your stat and theirs.

That covers the shield count, aura size, projectile count and projectile size at once, and predicts
the rest: anything reading `SizeMultiplier`, `Projectiles`, `DurationMultiplier`,
`ProjectileSpeedMultiplier` or `AttackSpeed` on a remote attack has it.

**It also explains why it looks fine at first.** Both players start with identical globals, so the
numbers agree until somebody takes an upgrade — which is exactly the trap noted below about a
playtest plausibly passing this.

### What the fix has to cover, now that the scope is known

The hardcoded `currentAmount = 2` is one symptom, not the bug. Replicating a shield count alone
would fix the countable case and leave aura size wrong.

**What has to be replicated is the owner's relevant `PlayerStats` globals**, so a remote attack can
be built from *their* numbers. A per-visual message per stat does not scale; a small block of the
owner's globals, sent when they change, does — they change only on level-up and item pickup, which
is rare.

**Still unchosen, and now the choice is different from what this entry originally proposed.** The
"new union tag carrying `ownerId` + `eWeapon` + `currentAmount`" below is the narrow version and is
no longer sufficient on its own.

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

**So OB-1 needs replication.** ~~A new append-only union tag carrying `ownerId` + `eWeapon` +
`currentAmount`~~ — see the scope note above: that is the narrow fix and covers only the shield
count. Replicating the owner's stat block is the one that also fixes aura and projectile size.

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

> **2026-10-18 evening: aura size confirmed in play** by the project owner after `4148e19` (the
> remote aura's size is now the owner's global × weapon product).

<a name="ob-2"></a>
### 2026-10-07 — checked against a third-party mod's 0.5 release

Its answer is to force `GetAttackSizeMultiplier` to `1.0` for any weapon a remote player owns — every
remote aura renders at base size regardless of upgrades. Ours replicates the owner's stat block and
applies the exact correction, which is strictly more accurate. Nothing to take.

### 2026-10-08 — it has never sent anything over Steam

`PlayerStatsSyncService` sent through `IUdpClientService`, which is always the LiteNetLib session.
Only `INetTransport` is switched to Steam by `UseSteamTransport`, so under the default transport every
stat block went into a LiteNetLib socket with no peers. Remote attacks have been corrected from no data
at all, which is consistent with OB-1 never having been confirmed in play. Found because the desync
detector copied the same pattern and logged nothing. Both now send through `INetTransport`.
**The OB-1 feature is effectively untested from here.**

### 2026-10-11 — size works; Aegis count fixed

Confirmed in play for Aura and Frost Walker size. The Aegis showed one shield on the host while the
owner had more: our prefix blocks a remote Aegis's `FixedUpdate`, the only caller of `AmplifyShield`,
so the count never grew. Now set directly to `GetAttackQuantity` (already owner-corrected). Shows the
owner's maximum; spent shields are not replicated.

### 2026-10-12 — Chunkers confirmed

Chunkers size and count matched on both screens after upgrades, confirming the stat-forwarding
fix (`NetPlayer.OnRemoteStatsChanged`).

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
## OB-3 — "Waiting for other player(s) choices…" reappears every ~20 s — **CLOSED 2026-10-21: has not reappeared**

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

**Closed 2026-10-21** at the host player's call. It has not reappeared since 2026-08-18, through every playtest since, including the long multi-stage Endless runs of 2026-10-21; those logs have no "Waiting for other player(s) choices" line. No root cause was ever named, so reopen it if the message comes back on a timer.

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
## OB-5 — A client's magnet pull often does nothing — **CLOSED: working in play (2026-10-12)**

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

### 2026-08-18 — the symptom is gone, and the residual is now classified rather than counted

**The reporter says the magnet pull works**, with occasional drops still logged. So this stopped
being "a client's pull often does nothing" and became "some instructions still name ids this peer
cannot resolve". Those are different bugs and the second may not be one at all.

`Services/PickupLifetimeDiagnostics` implements this entry's own recommended next step: a set of
every id ever registered this session, consulted in each of the four "not found" branches. Each miss
is now classified and reported on a throttle:

```
[pickup] 12 unresolved pickup instruction(s) in the last ~10s —
  NEVER-REGISTERED 0 [none],
  registered-then-removed 12 [OnReceivedPickupFollowingPlayer x12]. …
```

**Read it as follows, and the two answers want opposite work:**

| Reading | Meaning | Action |
|---|---|---|
| **NEVER-REGISTERED > 0** | the spawn never reached this peer | a real replication gap — chase it |
| **registered-then-removed only** | the pickup was consumed before the instruction landed | **expected**; the fix is to drop the warning to debug, not to change the netcode |

**The second is the likely answer now that the pull works**, and if the capture confirms it this
entry closes as "working as intended, warning too loud" rather than as a defect.

The four sites are reported separately because the original capture's split was itself a clue: 184
in `OnReceivedPickupFollowingPlayer` against 14 in `OnReceivedPickupApplied`.

**Not behind a config flag**, deliberately: two counter increments and a set lookup on a path that
was already building a warning string. Gating it would mean the next capture arrives without it,
which is how this entry stayed open for five sessions.

---

<a name="ob-6"></a>
### 2026-10-07 — a third candidate: pickup merging

`PickupManager.SpawnPickup` (VA `0x1804DED60`, decompiled) calls `CheckOverlap` and, when a pickup
of the same kind is already there, does `Pickup.AddValue` on the **existing** pickup and returns it.
No new pickup exists. Our `SpawnPickup_Postfix` then clears that object's `pickupId` and
`OnPickupSpawned` registers it again under a new id — **two ids, one host object**. The client
replays the spawn through its own `SpawnPickup` and merges too, but only if its overlap check agrees
with the host's.

Where it does not, consuming the object under one id leaves the other dangling, and a pull on it does
nothing. That would read to the existing diagnostic as **seen-then-removed** — the benign race whose
recommended fix is to ignore it — so it would hide this rather than reveal it.

**Not proven to matter**: the last two runs logged 0 unresolved pickups. Instrumented only:
`OB-5: N pickup merge(s) re-registered under a new id` at Info, every 10 s, silent when nothing merges.

**Separate, certain, and dormant:** with shared experience **off**, `OnPickupSpawned` sets
`pickup.value = GetPickupXpValue()` — on a merge that overwrites the merged total with a single orb's
worth, so merged XP is lost on the host. Unreachable in the shared-experience runs played so far.

**Reference point:** a third-party mod's 0.5 release replicates merged values with a dedicated
message. That is a wire change (a new union tag); not taken until the counter says merges are common.

### 2026-10-08 — merges are common and harmless

158 host-side merges in one run, and **zero** failed pickup lookups on either peer. The client merges
the same way the host does, so the aliased ids never dangle in practice. The merge lead is closed; the
dormant shared-experience-off value bug noted above stands.

### 2026-10-12 — closed

Working in play with no reported issues, and zero failed pickup lookups across the recent runs.
Closed by the project owner. The merge counter can stay; it is silent when nothing merges.

## OB-6 — A shrine's charge *value* is never transmitted — **REOPENED, FIXED and CONFIRMED in play (2026-10-18)**

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

> **Closed as won't-fix, 2026-08-18, by the project owner.** The defect below is real and the
> analysis stands — the charge value genuinely never crosses the wire. It is closed because **it has
> never been observed in play**: it was found by reading the protocol, not by anyone noticing a
> shrine behave oddly, across every session this project has run. The cost of fixing it is a new
> union tag or a completion-event replication; the observed benefit is zero.
>
> **Reopen it if this is ever seen:** a charge shrine completing and paying out for one player
> noticeably before the other's bar fills. That is the symptom the analysis predicts, and it is the
> only thing that would make this worth the wire change.
>
> The `ChargeShrine` rate lookup below is therefore **not** outstanding work. Do not spend a
> decompile on it unless the symptom above shows up.

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
### 2026-10-12 — reopened: the reopen condition was met

The closure above named one condition for reopening: *a charge shrine completing and paying out for
one player noticeably before the other's bar fills.* The 2026-10-11 run met it. The player reported
repeated pauses tied to charge shrines, and the barrier log shows the client's reward window opening
8-15 s before the host's in rounds 105-107; round 108 passed 20 s and fired the client's failsafe,
which is the "stuck" game the player reported. The desync detector also showed the `Charge Shrines`
row 1 apart for most of the run, i.e. one peer completed a shrine the other never did. The earlier
scope note that this row "does agree" is no longer true.

Decompiled `ChargeShrine.Update` (VA `0x1804C2E20`): each peer fills the bar on its own clock and
calls `Complete` when its own bar is full, then opens the reward window with `AddEncounter(1)`.

**Fix:** completion is now the host's decision. A client's `Complete` is held (its bar sits at full);
the host's `Complete` postfix sends `ChargeShrineCompleted` (union tag **79**), and the client
completes the same shrine on receipt. The counter follows, since `A_Charged` is raised inside
`Complete`. Trade-off: a shrine a client fills and the host does not stays unrewarded on the client.

## OB-7 — Desert final-boss pylons do not exist for clients — **FIXED (fixed ids) and CONFIRMED in play (2026-10-19)**

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

### 2026-10-18 — reopened: it was never fixed, the 2026-08-18 run was luck

The client could not see the pylons at all in the final fight. Its log has the same signature as
2026-08-07: `Pylon object not found in SpawnedObjectManagerService when processing
OnReceivedStartingToChargingPylon` and the `…Stopping…` equivalent, every time the host charged one
(host: `stopping charging pylon 23 / 23 / 16`).

**No code had ever changed.** Handover 15 already said so: "no `fix(...)` commit has ever touched
pylons". `FinalFightController.StartPylons` still called `AddSpawnedObject` on both peers. The counter
is reset by `ResetForNextLevel`, so on 2026-08-18 the host had happened to spawn nothing counted on the
final stage before the pylons, and the ids agreed by coincidence.

**Fixed as the fix shape above intended, without a protocol change.** Pylons now take
`PYLON_ID_BASE (0x70000000) + index` and are registered with `SetSpawnedObject` on every peer.
`pylons` is a serialized `BossPylon[]` on the controller (`dump.cs` offset `0x30`), so both peers walk
the same array in the same order; the counter never comes near the base within a stage. Logs
`OB-7: registered N pylon(s) with fixed ids 1879048192+.` on both peers.

**Same latent shape, not fixed here:** `GraveyardBossRoom.Activate` numbers the lamps with
`AddSpawnedObject` on the host and `SetSpawnedObjectAtLast` (max key + 1) on the client, which are two
different counters again.

**Confirms fixed as:** the client sees and can charge the pylons, and neither log has a `Pylon object
not found` line.

---

<a name="ob-8"></a>
## OB-8 — The Desert final boss does not move for clients, and its HP differs — **FIXED (ready-barrier spawn) and CONFIRMED in play (2026-10-19)**

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

### 2026-10-19 — reopened: never fixed in code, and the HP differs too


**Reported 2026-10-19, first full run with the test bot (bot as client).** At the Desert final boss:
host bar **6.8M / 6.8M**, client bar **5.5M / 5.5M**; neither moved, and on the client the boss did not
move at all and was not where the host saw it.

**It was never fixed.** Its entry ends "no fix has been written"; it was marked confirmed after the
2026-08-18 run, the same luck that hid OB-7. What OB-8 did establish stands: the host's
`FinalFightController.SpawnBoss` (VA `0x180493EC0`) goes through `EnemyManager.SpawnBoss` with flags
`0x24` (FinalBoss | StageBoss), so the boss should reach the client as an ordinary replicated enemy.
Decompiled again 2026-10-19: `SpawnBoss` changes no HP after the spawn (it stores the boss and sets
the controller's timers; Ghidra labels the boss slot `pylons`, one slot off).

**What the logs could not say.** Neither peer logged the final boss's spawn message, the HP it
carried, any `EnemyMaxHpScaled` (OB-9), or its position. 6.8M / 5.5M = 1.24 matches neither the team
Difficulty in play (0.29 average) nor the mod's 1.1 boss HP multiplier on its own.

**Diagnostic added** (`Services/BossDiagnostics.cs`), always on, bosses only:

- `[boss] host sent spawn (flag …): id N … hp …` and `[boss] client applied spawn …`
- `[boss] host sent max-HP rescale …` / `client applied max-HP rescale …` (or `… before its spawn; held.`)
- every 10 s on both peers: `[boss] host|client sees id N … hp X / Y at (x, y, z) flag …`

**Next run:** compare the two logs' `[boss]` lines for the final boss's id. A different HP in
"client applied spawn" than "host sent spawn" points at the message; equal spawn HP but a later host
change with no rescale line points at an HP change OB-9 does not cover; a client position that never
changes while the host's does points at the boss missing from the 40 Hz enemy stream.


---

### 2026-10-19 — root cause: the arena cinematic spawns the boss during the ready barrier

Decompiled, four ways, and they agree:

1. **No spawn was ever logged.** `EnemyManager.SpawnBoss_Prefix` logs `OB-11: leaving an arena boss
   ... (flag N)` for every arena boss once the session has started. The host log has flag 4 (stage
   bosses) and flag 2 (the time's-up `GhostInvincible`, enemy 0x30, from `GameManager.Update`), and
   **never flag 36** (FinalBoss 32 + StageBoss 4), although a final boss existed on both peers. The
   prefix is silent in exactly one case: `HasNetplaySessionStarted()` false.
2. **The cinematic starts itself on arena load.** `FianlBossCinematic.Start` (VA `0x180491D60`)
   calls `StartCoroutine(AnimateCinematic)` at once; the coroutine (VA `0x1804A0230`) is the only
   caller of `FinalFightController.SpawnBoss` (xref), a few seconds in.
3. **That moment is the ready barrier.** Both logs show the final arena's "Lobby not ready yet,
   waiting..." for 10+ s before "Lobby is ready, starting the game". `MyTime.Pause` freezes physics,
   not coroutines.
4. **Every guard on the path opened with `if (!HasNetplaySessionStarted()) return true;`** —
   `FinalFightController.SpawnBoss_Prefix` and `EnemyManager.SpawnBoss_Prefix`. Before Started, both
   peers ran vanilla: the host spawned an unreplicated boss, and the client spawned **its own**, with
   its own HP and no host updates — 6.8M / 5.5M, frozen on the client.

The HP path itself is sound: `EnemyManager.SpawnBoss` (VA `0x18041A200`) calls `SpawnEnemy` (the
overload the mod hooks) and then `SetSwarmMultiplierHp` only when `GameManager+0x60` boss curses are
above 0, which OB-9 already replicates; `FinalFightController.SpawnBoss` itself changes no HP.

**Fix.** `FinalFightController.SpawnBoss_Prefix` gates on `HasNetplaySessionInitialized`: a client in
a session never spawns the final boss; the host, if the barrier is still up, holds the spawn
(`[boss] final boss spawn held until the session starts`) and runs it once the session starts
(`[boss] session started; spawning the held final boss.`), so it goes through the replicated path.
`EnemyManager.SpawnBoss_Prefix` likewise stops a client spawning any local boss during the barrier,
and logs any host `SpawnBoss` that still happens before Started.

**UNVERIFIED:** that the rest of the cinematic and `FinalFightController.FixedUpdate` tolerate the boss
arriving a few seconds later on the host; that a `SpawnedEnemy` reaching a client still finishing
its barrier is applied. **Confirms fixed as:** the host logs the held/spawned lines and
`OB-11: ... (flag 36)`, both peers' `[boss]` lines show the same id and HP, and the boss moves on the
client.

---

### 2026-10-19 — follow-up: a client still in its barrier dropped the held boss

Three-player run (host plus two test bots): the held spawn worked on the host (`final boss spawn held`
→ `spawning the held final boss` → `flag 36`), one bot got the boss, the other did not: its digest read
`enemies 1/0`, the boss's special attacks arrived "not found", and with no boss its fight never
started its pylons (9 × `Pylon object not found`). **Cause:** `OnReceivedSpawnedEnemy` dropped every
spawn unless this client was `Started`, and a client starts only when the host's start message
reaches it, after the host has already spawned the boss. **Fixed:** spawns are also queued while
`Ready` (the barrier, new stage loaded); the queue still only builds enemies once `Started`, and
`LoadingNextLevel` still drops, so nothing from the old stage leaks in.

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
## OB-10 — Clients emit host-only-send warnings by the thousand — **FIXED and CONFIRMED (2026-10-19): session total 0 on host and both clients**

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

### 2026-10-18 — not reproducing: zero rejections in three runs, cause never identified

**Every surviving log is clean.** Seven `LogOutput.log` files from the 2026-10-08, 10-11 and 10-18
runs, three of them from the client (`Netplay loops started as CLIENT`), all MegabonkTogether 5.1.0
built after the diagnostic landed (`d586b18`, 2026-08-19): **zero** `[host-guard]` lines. At the
2026-08-17 rate (20,078 a run, continuous) the 10 s report would have printed many times, so the
flood itself is gone.

**What it was will not be known.** The 2026-08-17 log is no longer on disk, and its warning carried
no message type anyway — that is why the diagnostic was built. No run between 2026-08-19 and
2026-10-08 was kept. The likeliest cause is one of the fixes in that window that changed when a client
runs host-side logic — the OB-11 / OB-15 transition guard (`5e292e4`), the OB-13 damage gate
(`e4df8af`), the interpolator ordering guards (`41130f7`) — but that is **UNVERIFIED** inference from
timing, not attribution.

**Gap closed in the diagnostic.** `Report` prints only when a rejection arrives after its 10 s
throttle, and the first call only primes it, so a few rejections in a run, or the last batch of any
run, were counted and never printed. "No line" therefore could not prove "nothing rejected".
`HostGuardDiagnostics.Reset` (called from `ResetNetworking` on session teardown) now flushes whatever
is pending and always prints a total:

`[host-guard] session total: 0 host-only send(s) rejected on this peer.`

The total only prints when the session is torn down through the mod (leaving the lobby or returning
to the menu); closing the game mid-run skips it.

### 2026-10-18 evening — the session total paid off: 121, all `LightningStrike`

The first run with the end-of-session total: host `0`, client **`121`**, every one
`steam-net/SendToAllClients/LightningStrike`, spread across the whole run while the client held a
**Lightning Orb**. `WeaponUtilityPatches.LightningStrike_Postfix` had a session check and a
`CAN_SEND_MESSAGES` check but no host check, and `OnLightningStrike` broadcasts. Fixed by returning on
a client. No behaviour change: a client's broadcast was always dropped.

The 20,078 of 2026-08-17 were far more than this, so they were not only this path, but this is the
only one still firing.

**Close as fixed when:** the client's log from a full run shows `session total: 0`. If it shows a
non-zero total, the flushed `[host-guard] ... at session end` line ranks the message types — fix
those paths upstream of the guard and reopen this entry.


---

<a name="ob-11"></a>
## OB-11 — Minibosses always spawn near the host — **FIXED and CONFIRMED in play (2026-10-08)**

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

### 2026-10-21: second scope correction, bosses that come out of an object

**Reported in play.** Bosses from a Graveyard coffin, a Desert grave and a bush or bandit
challenge appeared next to a random player, not at their object. For a coffin that breaks the
fight: activating it raises a small barrier round the coffin.

**Cause.** The re-centring applied to every `SpawnBoss` except arena bosses. An xref on
`EnemyManager.SpawnBoss` (`0x18041A200`) gives its callers:

| Caller | Re-centred? |
|---|---|
| The timeline's miniboss (`SummonerController.EventMiniboss`) | **Yes.** The only one placed round the local player; what OB-11 is about. |
| The stage boss and final boss | Already exempt, by flag (the first correction). |
| `InteractableCoffin.Interact`, `InteractableDesertGrave.SpawnEnemy`, `InteractableCharacterFight.SpawnEnemy`, `InteractableSkeletonKingStatue.SpawnEnemy`, `GraveyardBossRoom.CheckSpawnBoss` | **No longer.** They spawn at their object on purpose. |
| Our own revive coffin's ghost (`InteractableReviver`) | **No longer.** It was being moved too. |
| `GameManager`'s dungeon overtime spawns | Unchanged. |

**Fixed.** `ObjectBossSpawns` names the object while each of those callers runs, and so does the
revive coffin's existing `CurrentReviver` flag. `SpawnBoss_Prefix` then leaves the position alone
and logs `OB-11: leaving a boss spawned by <object> where it came from`. **UNVERIFIED, not run
in-game.**

<a name="ob-12"></a>
### 2026-09-04 — third report, and why the previous two "confirmations" were worthless

Reported again: minibosses still spawn near the host.

**The 0-failures figure that closed this twice does not mean anything.** OB-11's only log
statements were one `LogWarning` on the exception path — which correctly never fired — and two
`LogDebug` lines, which do not reach `LogOutput.log` at the default level. Grepping all three logs
from the 2026-09-04 run for `OB-11` returns **zero lines on every peer**. There has never been
evidence the re-centring ran, only evidence that it did not throw.

This is the "a diagnostic that cannot run is worse than none" trap, in its most expensive form: it
did not merely fail to help, it produced a number that read as a pass.

**Four things checked before touching the logic, so they are not re-checked:**

| Question | Answer |
|---|---|
| Does the OB-16 arena exclusion swallow minibosses? | No. `SummonerMiniboss` is **16**; the exclusion is `FinalBoss\|StageBoss` = **36**. |
| Do minibosses even reach `EnemyManager.SpawnBoss`? | Yes. `SummonerController.EventMiniboss` (VA `0x180487240`, decompiled) calls it with flag `0x10`, passing the position by pointer — so a `ref` prefix is the correct interception point. |
| Does Harmony's by-name binding resolve? | Yes. The game's signature is `SpawnBoss(EEnemy eEnemy, int summonerId, EEnemyFlag enemyFlag, Vector3 pos, float extraSizeMultiplier)`. |
| Does the placement call match? | Yes. `GetEnemySpawnPositionAroundPoint(Vector3 pos, float minRadius, float maxRadius, int attempts, bool onlyGround, float fromHeight)`. |

`EventMiniboss` also spawns in a loop — `GameManager.Instance.<+0x60> + 1` minibosses per event —
so an event produces a burst, not one.

**Second cause, independent of whether the code works.** The slot was picked with
`Random.Range(0, Count + 1)`. That is fair over many draws and useless as a test: with two players
it is a coin flip, and a coin flip produces runs of three and four consecutive host-side spawns
routinely. A working fix and a dead one look identical from inside the game — which is very likely
what all three reports have actually been describing.

**Both addressed:**

1. Every branch now logs at `Info` — prefix entry with flag and incoming position, arena exclusion,
   no eligible peer, rotation landing on the host's slot, placement failure, and success with the
   peer id and before/after positions.
2. The random draw is now a **rotation** over the same slots, so consecutive minibosses alternate
   and the intended behaviour is checkable by eye in one level without reading a log.

### 2026-10-07 — the instrumentation answered it on the first run

```
OB-11: re-centring a boss spawn (flag 16) from (-127.07, 31.41, 91.09).
OB-11: re-centred a boss spawn on peer 82462037 — (-127.07, 31.41, 91.09)
       -> (369.25, 25.32, -38.03) (20.0u ring, slot 0 of 2).
```

Flag 16 is `SummonerMiniboss`. The prefix fires, the anchor is plausible, the placement succeeds,
and the miniboss moved roughly 500 units onto the **client**. That is the first evidence in three
reports that any of this runs at all — and it vindicates instrumenting before changing more logic,
since the code had been correct for two of those reports.

**Still unverified:** the rotation. The run ended at the [OB-21](#ob-21) freeze after a single
miniboss event, so `slot 0 of 2` was observed once and alternation was not. Two or more events in
one level are needed for that.

**What settled the earlier question:** play one level with a miniboss event and read the `OB-11` lines on the host.
Their absence means the patch never fires and the next step is patch-time resolution, not placement.
A `re-centred a boss spawn on peer N` line whose miniboss still appears next to the host means the
position is being overwritten downstream of the prefix, which nothing has yet looked for.

### 2026-10-08 — rotation confirmed

Three miniboss events in one run alternated exactly as designed: `slot 0 of 2` (client), then
`host's own slot (1 of 2)`, then `slot 0 of 2` again. With the 2026-10-07 placement proof, both halves
of OB-11 are now confirmed in play.

## OB-12 — The run's interactable counters diverge, except charge shrines — **FIXED and CONFIRMED in play (2026-10-12)**

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
### 2026-09-04 — the shady-guy row, solved by decompiling instead of reasoning

**Mechanism, from four bodies:**

| Method | VA | What it does |
|---|---|---|
| `InteractableShadyGuy.Interact` | `0x1804CD400` | Sets a static "current shady guy", `UpdatePrices()`, opens the shop via `EncounterWindows.AddEncounter(7)` — and **returns `false`** |
| `InteractablesStatus.OnInteractableUse` | `0x18051B1D0` | Opens with `if (!success) return;` — so the generic path **never** counts a shady guy |
| `InteractableShadyGuy.OnShadyGuyDone` | `0x1804CD5F0` | Runs when the shop finishes, guarded on the static still being this instance; raises `Action<InteractableShadyGuy>` |
| `InteractablesStatus.OnShadyGuyUsed` | `0x18051B4A0` | Subscribed to that action by `Init`; does `numUsed += 1` |

**Vanilla counts a shady guy exactly once, by a dedicated path, when the shop window closes.** The
`false` return from `Interact()` is deliberate: the interaction is not finished when the window
opens, so the generic counter is told it did not succeed.

**Why the mod doubled it.** The replication dispatch replays `shadyGuy.Interact()` on the receiving
peer, so that peer's window eventually closes and the game counts it. But `CountIfDispatchDidNot`
compares `numUsed` *immediately* — the real count is still pending — sees it unchanged, and adds
one. The window then closes and the game adds another.

Two per replicated shady guy on the receiver, one on the sender. With the client using both, the
host reads **4 of 2** and the client **2 of 2** — the reported figures exactly.

**Shady guy is the one row where measure-then-count cannot work**, because that shape assumes the
game counts synchronously during the dispatch. Every other row does. Excluded from `Take()`, so the
snapshot and the count can never disagree about it, and left to the game's own path.

**Stated trade-off:** the count now depends on the receiving peer's shop window actually completing.
A peer that dismisses it without reaching `OnShadyGuyDone` leaves the row one too *low* rather than
one too high. That is the better failure and it is what vanilla does, but it is not verified.

**Still open on this bug:** the challenge-shrine row, untouched by this — **resolved 2026-10-07, see
below.**

### 2026-10-07 — the challenge-shrine row is an echo

The first run with a BepInEx log from **both** machines, which is what this was blocked on.

One challenge shrine was used, and **both peers ran `OnReceivedInteractableUsed` for it**. The
barrier rounds are the only clock the two peers share, and they put the event in the same slot on
both sides:

| Host | Client |
|---|---|
| `Released round 19` | `Applied release for round 19` |
| **`snapshot 'Challenges' … reads 0`** → `counting 'Challenges'` | **`snapshot 'Challenges' … reads 0`** → `counting 'Challenges'` |
| `Released round 20` | `Applied release for round 20` |

One use, dispatched twice — not two uses. One of those peers is the one that used it, receiving its
own message back.

For a challenge shrine that is a visible divergence rather than a wasted call.
`InteractableShrineChallenge.Interact()` returns `true`, so the using peer's own
`DetectInteractables.A_Interacted` had already counted it; the echo adds a second count and that
peer's row reads **one higher** than everyone else's for the rest of the run.

`OnReceivedInteractableUsed` had **no owner check of any kind**. `OnReceivedChangeGold` already
carries one, added for the stated reason that the host's exclusion runs through
`SendToAllClientsExcept`, *"whose relay branch falls back to an EMPTY filter list on a lookup
miss"* — and this session was relayed. The same guard is now on the interactable dispatch.

Dropping the echo also prevents something worse than a miscount on the branches that replay the
interaction: the `Interact` branch would run the real interaction a second time on the peer that
just performed it — for a challenge shrine, a second wave of challenge enemies.

**Correction (2026-10-08): the cited route was wrong.** That run used **Steam transport**
(`[steam-net]` throughout the host log), and under Steam `SendToAllClientsExcept` never touches
`RelayEnvelope` — the empty-filter fallback quoted above belongs to the UDP relay path. Under Steam,
the sender only gets its own message back if `peerHandlesByConnectionId` has **no entry** for its
`OwnerId`, in which case nobody is excluded. Why that lookup would miss is not established. The guard
is correct whichever way it resolves, and its `OB-12: dropping our own InteractableUsed` line now
settles whether an echo happens at all: it fires on the peer receiving one.

**Not explained:** why only this row showed it. The `Pots` rows differ between the peers (12 against
15), which is consistent with genuinely different uses rather than echoes. Whether the echo is
universal or conditional on the relay filter missing is still open; the guard is correct either way.

**Also confirmed in this run:** the shady-guy fix is live and behaving — `leaving a shady guy to the
game's own OnShadyGuyUsed` appears twice on each peer, and no shady-guy row was double-counted.

### 2026-09-04 — the challenge shrine is **not** the shady-guy shape, and what that turned up

Decompiled on the hypothesis that it resolves through the same deferred pathway. It does not, and
each step is recorded so the hypothesis is not retried.

| Checked | VA | Result |
|---|---|---|
| `InteractableShrineChallenge.Interact` | `0x1804CE250` | **Returns `true`** — spawns the challenge wave, activates `fx`, destroys both icons. The opposite of shady guy. |
| `InteractablesStatus.Init` | `0x18051A4F0` | Touches `BaseInteractable`, `ChargeShrine`, `InteractableMicrowave`, `InteractableShadyGuy`, `MapGenerationController`, `GameManager` — and **nothing challenge-specific**. `A_Completed` has no subscriber here. |
| `BaseInteractable.OnDestroy` | `0x1804BFAA0` | → `DetectInteractables.InteractableDestroyed` → `StopHovering`. **Does not touch either counter.** So the host/client asymmetry in the dispatch cannot move `numTotal`. |
| Container layout | — | `+0x10` is `numTotal` (spawn `+1`, disable `−1`); `+0x14` is `numUsed`. |

Read the field names in `Interact` with care: Ghidra's applied names are **one slot late** throughout
it. The byte tested and set at the top is `done` (`0x68`), printed as `fx`; the `HashSet<Enemy>`
assigned is `enemies` (`0x78`), printed as `hasGivenReward`; the object given `SetActive(true)` is
`fx` (`0x70`), printed as `enemies`. Offsets from `dump.cs` are authoritative.

#### The finding that matters more than the hypothesis

`InteractablesStatus.OnInteractableUse` is subscribed to **`DetectInteractables.A_Interacted`** — a
static `Action<BaseInteractable, bool>` on the *player's own interaction detector*, raised by
`DetectInteractables.TryInteract()`.

**So calling `interactable.Interact()` from code never raises it and never counts.** That holds for
every branch of the replication dispatch, on every peer.

Two consequences:

1. `CountIfDispatchDidNot`'s before/after comparison is doing nothing for any row except shady guy.
   Its answer is always "the dispatch did not count". That is not a bug — the count it then performs
   is correct, and the comparison is the right shape to keep, because it is exactly what caught the
   shady guy's *deferred* count. But it should not be read as evidence that the other rows were
   measured.
2. The host branch calling `shrineChallenge.Interact()` and the client branch running a cosmetic
   shortcut end at the **same** counter value: neither counts, and the helper adds one on both.
   The asymmetry is real for gameplay — only the host spawns the challenge wave — but it is not the
   counter divergence.

#### Where this stops

The 2026-09-04 host log shows `OB-12: counting 'Challenges' -- the dispatch did not.` **twice**, so
the path fires and counts on the host. Establishing that the client disagrees needs the client's
**`LogOutput.log`**; the client files supplied for that run were Unity player logs, which carry no
BepInEx lines. That is the third run this has cost.

**Next run needs `BepInEx/LogOutput.log` from *both* machines**, with a challenge shrine used by each
peer at least once. Until then the challenge-shrine divergence has no evidence behind it beyond the
on-screen reading.

### 2026-10-08 — confirmed

Reported fixed by the player, challenge shrine and shady guy included.

### 2026-10-11 — the client never counted its own challenge shrine

The desync detector's first working run caught it: `counter 'Challenges' host 1 / local 0` from
digest #83, ending 3 / 1. On a client, `DetectInteractables.TryInteract_Prefix` fakes the shrine
cosmetically and returns `false`, cancelling `TryInteract` and with it `A_Interacted`, the game's
only count path. Now counted with `InteractableCounterHelper.CountAsUsed` before the cancel. Likely
looked fixed on 2026-10-08 because no client used a challenge shrine that run.

**Also caught, not yet reported by the player:** `Charge Shrines` held a steady **+1 on the client**
(host 14 / local 15 at the end) for most of the run. Small and persistent; uninvestigated.

## OB-13 — Enemies keep taking damage while an encounter window has the game paused — **FIXED and CONFIRMED in play (2026-10-08)**

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
### 2026-10-07 — the first fix was bypassed by our own code

Reported still broken. The first fix gated `DamageFromPlayerWeapon`, `DamageFromPlayerOther` and
`DamageExternal`, and logged nothing, so "not working" could not be told apart from "never fired".

**Inside the game the gate was complete.** All three decompile to a single call to the private
`Enemy.Damage`, and an xref on `Damage` (VA `0x1804AC7B0`) gives exactly four callers: those three
and `Kill`.

**Our code was the hole.** `OnReceivedEnemyDamaged` calls `enemy.Damage(...)` directly —
Il2CppInterop exposes private members as public — so every hit replicated from the other peer walked
past all three gates. That route was live, not theoretical: [OB-21](#ob-21)'s phase slip meant the
other peer was usually *not* paused at the barrier and kept fighting through this peer's window.

**Now gated in `Enemy.Damage_Prefix`**, which covers the game's four callers and ours. The rule
lives in one method, `IsSuppressedForPause`, so the funnel and the entry gates cannot disagree.
Harmony runs postfixes even when a prefix skips the original, and `Damage_Postfix` broadcasts the
hit, so the prefix hands `__state` to the postfix to stop it announcing damage that never happened.

**Observable now:** `OB-13: suppressed N enemy hit(s) while paused` at Info, every 2 s of unscaled
time. A reward window with **no** such line means the gate is not reached; a window **with** lines
and health still falling means a fifth way in.

**Cost:** a hit already in flight when the window opens (~160 ms at the observed RTT) is dropped
rather than deferred.

**Two fixes may each be enough on their own.** If OB-21's barrier fix holds, the other peer is
paused too and stops sending hits during the window. This gate is the backstop for when it is not.

### 2026-10-08 — confirmed

Reported fixed by the player. The host log carries 53 `OB-13: suppressed N enemy hit(s) while paused`
lines and the client 14, so the gate at `Enemy.Damage` is what held, not a coincidence of timing.

## OB-14 — The end-of-run damage panel credits you with your teammate's weapons — **FIXED, CONFIRMED in play 2026-10-21** (one run-end edge fixed after, in `cd0b388`)

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

**Confirmed, 2026-10-21 Endless run (host plus two bots, eleven stage changes, the host's log kept; each bot carried a TestItems loadout).**

- **Host:** the end screen had 0 LVL 0 rows, and three `OB-14: kept '...' off our damage panel` lines.
- **Player2:** also 0 LVL 0 rows.
- **Player3: one left, `Cactus LVL 0` (24).** A teammate's Cactus hit was replayed after the run ended, once the teammates' inventories had been cleared and while the tracker was still read only when Started. That is fixed in `cd0b388`, which reads the tracker for replayed hits in every state; the fix itself is **UNVERIFIED**.

<a name="ob-15"></a>
### 2026-10-12 — confirmed from both end screens, and fixed

The panel's **LVL** column is the tell: it shows `0` for a source the viewer does not own. Each
player's end screen from the 2026-10-11 run listed the other's weapons and items at LVL 0, with nearly
their full damage:

| Source | Host screen | Client screen | Owner |
|---|---|---|---|
| Wireless Dagger | LVL 0, 644.5K | LVL 10, 602.4K | client |
| Lightning Staff | LVL 8, 1.2M | LVL 0, 1.1M | host |
| Big Bonk, Slurp / Power Gloves | LVL 0 | LVL 1 | client |
| Shattered Knowledge | LVL 1 | LVL 0 | host |

`RunStats.OnEnemyDamaged` (VA `0x180411930`, decompiled) is a static handler on the global
`Enemy.A_Damage` event that adds each hit to the panel's per-source table, and it ran for every hit
applied on the machine, teammates' included. A prefix now skips it when the tracker attributes the hit
to someone else; `CombatAura` now sets that attribution on the host for a teammate's aura.

**Gap closed:** Chunkers (`RotatingProjectiles.HitEnemy`), Frostwalker (`IceAura.FixedUpdate`), Dragon's
Breath (`ProjectileDragonsBreath.HitEnemy`) and Space Noodle (`LaserBeamAttack.HitEnemy` / `StopLaser`)
are now attributed the same way, per attack call. Not per hit: the game recycles one shared
`DamageContainer` for every weapon hit (see P1-5). Damage-over-time that ticks after its attack has
returned may still go unattributed; that can only leave a little teammate damage on a panel.

### Related: OB-19, and kill and gold credit

**Same pattern as [OB-19](#ob-19), different mechanism.** Both are a game-wide static event fired for a
teammate's object on this machine, reaching a listener that assumes the local player: `Enemy.A_Damage`
into `RunStats` here, `WeaponInventory.A_WeaponToggled` into `AuraAttacks` there (and earlier
`A_WeaponAdded`, already silenced). They are separate defects: OB-19 only ever toggled visuals, and the
panel's LVL 0 rows mean "not owned", so none of the panel's teammate rows came from OB-19.

**Kill and gold credit** (noted from a third-party 0.5 changelog as unchecked in ours): already gated by
the same tracker attribution before this fix -- `Enemy.EnemyDied_Prefix` registers a kill only when the
hit is ours or unattributed. The 2026-10-11 end screens fit that (4,594 kills against 3,425). The
`CombatAura` attribution added here moves a teammate's aura kills to them on the host; the four other constant
attacks are now attributed too, for kills as well as damage.

**The pattern to check for next:** any other static game event raised for a remote avatar's objects.
`A_StatUpdate` is already silenced around remote inventories; `AegisAttack.A_Used` / `A_Regen` are
avoided by blocking remote Aegis `FixedUpdate`.

### 2026-10-18 — two gaps in the first fix, both closed

**The client's panel lost its own damage** (eight rows, a few thousand each). The host forwards every
hit it applies and stamped `AttackerId` as itself on all of them, including the client's own hits,
which the client records only from that forward. Once `RunStats` skipped other players' hits, the
client skipped its own. `AttackerId` now comes from the tracker.

**The host's panel still listed the client's Dice and Slutty Rocket at LVL 0.** `Rocket.HitEnemy`
and `ProjectileDice.Hitscan` override the hit method the projectile patch attributes; there are 33
such hit sites. Attribution now happens where they all meet: a postfix on
`GetDamageContainer(WeaponBase, ...)` notes a teammate weapon's owner, and an `Enemy.Damage` prefix
scopes the tracker to exactly that hit. The per-attack patches stay, as narrower scopes.

**Also fixed while comparing these screens:** the host's aura looked slightly smaller on the client.
`GetAttackSizeMultiplier` multiplies (global × weapon) and our correction added; see OB-1.

### 2026-10-18 evening — the hit-level fix did not close it

End screens from the first run with `fe94466` and `abdafe7`. Host played Roberto, client played
Dicehead.

| Source | Host screen | Client screen | Owner |
|---|---|---|---|
| Electric Plug | **LVL 0, 34.1K** | LVL 2, 408 | client (item) |
| Cursed Doll | **LVL 0, 27.7K** | LVL 1, 14.9K | client (item) |
| Bob (Dead) | **LVL 0, 23.6K** | LVL 2, 3,634 | client (item) |
| Slutty Rocket | **LVL 0, 1,021** | LVL 4, 3,241 | client |
| Wireless Dagger | **LVL 0, 554** | LVL 3, 379 | client |
| Dice | **LVL 0, 453** | LVL 4, 4,486 | client |
| Aura | LVL 8, 13.5K | **LVL 0, 2,331** | host |
| Chunkers | LVL 5, 3,843 | **LVL 0, 946** | host |

**Fixed:** the client's own rows are back at their real levels, which was the first of the two
earlier 2026-10-18 gaps.

**Still open, two different shapes:**

1. **Items, on the host, larger than the owner's own number.** Electric Plug, Cursed Doll and Bob are
   the client's *items*. The host credits itself with 34.1K Electric Plug against the client's own
   408. The weapon-hit attribution notes only the weapon overload of `GetDamageContainer`; item
   damage goes through the other overload (the static at `WeaponUtility+0x18`), which is never noted.
   That the host's figure is *higher* than the owner's suggests the host also runs those items'
   effects for the client's avatar. **UNVERIFIED**: the item proc path needs decompiling.
2. **The host's constant attacks on the client** (Aura, Chunkers at LVL 0). The client records hits
   from the host's forwards, and the `AttackerId` the host stamps for its own constant attacks is
   evidently not reaching the client's `RunStats` filter as "someone else".

The weapon leaks on the host (Rocket, Dagger, Dice) are small next to the items, so most of what is
left is the item path.

### 2026-10-18 — items: the host was running a second, live copy of the client's items

Decompiled rather than reasoned about. **This was a damage bug, not only a panel bug.**

- **The owner already runs and forwards its items.** A client applies nothing itself, but
  `Enemy.Damage_Postfix` sends any hit whose source is in `AllowedDamageSource` (every `EItem` name),
  and an item's source is exactly `EItem.ToString()` (`ItemElectricPlug..ctor`, VA `0x1804561A0`). The
  client's 66 `[bw] EnemyDamaged` lines are those forwards. So the owner's copy reaches every peer.
- **Every other peer also holds a live copy.** `NetPlayer.AddItem` adds each teammate item to a real
  `ItemInventory`, so `Init` subscribes it to the game's static events. On the host,
  `NetPlayer.Update` calls the teammate's `PlayerInventory.PhysicsTick` (VA `0x1804E0680`), which runs
  `ItemInventory.Tick` (VA `0x180443500`) and so every item's `Tick`.
  - **Electric Plug**: `Init` (VA `0x180455A20`) subscribes `OnPlayerHit` to the static `PlayerHealth`
    hit event. The host's copy zapped enemies every time **the host** was hit: 34.1K, against the
    owner's real 408. The client's copy of the host's plug would equally fire on the client's hits
    and be forwarded as item damage.
  - **Cursed Doll** damages cursed enemies in `Tick` (VA `0x18043FC70`); **Bob (Dead)** spawns ghosts
    in `Tick` (VA `0x18043A5D0`).
- So those three items hit enemies twice, and the second copy was unattributed, which put it on the
  host's panel at LVL 0. Same family as OB-2 (`ItemGhost`).

**Fixed** in `Patches/Items/RemoteItemCopies.cs`: `ItemElectricPlug.OnPlayerHit`, `ItemCursedDoll.Tick`
and `ItemBobDead.Tick` return early when the item's own `itemInventoryRef` is not the local player's
inventory (native pointer compare, so no search in a per-frame path). Only these three: some item ticks
may do things only the host may do, and the host's copy is then the only one that works. **Other
items with a static subscription or a damaging `Tick` have the same latent shape** and need the same
check before being gated.

**Aura and Chunkers on the client, not found offline.** Every path read attributes the host's own
constant attacks to the host: `CombatAura.FixedUpdate` (VA `0x180352DC0`) builds each hit with the
weapon overload of `GetDamageContainer`, which refreshes the hit-level note with no owner for a local
weapon; the host stamps `AttackerId` from the tracker; the client replays with it. Instead of a guess,
`RunStatsPatches` now logs, once per source, when the panel records a source the local player owns no
weapon or item for, saying whether the hit was unattributed or attributed to us and whether it was a
replay:

`OB-14: recording 'Aura' as our damage, but we own no weapon or item by that name -- unattributed, replaying False, host False.`

Status effects (Burn, Poison) are expected once each.

**Confirms fixed as:** no LVL 0 Electric Plug / Cursed Doll / Bob (Dead) on the host's panel, those
three roughly equal on both screens, and the client log's `OB-14: recording` lines naming the path for
Aura and Chunkers.

### 2026-10-19 — the remaining LVL 0 rows were recorded outside `Started`

Three players: the host and two test bots. The final boss was killed. The item rows the previous
section fixed are gone. What was left:

| Screen | LVL 0 rows |
|---|---|
| Host | Soul Harvester 945, Black Hole 412, Aura 109 |
| Client (Player2) | Flamewalker 5,772, Soul Harvester 680 |

**The diagnostic's silence is what located it.** `ReportForeignSource` logs the first time any
source we do not own is recorded as ours, whether the hit was unattributed or attributed to us. It
fired once on the host, for Poison, a status effect. It never fired on either client. So none of
these rows was recorded through the path it watches. The prefix had exactly one way to record a hit
without reaching it: the early `return true` when `HasNetplaySessionStarted()` is false. The
session is in netplay but not `Started` during Ready, LoadingNextLevel (from a portal or the final
boss spawner until the next level runs) and Endgame. In those states the panel recorded every hit
applied on the machine, a teammate's included.

- **Host.** It still runs every teammate's weapons, and their hits went into its panel.
- **Clients.** `Enemy.Damage_Prefix` is gated on `Started` too, so a client lets local hits apply
  in those states, such as the host's projectiles it was sent.

This is the same ready-barrier hole as [OB-8](#ob-8).

**What LVL 0 means, decompiled rather than inferred:**

- **`DamageSource.GetLevel`** (VA `0x1804011E0`): `Enum.TryParse` the source as `EWeapon`,
  ignoring case, and return the local `WeaponInventory.GetWeaponLevel`. Failing that, parse it as
  `EItem` and return `ItemInventory.GetAmount`. Any other source shows 1.
- **The two getters** (VA `0x180432B20`, `0x1804427E0`): each returns 0 exactly when the key is not
  in the dictionary.
- **`RunStats.OnEnemyDamaged` is the panel's only writer.** `DamageSource.AddDamage` and its
  constructor have no callers of their own; they are inlined there.

So a LVL 0 row is, by definition, a weapon or item source the viewer does not hold.

**Fixed** in `Patches/RunStats.cs`. The prefix now runs while the session is *initialized*:

1. **The tracker filter is unchanged.** It still applies only while `Started`, because the patches
   that scope the tracker are all gated on that.
2. **A second test needs no tracker, so it holds in every state.** It drops a hit whose source
   parses (the game's own order and case rule) as a weapon or item the local player does not hold
   and a spawned teammate does. While `Started` it also catches a teammate hit the tracker left
   unattributed (`UNRESOLVED-WEAPON`).

Requiring a teammate who holds it keeps the fix from hiding real damage: a game effect could carry
the name of a weapon or item we lack.

**Known gap.** A source both players hold is left to the tracker. Outside `Started` such a hit can
still add to our row, but at our real level, not as a LVL 0 row.

**UNVERIFIED, not run in-game.** Confirmed when:

- No end screen shows a LVL 0 weapon or item row.
- Each peer's log carries one `OB-14: kept '<source>' off our damage panel` line per teammate source
  it dropped. The line's `session started` value names the state it happened in.

### 2026-10-19, second run — the filter works; one gap at the portal, and what it uncovered

Same setup, with `63a7b36` in. Both clients' panels have no LVL 0 rows. The host's has one:
**Wireless Dagger, LVL 0, 24**. That weapon belongs to Player2.

**The filter is catching real leaks.**

| Peer | Lines | Whose sources |
|---|---|---|
| Host | `LightningStaff` (Player3's) | a teammate's |
| Player2 | `GlovePower`, `IceCube`, `Bonker`, `SpicyMeatball` | the **host's** items, each with `replaying True` |

**The gap.** The host logged `OB-14: recording 'BluetoothDagger' as our damage ... unattributed` on
the line right after `Cleared all player cards`. That is the portal into the final arena:
`ResetForNextLevel` had just cleared the spawned avatars, and a dagger still in flight landed with no
avatar left to match. The scan now reads the teammates' *inventories*
(`PlayerManagerService.GetRemoteInventories`), which survive the level change, instead of the
spawned avatars.

**What the Player2 lines uncovered, decompiled.** These were the host's own items, replayed on
Player2 under Player2's id. So on the host, the host's items had proced inside a hit that the
tracker attributed to Player2. That is how the game is built:

- **`Enemy.DamageFromPlayerWeapon`** (VA `0x1804AC700`) calls `Damage` and then
  `MyPlayer.Instance.inventory.itemInventory.PostDamage(dc)`. That is the **local** player's
  on-hit items, whoever owns the weapon.
- **`WeaponUtility.GetDamageContainer`**, both overloads (`0x180434A50`, `0x180434FF0`), runs the
  local player's `ItemInventory.PreAttack`. It also reads crit chance and crit damage from the
  static `PlayerStats.GetStat`.
- **So the host is the only player whose on-hit items proc.** Every teammate weapon hit the host
  simulates gets the host's pre-attack modifiers, the host's crit stats and the host's on-hit
  procs, and those are credited to the teammate.

The panel side of that is now filtered. **The gameplay side is open and LIKELY:** a teammate's own
on-hit items do not proc from their weapons on the host, and the host's do. A teammate's items
that the client runs locally still reach the host through the client's item forward (Player2's
Thunder Mitts and Zap rows), so not every item is affected.
**UNVERIFIED in play** beyond the panel evidence above.

## OB-15 — The client's NullReferenceException storm — **FIXED and CONFIRMED in play (2026-10-12)**

> Confirmed by absence: zero exceptions on both peers across the 2026-10-07, 10-08, 10-11 and
> 10-12 runs, two of which reached the Desert final boss.

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
## OB-16 — Desert final-boss pylon charge throws on the host — **FIXED and CONFIRMED in play (2026-10-12)**

> Confirmed by absence: zero exceptions on both peers across the 2026-10-07, 10-08, 10-11 and
> 10-12 runs, two of which reached the Desert final boss.

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
## OB-17 — The host throws in MinimapCamera.UpdateArrows — **FIXED and CONFIRMED in play (2026-10-12)**

> Confirmed by absence: zero exceptions on both peers across the 2026-10-07, 10-08, 10-11 and
> 10-12 runs, two of which reached the Desert final boss.

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

### 2026-10-19 — a second throw in `UpdateArrows`: arrows for destroyed avatars

Host, three players (owner plus two test bots): 72 pairs of `NullReferenceException` in
`Component.get_transform` from `MinimapCamera.UpdateArrows`, right after a teammate's avatar was
rebuilt at the stage-2 transition (`Spawned NetPlayer for …`). `arrowDict` is keyed by each tracked
player's `Transform`; the old avatar was destroyed but its entry stayed, and `UpdateArrows` read it
every frame. The first OB-17 fix only covered a missing local player. **Fixed:**
`UpdateArrows_Prefix` now prunes entries whose key or arrow is destroyed (Unity null) before the
original runs, and logs `[minimap] dropped N arrow(s) whose target was destroyed.` **Confirms fixed
as:** no `UpdateArrows` exceptions across a stage transition with three or more players.

<a name="ob-18"></a>
## OB-18 — "Projectile Bounces" stays 0 for the whole run — **CLOSED: vanilla behaviour, not a bug (2026-10-08)**

**Reported 2026-08-18, with a screenshot.** The stat panel reads `Projectile Bounces 0` all run.
Same panel shows `Projectile Count 1`, `Size 1.0x`, `Duration 1.0x` — those are the base values, and
the run was level 6 with several items.

### First, whether this is a bug at all

**0 is the correct value for a player with no bounce source.** Nothing in the screenshot shows a
bounce-granting item, so a panel reading 0 may simply be reporting the truth.

**The check that settles it, and it needs no second machine:** take a bounce item in
**singleplayer** and watch the row.

| Singleplayer | Meaning |
|---|---|
| rises | netplay-specific — this entry is real, read on |
| stays 0 | the game's own behaviour, or the item does not grant what it appears to; **not ours** |

**Do not write a fix before that check.** This is the same trap [OB-6](#ob-6) sat in for weeks: a
protocol-level analysis of something nobody had confirmed was visible.

### The accessor is the same shape as OB-1's

```
GetProjectileBounces(weaponBase)  VA 0x180435930
  = weaponStats[EStat.ProjectileBounces(45)] + PlayerStats.GetStat(45)
```

Identical to `GetAttackQuantity` and `GetAttackSizeMultiplier` — so bounces is in
**[OB-1](#ob-1)'s class**, and the stat-block replication added for OB-1 should carry it. It does
not today: `TrackedStats` in `PlayerStatsSyncService` lists Projectiles, SizeMultiplier,
DurationMultiplier, ProjectileSpeedMultiplier and AttackSpeed. **Adding `ProjectileBounces` (45) is
one line** and needs no protocol change, because the message carries a keyed dictionary precisely so
the tracked set can grow.

**But that fixes a different symptom than the one reported.** OB-1 is about a *remote* player's
attack drawn with the observer's stats. This report is the panel showing the **local** player's own
stat, which OB-1's correction never touches. Adding the stat to the block is worth doing anyway and
would not, on its own, make this row move.

### The one mod-side mechanism that could actually zero it

`Plugin.SavePlayerInventoryActions` **nulls the game's own stat-update handler**:

```csharp
originalStatUpdateAction = PlayerStatsNew.A_StatUpdate;
WeaponInventory.A_WeaponAdded = null;
PlayerStatsNew.A_StatUpdate = null;      // ← the game stops reacting to stat changes
```

restored in a `finally`. It is used in two places — `NetPlayer` construction and
`SynchronizationService` — both while building a *remote* player's inventory, so the game does not
react to the fake inventory being assembled.

**The hazard: it is a global static, and the local player's stat changes are just as suppressed
during that window.** A stat granted while a remote player's inventory is being built would not be
reacted to. Whether the derived value is ever recomputed afterwards, or is only computed in that
handler, is **not established** — and that is what decides whether this mechanism can produce a
permanent 0.

**Next step if singleplayer says it is ours:** log `PlayerStats.GetStat(45)` alongside the suppression
window and see whether a bounce pickup lands inside one.

**Not established:** whether the panel reads `GetProjectileBounces` at all, or reads
`PlayerStats.GetStat(45)` directly. They differ by the per-weapon half, and if the panel takes the
second path then the weapon's contribution was never in the number to begin with.

---

<a name="ob-19"></a>
### 2026-10-08 — closed: the global stat is unused in the base game

The global *Projectile Bounces* stat reads 0 in singleplayer too. Bounces exist only as **per-weapon**
upgrades (Pistol and Lightning Staff among them), and nothing in the game raises the global stat —
not items, tomes, Chaos Tome, the Dice character or shrines. Source: the Megabonk Steam community
thread *"Whats the point of the Projectile Bounce stat?"*
(https://steamcommunity.com/app/3405340/discussions/0/598540711282025719/), reported by the player.

**Nothing to remove.** `ProjectileBounces` stays in OB-1's tracked stats: the correction is
`result - observerGlobal + ownerGlobal`, which is `result - 0 + 0` while the global stat is unused, so
per-weapon bounces pass through unchanged. If a future game update starts using the stat, the
replication is already in place.

## OB-19 — Final-boss weapon return gives a player somebody else's weapon — **FIXED and CONFIRMED in play (2026-10-18)**

**Reported 2026-08-18.** The Desert final boss takes the players' weapons and returns them as its
phases progress. After a return, **a player gains the visual for a weapon they never owned** — P1 had
Aura, P2 did not, and after the return P2 had the Aura visual too.

**Only during the final fight**, which is the clue rather than a coincidence: it is the one moment
weapons are removed and re-added *en masse*, so a latent hole in the add path surfaces there and
nowhere else.

### The mechanism, from code rather than inference

`FinalFightController.GiveWeaponBack` (VA `0x1804929E0`) restores to **`MyPlayer.Instance`** — the
local player. That much is correct by construction: each peer restores its own. **Neither
`TakeWeapon` nor `GiveWeaponBack` is patched or replicated by the mod**, so both simply run on each
peer.

The hole is in the add path they run through:

```csharp
// Patches/Inventories/WeaponInventory.cs — AddWeapon_Post
if (!synchronizationService.HasNetplaySessionStarted()) return;
if (!Plugin.CAN_SEND_MESSAGES) return;
synchronizationService.OnWeaponAdded(__instance, weaponData, upgradeOffer);
```

```csharp
// SynchronizationService.OnWeaponAdded
OwnerId = playerManagerService.GetLocalPlayer().ConnectionId,   // ← always the local player
```

**The postfix fires for *any* `WeaponInventory` and the message is stamped with the local player's
id regardless of whose inventory `__instance` actually is.** Remote players are not abstract here:
`NetPlayer.Inventory.weaponInventory` holds real inventories on this machine —
`GetNetPlayerByWeapon` exists precisely because it does. So an `AddWeapon` on a *remote* player's
replicated inventory is broadcast as though the local player had gained that weapon, and the
receiving peer applies it to the wrong player.

**This patch is missing its ownership check**, which is the standing rule in `CLAUDE.md`: session
guard *and* ownership check. It has the first. `CAN_SEND_MESSAGES` suppresses the echo when applying
a received message, which is why this does not loop — but it does nothing about *whose* inventory
was added to.

**It is the third instance of one pattern on this branch.** [OB-2](#ob-2) was remote players'
`ItemGhost` instances firing on a global event; [OB-15](#ob-15) was a remote challenge shrine's
handler running on a peer that never populated it. All three are *real game objects belonging to
another player, living on this machine, reached by a path that assumes "local"*.

### Fix shape

Gate `AddWeapon_Post` on `__instance` being the local player's inventory — compare against
`GameManager.Instance.player.inventory.weaponInventory`, or reject it when
`GetAllSpawnedNetPlayers()` holds it, the way `IsRemoteItem` does for OB-2. The second reuses an
existing shape; the first is cheaper and does not enumerate.

**Do not fix by suppressing the final fight specifically.** The final fight is where it was seen, not
what causes it — any future path that adds a weapon to a replicated inventory has the same hole.

### Status LIKELY, and what would settle it

The code path is read and the stamp is provably unconditional. What is **not** established is that
the final fight actually calls `AddWeapon` on a remote player's inventory — that is the step joining
a real hole to the reported symptom.

**One log line settles it:** in `AddWeapon_Post`, log whether `__instance` is the local player's
inventory, and play to the final boss. A single "false" during the weapon return confirms the chain.

**Not established:** whether the effect is visual only. The reporter says "likely visual", but the
message carries `upgradeOffer` and the receiver applies a real weapon, so the wrongly-added weapon
may also *fire*. Worth watching the damage panel for a source that player never had — which is the
same reading [OB-14](#ob-14) needs.

---

### 2026-10-07 — checked against a third-party mod's 0.5 release

Its `WeaponInventory.AddWeapon` postfix decides local-or-remote from an ambient "applying a remote
player's action" flag and never checks whether `__instance` is the local inventory — the same hole this
entry describes, and it names no final-boss weapon handling. The fix shape above (compare `__instance`
against the local inventory's `Pointer`) remains the stronger one. Still waiting on a run that reaches
the final boss for the `[weapon-owner]` line.

### 2026-10-11 — not AddWeapon at all: a static toggle event

Reproduced: both players received each other's stolen weapons. `[weapon-owner]` never fired, which
disproved the AddWeapon hypothesis. Decompiled, `FinalFightController.TakeWeapon` and
`GiveWeaponBack` only call `WeaponInventory.ToggleWeapon`, and only on `MyPlayer`'s own inventory.

The leak was ours: `WeaponInventory.A_WeaponToggled` is a **static** event, so replaying a peer's
toggle on their avatar notified the local player's `AuraAttacks.OnWeaponToggle` too. The add path
already silences the sibling `A_WeaponAdded`; the toggle path now does the same, and shows or hides
the avatar's own constant attack directly so a stolen aura disappears on other screens.

## OB-20 — A player who dies stays dead through the level transition and ends the run — **FIXED and CONFIRMED in play (2026-10-12)**

**Reported 2026-09-04, Desert Tier 1, two peers.** The host died early, spectated for roughly half
the level, and at the portal the run ended for both players rather than continuing on level 2.

**Expected:** every peer crosses the portal, and any peer who is dead is revived on arrival so the
run continues.

### What already works, and is not the bug

Dying mid-level is handled. `Plugin.PreventDeath` swaps `PlayerHealth.A_Died` for
[`OnPlayerDied`](../../src/plugin/Plugin.cs), which does not end the run — it switches the camera to
a living peer. That is the spectator state the reporter was in, and it behaved correctly for the
whole level. `InteractableReviver` is the intended way back: a peer kills the coffin enemy, the host
calls `OnRespawn`, and the dead player is fully restored.

`IsGameOver()` is `players.Values.All(p => p.Hp == 0)` and is **not** the trigger — one player was
alive throughout.

### Mechanism

The level transition lifts death prevention and restores nothing else.
[`SynchronizationService.cs:932`](../../src/plugin/Services/SynchronizationService.cs), on
`GameEvent.PortalOpened`:

```csharp
playerManagerService.ResetForNextLevel();
PrepareForNextLevel();
Plugin.Instance.ClearPrefabs();
Plugin.Instance.RestoreDeath(false);      // hands PlayerHealth.A_Died back to the game
```

Compare that against the local branch of `OnRespawn`, which is the mod's own complete
revive routine and does **five** things:

| # | `OnRespawn` (working revive) | `PortalOpened` |
|---|---|---|
| 1 | `player.transform.position = position` | — (the game places them on the new level) |
| 2 | `playerHealth.hp = playerHealth.maxHp` | **missing** |
| 3 | `localPlayer.Hp = localPlayer.MaxHp` + `UpdatePlayer` | **missing** |
| 4 | `CameraSwitcher.ResetToLocalPlayer()` | done, inside `RestoreDeath` |
| 5 | `playerRenderer.gameObject.SetActive(true)` | **missing** |

So the transition performs step 4 alone. The dead player arrives on the next level with `hp == 0`,
their renderer still disabled, and their mirrored `Player.Hp` still 0 — `ResetForNextLevel` clears
`IsReady` and the spawned avatars but never touches `Hp`. Death prevention is meanwhile *off*: it is
re-armed only at the end of `SpawnPlayers` on the new level, and re-arming replaces the death
*handler*, it does not resurrect somebody already dead.

**The remote side self-heals and needs nothing.** `ResetForNextLevel` calls `spawnedPlayers.Clear()`
and `SpawnPlayers` rebuilds every `NetPlayer` on the new level, so the latched `isDead` flag set by
`NetPlayer.OnDied` is discarded with the old object. Only the local player is left broken — which is
why the symptom is per-peer rather than a desync.

### Fix shape

Revive the local player at `PortalOpened`, reusing steps 2, 3 and 5 above. Every peer runs this for
itself; the readiness barrier already guarantees all peers reach `PortalOpened`, so no message and
no new union tag is needed, and the restored HP propagates through the existing `PlayerUpdate`.

Extract the local branch of `OnRespawn` into `ReviveLocalPlayer(Vector3? position)` and call it with
`null` from both sites — `OnRespawn` and `OnReceivedPlayerRespawned` already duplicate that block
verbatim, so this removes a copy rather than adding one. Skip the position: the game places the
player on the new level, and forcing a stale coordinate there would be worse than doing nothing.

**Guard it on actually being dead.** Unconditionally setting `hp = maxHp` at every portal would full-heal
a living player each level, which is a much larger balance change than the one being asked for.

### Open design question — not answerable from the code

Reviving at every transition makes death cost nothing beyond the remainder of the current level, and
makes `InteractableReviver` mostly redundant. Full HP, or a fraction? That is a balance call for the
reporter, not something the code implies.

### Also seen, benign

The host log ends with `Death not prevented` from `RestoreDeath`. That is the return-to-menu path
([`WindowManager.cs:55`](../../src/plugin/Patches/WindowManager.cs)) calling `RestoreDeath` after the
`GameOver` path already did. The method is idempotent and the warning is the guard working. Not
related to this bug — noted so it is not chased again.

---

### 2026-10-12 — confirmed

The host died on level 1, stayed dead, and arrived on level 2 fully revived; the host log shows
`OB-20: local player was dead at the portal; revived for the next level.`

**Side effect, since fixed:** for about 50 s after the revive the desync detector showed the client one
level ahead (`level host 30 / local 31`, digests #194-#204). Likely XP that a dead player did not
receive. It self-corrected, but while it lasted the client reached each level-up first and waited at the
barrier for the host.

## OB-21 — The run freezes at an encounter and the failsafe fires every frame — **FIXED and CONFIRMED in play (2026-10-11)**

**2026-10-07, Desert, two peers.** Neither player could finish a run. The host's log holds **5,480**
`Shared-experience failsafe fired` warnings and **5,509** barrier rounds inside 38 seconds; the
client's holds **zero** warnings and 5,480 `Applied release` lines. The host stayed paused behind an
encounter window that never closed.

This is two separate defects stacked. Keeping them apart matters, because only one is fixed.

### Part 1 — the trigger: a stale `forceClose` runs one peer an encounter ahead — **FOUND and FIXED 2026-10-07**

Rounds 0–28 are healthy: each shows `Report from <client> accepted for round N` then
`Released round N`. **From round 29 there is not one report.**

| Peer | At the stall |
|---|---|
| Host | `[pause] encounterWindow=False … t=666.96` → release of round 28 → `[pause] encounterWindow=True MyTime.paused=True t=667.90`, and **no further `[pause]` line for the rest of the log** |
| Client | closed its window at its own `t=392.75`, then ~20s of ordinary play — `PlayerUpdate` at 60 Hz, two full bandwidth reports — with **no encounter window at all** |

The link is closed, not lossy: `rtt 160 ms quality 100.0%/100.0%, pending 0 B, unacked 0 B` on both
sides throughout, and the client applied every release the host sent.

`BeginWaiting()` has exactly one caller, `RewardFinished()` — a peer starts its failsafe clock only
when **its own** reward window finishes. A peer that never receives an encounter in a round has
nothing to report *and no clock*, so it neither releases the barrier nor rescues itself. That is why
the client logged zero failsafes: not that its failsafe worked, but that it never knew a round was
open.

**LIKELY mechanism, not established:** the host's window opened **0.94 s** after the previous round
released, which is the signature of a second queued reward (`EncounterWindows.rewardQueue`) popping
immediately. If one peer has two rewards queued and the other has one, the peers' round counters
diverge by one and the barrier can never complete. What the logs do **not** show is what put the
extra reward on the host's queue.

#### The mechanism, measured

Both peers opened **29** encounter windows — they are not out by an encounter, they are out of
*phase*. Aligning the logs by the constant **274.2 s** offset between the two clocks, every window
pairs to within 0.03 s for 28 rounds and then stops:

| Client | +274.2 | Host |
|---|---|---|
| 379.21 window opens | 653.4 | 653.42 window opens ✓ |
| 381.88 window closes | 656.1 | 656.09 window closes ✓ |
| — | — | **664.68 paused, no window** — host waiting at the barrier |
| **391.22 window opens** | 665.4 | — host has nothing open |
| 392.75 window closes | 667.0 | 666.96 release of round 28 |
| — | — | **667.90 window opens** — alone, and this is round 29 |

The client opened its next window **while the host was still waiting**, and the release for round 28
killed it 1.53 s later. The host then opened the matching encounter at 667.90 with nobody left to
report it, because the client had already burned it unseen.

**Why the client never waits.** `RewardFinished_Prefix` returns early when the reward queue is
non-empty — *before* it reaches `ClearClosedEncounters()`. A peer holding a queued reward therefore
keeps **both** `waitingSince` and `forceClose`. The stale clock is Part 2's storm. The stale
`forceClose` is the quieter half: the next round's `RewardFinished_Prefix` finds `IsClosable()`
already true, closes the window and unpauses **without ever calling `RewardFinished()`** — no
report, no wait. That peer is one encounter ahead from then on.

The count settles it: **across 29 rounds the client entered the barrier-wait state once, and the
host twice.** A working barrier puts both peers there every round.

**This also explains lost picks nobody had filed.** A window open for 1.53 s and shut by an incoming
release is a reward the player never got to choose.

**Fixed** by calling `ClearClosedEncounters()` unconditionally *after* the branch in
`OnCloseEncounter()`. It cannot move before the branch: callers set `forceClose` immediately
beforehand so the re-entrant `RewardFinished_Prefix` takes the `IsClosable()` path and closes the
window instead of reporting twice, and clearing first would turn that close into a duplicate report.

**Correction to the first attempt.** The narrower `StopWaiting()` fix reset the clock but not the
force flag, so it would have stopped the storm and left the phase slip — the symptom without the
cause.

### Part 2 — the amplifier: the failsafe clock is reset on only one branch — FIXED

`ClearClosedEncounters()` is the only thing that resets `waitingSince`, and `OnCloseEncounter()`
reaches it only on the branch taken when **no** reward window is open:

```csharp
if (encounterWindows != null && encounterWindows.encounterInProgress)
    encounterWindows.RewardFinished();          // clock NOT reset
else
    { encounterService.ClearClosedEncounters(); MyTime.Unpause(); }   // clock reset
```

A peer holding an open window takes the first branch, keeps `waitingSince` latched at the first
round's start, and is `HasWaitedTooLong()` from then on. `EncounterWindows.LateUpdate` tests it every
frame **and keeps running at `timeScale 0`** — so the failsafe fires once per frame forever, each
fire releasing a round and opening the next.

Three things in the log confirm it without reading any code:

1. The warning's own elapsed figure **climbs** — `after 20.0s` … `after 58.5s` — instead of
   restarting per round. That is the un-reset clock, printed.
2. 5,480 fires over 38.5 s is **142/s**, against the 144 FPS in the reporter's screenshot: one per
   frame.
3. The asymmetry names the branch. The host's window was open (first branch, no reset, 5,480 fires);
   the client's was closed (second branch, reset, 0 fires).

Fixed by `IEncounterService.StopWaiting()`, called unconditionally at the top of
`OnCloseEncounter()`. Narrower than `ClearClosedEncounters()` on purpose: reaching that method means
a release for the round was applied, so the wait is over on either branch, but the reports and the
force flag are barrier state only the existing branch should rewrite.

Superseded by the Part 1 fix, which resets the whole of the per-round state rather than only the
clock. Both halves come from the same asymmetry.

---

### 2026-10-08 — the storm is gone, but the second fix stranded the host

**Confirmed:** 2 failsafe fires on the host and 1 on the client across 48 rounds, not 5,480. The
phase slip did not recur.

**New failure, caused by the fix:** the host stayed paused for good. The client's report for round 48
was accepted twice — the second from its own failsafe — with `barrier closable: False`, which with
two players means the host's own report was missing. The host never fired a third failsafe.

The host failsafe reaches `OnCloseEncounter` through `ReleaseBarrier` **without the force flag** (the
client's failsafe sets it; the host's never did). With a window open, the re-entrant
`RewardFinished_Prefix` found the barrier not closable and filed the host's report for the *next*
round, starting its clock — and the post-branch `ClearClosedEncounters` from the second fix then
wiped both. Paused, window hidden, no report, no clock.

**Third version:** clear the spent round first; set the force flag inside `OnCloseEncounter` for every
caller so the re-entrant prefix closes the window; drop the flag straight afterwards so it cannot leak
into the next round. Host and client now take the same path.

**Lesson recorded:** two consecutive fixes here each changed *when* per-round state is reset without
mapping every re-entrant path that creates it. The release path re-enters the report path; any change
to either must be checked against both.

### 2026-10-11 — confirmed

A full run to the final boss with no stall reported.

## OB-22 — A peer's Steam avatar never appears for one side of the lobby — **FIXED and CONFIRMED in play (2026-10-11)**

Reported 2026-10-07 with screenshots: the host's lobby showed no avatar for the client, while the
client's showed both.

`GetMediumFriendAvatar`'s two sentinels were read the wrong way round. Per the Steamworks
`ISteamFriends` reference — `GetLargeFriendAvatar` states it explicitly, the Medium entry is
abbreviated but behaves the same:

| Value | Meaning | The code assumed |
|---|---|---|
| `0` | no avatar is set for that user | "fetching; ask again" |
| `-1` | **not loaded yet** — Steam starts downloading and raises `AvatarImageLoaded_t` | "will never answer" |

`-1` went into `failed`, which is tested before anything else and cleared only at session teardown.
So the ordinary first answer for a user Steam has not cached permanently disabled that avatar for the
whole session. The same reference says an uncached user is anyone who is not a friend, not on a
requested leaderboard and not yet known through a shared lobby — the normal case in a lobby, not an
edge case. Whichever peer has not cached the other loses the avatar, which is why it looked
arbitrary and one-sided.

Both sentinels are now retryable and call `RequestUserInformation`, throttled to once per second per
account because the lobby panel rebinds its rows on every refresh. `failed` keeps its documented
meaning: a read that threw or returned garbage.

**Reproducing it needs two accounts that are not Steam friends and have not played together
recently** — which is probably why it has not been seen before.

---

### 2026-10-08 — still broken; the real cause was upstream of the avatar call

Reported unchanged after the sentinel fix. On the host, the avatar lookup never ran at all.

`SteamLobbyService.RefreshMembers` ran only from `ApplyLobbyCreated` and `ApplyLobbyEnter`, so a
member who joined *later* was never in the cached member list. `LobbyView.ResolveSteamIds` then had no
Steam account for their connection id, their row got SteamId `0`, and `TryGetAvatar(0)` returned null
without a log line.

| Peer | Cached member list | Result |
|---|---|---|
| Host | filled at creation, alone; never grows | client's row has SteamId 0, no avatar |
| Client | filled on entry, host already present | host's row resolves, avatar shows |

The host's *own* row reads `LocalSteamId` directly and never touched the list, which is why the host
always saw its own face. The join key was checked and is not at fault: the client published
`82462037` and the host knows it by the same id.

**Fix:** refresh the list once a second from `Poll` while in a lobby, and log whenever the count
changes. Polled rather than driven by `LobbyChatUpdate_t`, because broadcast callbacks through the
game's interop are still unproven. The sentinel fix stands — it was a real bug — but could not have
fixed this.

**Confirms as:** `[steam-lobby] Member list now has 2 member(s) (was 1).` in the host log when the
friend joins, and his avatar on the host's screen.

## OB-23 — The client's camera detaches from its character during the failsafe storm — **CLOSED 2026-10-21: has not reappeared**

Reported alongside [OB-21](#ob-21). Not explained, and recorded as unexplained rather than guessed.

**Eliminated:**

- **Not an exception.** The client's `Player.log` contains zero exceptions or errors for the whole
  run, with Unity stack traces enabled.
- **Not a death.** Neither peer died; `Death not prevented` appears once on each, from the
  return-to-menu path already written up under [OB-20](#ob-20). So the spectator camera in
  `Plugin.OnPlayerDied` was never armed.

**What the client actually did** is run `OnCloseEncounter()` 5,480 times in 38 seconds, once per
release applied. With no window open, each pass took the else branch: `ClearClosedEncounters()` and
**`MyTime.Unpause()`** — several thousand unpauses against no pause, while the host was frozen and
therefore publishing no authoritative state.

That is a strong candidate and it is not evidence. **Re-test after the OB-21 fix before
investigating further:** if the storm is what detached the camera, the storm is gone and so is this.
If it recurs without a storm, it is a real and separate bug and this entry gets a mechanism.


### 2026-10-11

Not seen in the first run after the OB-21 fixes. Consistent with it having been a side effect of the storm.

## OB-24 — The client's boss health bar never moves — **FIXED and CONFIRMED in play (2026-10-18)**

**Reported 2026-10-11, final stage.** The client's bar read `6.2m / 6.2m` for the whole fight while
the host's read `7.9m / 8.4m`, and the client appeared unable to damage the boss.

The client's boss HP was being updated; the bar never redrew it. Decompiled `TargetOfInterestPrefab`:
`Update` only fades the bar and redraws debuffs. `UpdateHp` (the text and the bar) runs only from
`OnDamage` and `OnHealthChange`, which `Awake` subscribes to the static `Enemy.A_Damage` and
`Enemy.A_HealthChange`. A client writes a boss's HP directly in `EnemyInterpolator`, and its maximum in
`ApplyRescaledHp` (OB-9), so neither event fired and the bar kept its spawn values. That explains
both numbers being frozen together.

**Fix:** both writes raise `Enemy.A_HealthChange`, the event the game raises when health changes
outside a hit. Bosses only, and from the interpolator only on an actual change.

**Not established:** whether the client's hits also failed to land. The host's bar fell during the
fight, so some damage reached the boss, but the log cannot say whose.


## OB-25 — `Look rotation viewing vector is zero` floods the client log while the host fires rockets — **FIXED 2026-10-24, confirmed in play**

**What it is.** A Unity warning, not an error: something called `Quaternion.LookRotation` with a
zero-length direction. Unity returns the identity rotation and carries on, so at worst a projectile
faces the default direction for one frame. No player has ever noticed anything in play.

**Where it comes from: the host's rockets, on the client.** It appears only on clients (0 on the host
in every run), and only in runs where the host fires rockets:

| Run | `SpawnedRocketProjectile` in the host's `[bw]` samples | Warnings in the client log |
|---|---|---|
| earlier session (handover 13) | none | 0 |
| earlier sessions (handovers 12-13) | present | 23,622 → 870 |
| 2026-10-07 | present | 178 |
| 2026-10-11 | 118 samples | **8,961** |
| 2026-10-12 | none | **0** |

Zero rockets has meant zero warnings every time it has been measured. The count scales with how much
the host uses rocket weapons, not with session length.

**Mechanism (LIKELY, not decompiled).** On a client, a remote rocket is a real game object that
`ProjectileInterpolator` *teleports* to each snapshot position, while the game's own projectile code
still runs on it. That code derives the rocket's facing from its movement; on a frame where the
interpolator has not moved it, the movement is zero and the facing call logs. Two authorities on one
transform: the interpolator owns position, the game owns everything else.

**Not a missing guard in our code.** Every `LookRotation` call in the mod is accounted for: three are
guarded against zero vectors (`NetPlayer.cs` x2, `ProjectileInterpolator.cs`), and the third
`NetPlayer` site runs about twice a run. The calls that log are the game's, reacting to our teleports.

**Cost today.** One log write per occurrence on the client's main thread: about 8,961 over a full run
when the host is rocket-heavy. Not measurable in play, but it buries real warnings in the log.

### Why it is parked

Four separate patches to remote projectiles have already been made and each fixed a symptom of the
same split authority (see handovers 12 and 13). The handover's instruction stands: *do not patch it a
fifth time*. This wants a design decision.

### Fix options for whoever picks this up

1. **Inert visuals.** Spawn a client's remote projectiles as plain visual objects with the game's
   projectile components removed or disabled, driven only by the interpolator. Removes the conflict
   entirely. Risk: anything the client relies on from those components (hit effects, sounds,
   expiry) has to be reproduced or replicated.
2. **Give the interpolator the whole lifetime.** Keep the game components, but have the interpolator
   also own expiry and facing (set rotation from the snapshot delta, skipping zero deltas), and stop
   the game's own movement update on remote projectiles.
3. **Send events, not transforms.** Replicate "fired, with this origin, direction and speed" and let
   each client simulate the projectile locally, as the game would. Cheapest on bandwidth; risks
   drift between peers for long-lived projectiles.

**Before choosing, decompile first:** find which rocket method calls `LookRotation` (start from the
rocket projectile class's `FixedUpdate` / `Update` in `dump.cs`) to confirm the mechanism above.

**Cheap stopgap if the log noise ever matters:** a prefix on that one game method that skips the
facing update when the movement delta is zero, on remote projectiles only. It silences the warning
without touching the design question.

**Confirms fixed as:** a run where the host fires rockets heavily and the client log has no
`Look rotation viewing vector is zero` lines.

### 2026-10-23 — the 10-19 caller does not explain the current spam

The 2026-10-22 run still logged **15,080** of these on the client, about a third of its log. But
`RocketPatches.MyFixedUpdate_Prefix` already returns false on clients for every rocket carrying an
`ownerId`, so `Rocket.FixedUpdate` → `StepMovement` → `LookRotation` cannot run for received rockets.
Either some client rockets carry no `ownerId`, or another `LookRotation` call is logging. Next step:
one run with `[Diagnostics] LogUnityStackTraces = true` and `LogUnityMessageStackTraces = true` on the
client; its `Player.log` then names the caller.

### 2026-10-23 — stack trace captured; root cause found and fixed, not yet run in game

The client's `Player.log` from the 2026-10-22 run gives one stack for **all 15,080**:
`Quaternion.LookRotation(Vector3)` ← `Rocket.StepMovement()` ← `Rocket.FixedUpdate()`. So received
rockets **do** run the game's flight on the client, although `MyFixedUpdate_Prefix` should skip any
rocket tagged with `ownerId`, and clients never tick weapons (`WeaponInventory.Tick_Prefix`). The host
logged 9 all run: simulation alone rarely yields a zero direction; simulation plus the interpolator's
teleports does. Decompiled `ProjectileRocket.TryInit` (0x1804F0C10) reuses its child rocket (field
0x68), so the tag is not lost to a swapped child.

**Root cause (CONFIRMED by decompiling the two libraries, not yet in game):** the tag lives on a
managed wrapper that nothing keeps alive.

- MonoMod `DynamicData.For` stores data in a `ConditionalWeakTable<object, …>`, keyed by the
  managed object's reference identity (ilspy of the game's `MonoMod.Utils.dll`).
- `Il2CppObjectPool.Get` caches wrappers for non-injected types in a
  `ConcurrentDictionary<IntPtr, WeakReference<Il2CppObjectBase>>`; once the weak target is collected
  it builds a new wrapper for the same native pointer (ilspy of `Il2CppInterop.Runtime.dll`).
- `SynchronizationService` tags `rocketProjectileInstance.rocket`, a wrapper read once and dropped.
  The interpolator holds the parent `ProjectileRocket`, never the child. After the next GC, the
  `__instance` Harmony passes to `MyFixedUpdate_Prefix` is a fresh, untagged wrapper, so the prefix
  lets the game fly the rocket while the interpolator teleports it.

That makes the miss depend on GC timing, which fits a spam that came and went; the link to which way
the player faced is most likely coincidence.

Fix: `RocketPatches` keeps received rockets in a `HashSet<int>` of `GetInstanceID()`, which belongs to
the native object, filled at the spawn site and cleared with the other per-session state in
`NetworkHandler`. The prefixes check it first and the `DynamicData` tag second. The earlier
`GetMovementDirection` stopgap was dropped as no longer needed.

**2026-10-24 playtest:** the spam is gone on Player2, but so were rocket explosions: the client now
always skips `Rocket.ProjectileDone`, the only place they are drawn, and before the fix they showed
only because the tag was lost. `ProjectileInterpolator.PlayRocketExplosion` now draws one when it
removes a received rocket. Not yet run in game.

The second 2026-10-24 test did not exercise it: Player2's install still had the previous DLL (see
OB-56). A `[rocket] Drew the first explosion for a received rocket.` line now confirms the path ran.

**Third 2026-10-24 test:** Player2 again ran the old DLL (same hash as the 2026-10-23 21:38 build; no
`[item-fx]` line in its log while the host logged one every 10 s), so the client explosion was still
untested. The path it relies on is confirmed by decompiling: the host's `Rocket.ProjectileDone` draws
the explosion, then invokes `A_ProjectileDone`; `ProjectileRocket.Awake` (0x1804F0A10) subscribes
`OnRocketDone` (0x18044C360, folded with `ProjectileHoming.DestroySelf`) to it, which calls
`ProjectileBase.ProjectileDone`, the patch that sends `ProjectileDone`. `ProjectileRocket`'s
`MyFixedUpdate`, `MyUpdate`, `StepMovement` and `FindMovementDirection` are the shared empty stub
0x1803321E0, so the parent ends only through its child. The client therefore receives one
`ProjectileDone` per rocket, and `ProjectileInterpolator` draws the explosion when it removes it.

**Confirmed in play, 2026-10-24 (fourth test, both installs on the same build):** rocket explosions show
on Player2, whose log has `[rocket] Drew the first explosion for a received rocket.` and no
`Look rotation` warnings over two stages. The host logged two, both in the menu before the run.
Local test, 0% loss.

**Same hazard elsewhere, not fixed:** every `DynamicData` tag on a game object is lost the same way
if nothing holds its wrapper. A dictionary holding a *different* wrapper does not help: on clients,
`OnReceivedSpawnedProjectile` tags the `ProjectileBase` component but registers its `GameObject` with
the interpolator, so the `netplayId` that `ProjectileBase.ProjectileDone_Postfix` checks is exposed
too. The likely symptom is a received weapon projectile ending early on the client (UNVERIFIED: not
seen in logs). Interactables tagged on `gameObject` and enemies tagged for `targetId` need the same
check before relying on their tags.

### 2026-10-19 — decompiled: the caller is `Rocket.StepMovement`

- `ProjectileRocket` itself moves nothing (its `StepMovement` / `MyUpdate` / `MyFixedUpdate` overrides
  are the shared empty stub). Its `Rocket` component does: `Rocket.FixedUpdate` (VA `0x1804F3740`)
  calls the virtual `Rocket.StepMovement` (VA `0x1804F44D0`) every physics tick unless paused.
- `StepMovement` checks enemies in radius, calls `Rocket.GetMovementDirection` (VA `0x1804F3830`),
  normalises the result, sphere-casts ahead, moves, and ends with
  **`Quaternion.LookRotation(direction)`**. `GetMovementDirection` homes: it picks a target with
  `EnemyTargeting.GetRandomEnemyInRadius` and steers toward it (Ghidra labels the target field
  `currentDir`, one slot off).
- So the warning fires whenever the direction is zero: the rocket on its target's position, or no
  target and no stored direction. On a client a remote rocket runs this full homing and movement
  **and** `ProjectileInterpolator` teleports it to the host's positions — the split authority the
  entry suspected, now confirmed.
- **Measuring gap:** the bot copy (`D:\Games\Megabonk`) has `[Logging.Disk] WriteUnityLog = false`, so
  Unity warnings never reach its log; a "0" there means nothing. The host copy has it `true` and
  logged 0 in the 2026-10-19 run.

### 2026-10-19 — every old log surveyed; the caller is still unidentified

All 11 logs in the owner's `megabonk_logs` folder (2026-10-08 to 10-18, all with `WriteUnityLog = true`):

| Role | Runs | Warnings |
|---|---|---|
| Host | 6 | **0** every time |
| Client | 5 | 178, 8,961, 4,819, 0, 2,507 |

Every client run with warnings pairs with a host log carrying `SpawnedRocketProjectile` (39–118
`[bw]` samples), and in the 2026-10-18 evening run those rockets were the **client's own** (its end
screen: Slutty Rocket LVL 4; the host's listed it at LVL 0). That fits the design: a client never fires
projectiles itself; the host simulates every weapon and sends each spawn back, the client's own
included (`OnReceivedSpawnedProjectile` takes the local inventory when `OwnerId` is local). So on a
client every rocket is a received one, and the player using rockets sees the most.

**But the obvious caller is already blocked.** `RocketPatches.MyFixedUpdate_Prefix` (since
`91d4673`, 2026-08-04, so in every log above) skips `Rocket.FixedUpdate`, and with it
`StepMovement`'s `LookRotation`, for any rocket carrying the `ownerId` marker a received rocket gets.
`ProjectileInterpolator` guards exact-zero vectors. The remaining call is therefore not proven, and the
entry's rule stands: no fifth patch without the caller.

**Why no log has ever named it:** `LogOutput.log` keeps Unity's message but not its stack, and the mod
turned script stacks on only for Error / Exception / Assert. This warning is a plain Log message, so
even `Player.log` never recorded a caller.

**Next step (built 2026-10-19):** `[Diagnostics] LogUnityMessageStackTraces = true` (with
`LogUnityStackTraces = true`) on the **client** for one rocket-heavy run, then read that machine's
`%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log`: each warning will carry the script stack
that produced it.

**Targeted form of option 2 now available:** a prefix on `Rocket.StepMovement` that returns false for
a *remote* rocket on a client, leaving position and facing to the interpolator (which already guards
its own `LookRotation`). Expiry still runs (`FixedUpdate` checks it after the call). **Cost:** the
client stops running that rocket's own collision, so any hit or explosion visual it produced locally
must come from the host's replicated `ProjectileDone` / hit messages instead. Check that in play.



## OB-26 — The players' Difficulty is not in sync — CONFIRMED by decompile 2026-10-18, **built 2026-10-18 (team average); average confirmed in play, enemy HP fix not yet**

**Reported by the project owner.** Each player's Difficulty stat is built from their own sources and
the two machines disagree on it. The game's formula, from the in-game stat breakdown:

`((baseValue + flatValues) × (1 + additionValues)) × multiplicationValues`

**Sources that raise it** (all per-player): Shrine upgrade, Chaos Tome upgrade, Dicehead's passive,
Cursed Tome, Cursed Grabbies, Skuleg, Sir Chadwell's passive, and Sir Chadwell's +10% base Difficulty.
Character choice alone changes it: in the 2026-10-18 run the client played **Dicehead**, whose
passive raises Difficulty, and the host played Megachad.

### What the code shows

- Difficulty is a per-player stat: `EStat.Difficulty = 38` in `dump.cs`.
- **It is never replicated.** It is not in `PlayerStatsSyncService.TrackedStats` (OB-1's synced stat
  block), and nothing else sends it.
- **Unrelated to `GameBalanceService`'s "difficulty".** That is a player-count tier (`Duo`, `Trio`,
  ...) the mod applies to spawn and HP multipliers; it does not read `EStat.Difficulty`.

### Mechanism — LIKELY, not decompiled

The game reads stats through `PlayerStats.GetStat`, which is static and answers for whichever player
is asking: the same "local singleton" shape behind OB-1, OB-2 and OB-11. In netplay **only the host
spawns and scales enemies**, so the enemies both players fight are most likely driven by the
**host's** Difficulty alone. If so:

- A client's Difficulty sources (Dicehead's passive, a Cursed Tome, Skuleg, ...) change the number on
  the client's stat screen and **do nothing to the run**.
- Picking a Difficulty-raising character or item as a client is a dead choice, which matters more
  where Difficulty also feeds rewards.

**Before fixing, decompile to confirm:** find which game methods read `GetStat(38)` (enemy HP and
spawn scaling are the candidates) and whether any of them run on a client. Also check whether
Difficulty feeds anything besides enemies (rewards, silver, drop rates), since that decides whether
the client's own value matters locally.

### Fix shape — needs a design decision first

1. **Replicate it.** Add `EStat.Difficulty` to `TrackedStats`. No wire change: the stat block is keyed
   by `EStat` as an int, so a new key travels in the existing `PlayerStatsChanged` message.
2. **Decide how players combine.** The host must turn every player's Difficulty into one value for the
   run. The choice is a balance call, not a technical one:
   - **sum** of everyone's extra over base: every source counts, and two cursed players stack;
   - **highest**: one cursed player sets the run for everyone;
   - **average**: a cursed player is diluted by an uncursed one.
3. **Feed it to the game** where the host reads Difficulty for enemy scaling, the same postfix
   pattern OB-1 uses on `WeaponUtility`.

**Confirms fixed as:** a client picking Dicehead or taking a Difficulty source visibly changes enemy
HP and spawns for both players, and the desync detector (add Difficulty to the digest) shows the
combined value agreeing on both machines.


### 2026-10-18 — decompiled: the run is a hybrid of both players' Difficulty

All 128 callers of `PlayerStats.GetStat` (VA `0x18042B6A0`) decompiled; **11 read Difficulty**
(`GetStat(0x26)`). None touch rewards, silver, XP or drops:

| What it scales | Readers | Runs on | So it follows |
|---|---|---|---|
| Enemy HP | `EnemyStats.GetHp`, `CombatScaling.GetHpMultiplierAddition`, `GetStageHpMultiplier` | host (spawns enemies; clients get HP in `SpawnedEnemy`) | **host only** |
| Enemy speed | `EnemyStats.GetSpeed` ← `Enemy.GetSpeed` | host (simulates movement; clients interpolate) | **host only** |
| Knockback resistance | `EnemyStats.GetKnockbackResistance` | host (applies hits) | **host only** |
| Spawn count and rate | `BaseSummoner.GetMultiplier`, `StageSummoner.GetNumTargetEnemies`, `GetSummonInterval`, `SwarmSummoner.GetNumTargetEnemies` | host (only the host spawns) | **host only** |
| **Damage enemies deal to a player** | `EnemyStats.GetDamage` ← `DamageUtility.GetPlayerDamage`, enemy special attacks | **the victim's own machine** | **each player's own** |
| Achievements | `AchievementTracker.OnStatUpdate` | each machine | local, harmless |

Damage to a player is computed on the victim's machine: health is owner-authoritative (each owner's HP
is copied onto their avatar in `OnPlayerUpdate`), and no host path damages a teammate's avatar.

Difficulty enters additively in both decompiled formulas: HP as `difficulty x k + ... + 1`, damage as
`(difficulty + ... + 1) x ...`.

**So the real behaviour:** enemies are as tough and as numerous as the **host's** Difficulty says, but
each player is hit as hard as **their own** Difficulty says. A client who picks Dicehead or a Cursed
Tome takes more damage and gains nothing in enemy numbers or toughness; a host who does raises
enemy toughness for both while only the host takes harder hits.

**Fix shape, unchanged in principle:** replicate `EStat.Difficulty` in `TrackedStats` (no wire change),
then on every machine replace the value these readers see with one combined value, so enemies and
incoming damage agree. Postfix the 11 readers' `GetStat(0x26)` via a single patch on
`PlayerStats.GetStat` filtered to stat 38 -- every reader goes through it. The combining rule
(sum of extras, highest, or average) is the decision still pending.

### 2026-10-18 — built: team **average** for the shared world, own value for incoming damage

The combining rule was decided as the **mean** of every connected player's Difficulty (why:
[`16-session-handover.md`](16-session-handover.md)). The earlier "every machine, all 11 readers"
shape above is superseded: incoming damage stays per-player on purpose.

- `EStat.Difficulty` added to `PlayerStatsSyncService.TrackedStats` — travels in `PlayerStatsChanged`
  (tag 77), no wire change.
- `Patches/TeamDifficulty.cs`: a prefix/finalizer scope on the nine host readers, and a postfix on
  **`PlayerStatsNew.GetStat`** (not `PlayerStats.GetStat`) that hands the team mean to stat 38 only
  inside that scope, on the host, in netplay. Decompiled at `0x18042B6A0`, `PlayerStats.GetStat` is a
  direct forward to the local player's `PlayerStatsNew.GetStat`, which the mod already detours — so
  this adds no new trampoline to the hottest stat path.
- `EnemyStats.GetDamage` and `AchievementTracker.OnStatUpdate` are deliberately outside the scope.
- A teammate whose block has not arrived is left out of the mean; dead players are kept.
- Host log on every change of the mean, including the first:
  `OB-26: team Difficulty 0.20 (host 0.00, <id> 0.40) over 2 players`.

### 2026-10-18 evening — the average works; one hook broke enemy HP on the host

**The averaging is confirmed in play.** The host log tracks both players and the mean is right at
every change: `0.00`, then `0.03 (host 0.00, client 0.05)`, up to `0.18 (host 0.21, client 0.15)`.

**The hook on `CombatScaling.GetHpMultiplierAddition` broke that method.** It takes three
`out float` parameters, and with a prefix and finalizer on it the native-to-managed trampoline threw
a `NullReferenceException` on **every** call before reaching mod code: **2,659** on the host, with
no managed frame in the stack. A trampoline that throws hands the game a default `0`, and
`EnemyStats.GetHp` (decompiled) computes `hp × (1.1·D + addition + 1)`, so every enemy the host
spawned lost its time, swarm and stage scaling. **That is the boss-health gap**: the Desert final boss
had **52.0K** on the host against **5.0M** on the client. It is not the averaging (`D` is at most 0.2
here); it is the missing `addition`.

**Fixed** by removing that one hook. Its only decompiled caller is `EnemyStats.GetHp`, which keeps its
own scope, so the team value still reaches it. Callers outside `GetHp`, if any, see the host's own
Difficulty. **UNVERIFIED**, not decompiled.

**Lesson for any future hook:** a prefix or finalizer on a method with `out`/`ref` value-type
parameters needs one run to prove its trampoline works before anything depends on it.

**Still UNVERIFIED until played:** that the nine readers' `GetStat(0x26)` reaches `PlayerStatsNew.GetStat`
through `PlayerStats.GetStat` at run time (the decompiles show direct calls, not inlined bodies); that
HarmonyX applies the finalizers on these static IL2CPP methods; and the felt effect on enemy
toughness and spawns. **Confirms fixed as:** the host log line appears with the mean of both players,
and swapping who plays Dicehead leaves enemy toughness unchanged for the same pair of builds.

<a name="ob-27"></a>

## OB-27 — Character select throws every frame once a friend joins — **FIXED, CONFIRMED by log 2026-10-20**

**Reported 2026-10-18 (evening run).** "So many errors as soon as my friend joined." The host logged
**4,362** `NullReferenceException`s, all from one line, `WindowManagerPatches.Update_Postix`
(`WindowManager.cs:181`), starting the frame the host advanced both peers to character selection and
running until the host left the lobby. The run never got past character select. The client threw
once, in `CreateFriendliesInfoDisplay` (`WindowManager.cs:229`) as the same window opened.
`Player.log` held no errors, so the fault was ours alone.

**Cause — LIKELY.** Both sites read `b_confirm` off `window as CharacterMenu`. A C# `as` only checks
the managed wrapper, and for a value typed `Window` Il2CppInterop often hands back a plain `Window`
wrapper over a native `CharacterMenu`, so the cast returns null. `UiAssetService.ExtractPrefab`
already documents the same trap for `GameObject`. Whether a derived wrapper is cached depends on what
fetched the object earlier, which fits the facts: same game build (21750826, version 1.0.69), same
startup and lobby path as the earlier 2026-10-18 run that passed character select, and none of this
session's code touches the menu. Not proven: no log says which of the two nulls it was.

**Fix.** `Helpers/WindowCast.AsCharacterMenu` falls back to `GetComponent<CharacterMenu>()`, which
resolves the type on the native side. All seven `CharacterMenu` casts use it (`WindowManager.cs` x4,
`Plugin.cs` x2 and `LobbyPanel.cs`, the last of which silently skipped preselecting a character), and the
per-frame site now returns instead of throwing when either is null.

**Confirms fixed as:** a friend joins, both reach character select, and neither log has a
`WindowManager.cs:line` exception; the host's confirm button greys out alone and lights up with two.

**Confirmed by log, 2026-10-20 21:01 run (host plus two bots, Forest stage 1, ended in a game over before any stage boss died).** Three peers reached character selection together (`[lobby] Host started the lobby; advancing every peer to character selection.`), and no peer logged a `WindowManager.cs` exception. The confirm button's grey-out with one player was not reported either way.

<a name="ob-28"></a>

## OB-28 — The client's minimap throws every frame after the boss dies — **FIXED, CONFIRMED by log 2026-10-21**

**Seen in the 2026-10-18 evening client log**, never before in any log: **948**
`ArgumentException: An item with the same key has already been added. Key: BossSpawner(Clone)`,
from just after "Boss defeated, activating portal." on stage 2 until the stage ended.

**Cause, from the decompiles.** `MinimapCamera.AddArrow` (VA `0x1804B16F0`) ends in an unchecked
`arrowDict.Add`. `TrySpotBossSpawner` (VA `0x1804B2AE0`) calls it once the boss spawner is inside the
minimap's range and sets `bossSpotted` only after it returns. The boss-defeated path had already
added the spawner's arrow, and the client walked into range only after the host killed the boss.
So `Add` threw, `bossSpotted` was never set, and it threw again every frame. Field names were
confirmed against `dump.cs` (`bossSpotted` 0x50, `bossSpawner` 0x58, `arrowDict` 0x90); Ghidra's
labels were off by a slot again.

**Fix.** A prefix on `AddArrow` skips a target already in `arrowDict`, so `TrySpotBossSpawner` sets
its flag and stops asking. **Confirms fixed as:** no `same key has already been added` lines.

**Confirmed by log, 2026-10-21 Endless run (host plus two bots; Graveyard start, nine stage changes across Forest, Desert and Graveyard, with Ent and the Ghost King as boss rooms). The host's log was overwritten by a relaunch, so this rests on both bots' logs.** Stage bosses died on several stages (`Boss defeated` on both bots), and neither bot logged `same key has already been added`.

<a name="ob-29"></a>

## OB-29 — The Shady Guy counter differs between host and client — **CLOSED 2026-10-18: per-player by design, not a bug**

Both HUDs on 2026-10-18 evening: host `Shady Guy 1 / 2`, client `2 / 2`. The client's desync detector
flagged it as soon as it happened (`counter 'Shady Guy' host 0 / local 1`, then `1 / 2`). Same family
as OB-12, whose fix covers the other counters; the Shady Guy path has not been traced.

**Closed by the project owner.** The host had no money to buy anything from the second shady guy,
while the client bought from it. A player who has not used a shady guy keeps it available, so the
host's `1 / 2` is telling the host there is still one to visit. That matches the decompile noted in
`InteractableCounterHelper`: the game counts a shady guy in `OnShadyGuyUsed` when that player's own
shop window closes, not when anyone interacts with it.

**Desync detector change.** `DesyncDetectorService` now skips the `Shady Guy` row
(`IsPerPlayerCounter`), because comparing a per-player count across peers can only produce false
warnings.

<a name="ob-30"></a>

## OB-30 — Enemies vanish and reappear past a certain distance — **FIXED, CONFIRMED in play 2026-10-20**

**Reported by the project owner.** In netplay, enemies disappear once they are some distance away
and pop back in when they come closer. It looks like a render distance or LOD cutoff.

### Likely cause — the mod hides them, not the game

`Helpers/DistanceThrottler.cs`, added in `ffb8c44` (2026-02-08, "Distance throttler for enemies"),
runs from `EnemyPatch.MyUpdate_Prefix` and `MyFixedUpdate_Prefix` for every non-boss enemy, on
**both** host and client, only in netplay:

- `Plugin.GetDistanceToPlayer` measures from the local player, or from the spectate target while dead.
- **Close** `< 25` (`Tunables.DISTANCE_CLOSE`): updates every tick, renderer on.
- **Medium** `25–60`: renderer on; on a client, updates throttled to once per second
  (`MEDIUM_DISTANCE_UPDATE_INTERVAL_SECONDS`).
- **Far** `≥ 60` (`Tunables.DISTANCE_MEDIUM`): **`renderer.enabled = false`**. On a client the
  enemy's update is skipped too; the host keeps simulating it.
- Back inside 60 the renderer is switched on again.

That is exactly "vanishes past a distance, reappears when closer", and the 60-unit cutoff is a hard
edge with no hysteresis, so an enemy moving along the boundary flickers.

### Details that make it worse

1. **One renderer only.** `SetRendererEnabled` toggles `GetComponentInChildren<Renderer>()`, the
   first renderer found. An enemy built from several renderers (body plus weapon, elite glow, shadow)
   would lose only part of itself.
2. **60 units is short for this game's camera.** The camera can see well past 60 units across open
   ground, so the cutoff is inside the visible area rather than beyond it. **UNVERIFIED**: the
   camera's actual visible range has not been measured.
3. **Measured from one point.** Distance is from the local player, not the camera or its view, so an
   enemy in plain sight down a slope or across a gap can be "far" and hidden.
4. **Bosses are exempt** (`IsBoss` / `IsStageBoss` / `IsFinalBoss` return early), which fits
   bosses never being reported as vanishing.

### What would confirm it

- It happens only in netplay, never in singleplayer (the throttler is behind
  `HasNetplaySessionStarted`).
- Enemies vanish at roughly the same distance every time, about 60 units, on both host and client.
- Bosses never vanish.

### Fix options (not chosen)

- **Stop hiding renderers** and keep only the update throttling. The renderer toggle saves draw calls
  but is the visible part; the update skip is where the CPU saving is.
- **Raise the cutoff** past the camera's visible range, after measuring it.
- **Add hysteresis** (for example hide at 70, show at 60) so the boundary cannot flicker.
- **Use the camera's view**, not distance from the player, to decide what to hide.

The update throttling on a client is a separate question: a skipped `MyUpdate` on a far client-side
enemy may be harmless because the host simulates it and the client interpolates, but that has not
been checked.

### 2026-10-19 — fixed: stop hiding renderers, keep the update throttling

Chosen by the project owner. `DistanceThrottler` no longer touches any renderer: the `Far` and
`Medium`/`Close` branches now only decide whether the update runs. The CPU saving (skipped
`MyUpdate`/`MyFixedUpdate` on a client) stays; the draw-call saving goes, and Unity's frustum
culling still skips anything off-screen.

**Client enemies past 60 still move.** Checked rather than assumed: their position is set by
`EnemyInterpolatorManager.Update`, which ticks every registered `EnemyInterpolator` whatever its
distance, not by the skipped `MyUpdate`. **UNVERIFIED:** whether anything visible besides position
(facing, animation state, hit flashes) depends on `MyUpdate` and so now shows as frozen on a far
enemy.

**Watch for in play:** host and client FPS in heavy waves (more enemies drawn), and whether far enemies
on the client look frozen apart from moving.

**Not changed:** projectiles have their own far-distance handling (zero opacity, `ffb8c44`), which was
not part of this report.

**Confirmed in play 2026-10-20** by the host player: distant enemies no longer vanish and reappear. Nothing logs this, so the confirmation is visual.

<a name="ob-31"></a>

## OB-31 — Levels drift apart at a stage transition, and never come back — **FIXED, CONFIRMED by log 2026-10-21**

**Reported 2026-10-19** (owner hosting, test bot as client): "level discrepancy which makes my gameplay
not smooth". Every level-up opens a shared-experience reward window for both players, so a peer that
is ahead makes the other sit through extra pauses.

**Evidence.** The client's desync detector: one level apart off and on through the run (27 / 28 up to
39 / 40, re-converging), then **39 / 40 before the final arena's ready barrier and 42 / 47 after it**,
for the rest of the run. The one-level wobble is gains in flight near a threshold; the jump is not.

**Cause — same hole as OB-8.** `PlayerXpPatches.AddXp_Postfix` opened with
`if (!HasNetplaySessionStarted()) return;`. During a stage transition's ready barrier the session is
not Started, so every XP gain in that window stayed local. Shared XP replicates gains, not totals
(see OB-20 and the shared-XP notes), so a gain that is never sent is never recovered.

**Fix.** The postfix gates on `HasNetplaySessionInitialized`, so barrier-window gains are sent. The
receiver may have no player yet while its scene loads; such a gain is held (`pendingSharedXp`) and
added with the next one that lands, and cleared on session reset.

**UNVERIFIED:** what produces the XP inside the barrier (likely the stage ending, LIKELY not
decompiled). **Confirms fixed as:** no `level host N / local M` lines after a stage transition.

### 2026-10-19 — why a level gap blocks the run, and re-convergence

**Reported by the owner:** with levels apart, one player sits on "Waiting for other player(s)" after
every level-up, until the shared-experience failsafe releases it, or until the other reaches the same
level, which a five-level gap never does.

**Mechanism: SE-6.** The barrier expects every reward window on every peer (shared XP normally makes
both level up together), and `EncounterService.IsClosable()` waits for a report from every player. A
peer that is behind has no level-up of its own, so no window and nothing to report; only a dead player
auto-reports (`PopReward_Prefix` / `AddEncounter_Prefix`). Each extra level-up on the peer that is
ahead therefore waits the full failsafe.

**Fix: re-converge the totals instead of tolerating drift.** `AddXp` already carries the sender's
total (`Xp`) next to the gain. On receipt the gain is added and, if the sender's total is still
higher, ours is raised to it (`[xp] behind the shared total by N; raising ...`), then `AddXp(0)` runs
the level-up check. It cannot over-count: the sender's total is a sum of real gains and can only lack
some of ours. Because gains arrive `ReliableOrdered`, ours is only ever below it after a genuine loss,
so the line marks exactly the drift cases. Any lost XP is recovered on the next message, so a gap
lasts one pickup, not the run.

**SE-6 itself stays open:** a player who legitimately has no window (any future asymmetric reward)
would still hold the barrier until the failsafe. Fixing that needs per-round participant lists.


### Related, not a mod bug: the bot could not finish a revive fight

Same run, third revive at the final stage: the bot pressed the coffin, then stayed `Reviving` until
the end. The new `[boss]` lines show the revive ghost **in sync** (same id, HP and position on both
peers, all three ghosts); the bot was parked at the coffin by design while the ghost wandered ~40 u
away, out of its weapons' reach (5.75M HP, about 4k lost a minute). Fixed in the bot: it now chases
the ghost during the revive fight. Separately worth a look: the ghost's HP scales with run time and
revive count (`RebalanceIfNeededReviverEnemy`: x1/6, x1/3, x1/2 ...), 5.75M at the third final-stage
revive, which may be a lot for one surviving player.

**Confirmed by log, 2026-10-21 Endless run (host plus two bots; Graveyard start, nine stage changes across Forest, Desert and Graveyard, with Ent and the Ghost King as boss rooms). The host's log was overwritten by a relaunch, so this rests on both bots' logs.** Nine stage changes passed with no `level host N / local M` line and no `[xp] behind` line, and every digest stayed in sync.

<a name="ob-32"></a>

## OB-32 — A Desert tumbleweed spawns on both peers in every stage's ready barrier — **FIXED, CONFIRMED in play 2026-10-21**

Found by an audit, not a report: after OB-8 and OB-31, every Harmony patch that enforces host
authority and opens with `if (!HasNetplaySessionStarted()) return true;` was checked for whether game
code can reach it while a stage loads or the ready barrier is up.

### 2026-10-19 — the window, and what can run in it

`HasNetplaySessionStarted()` is `currentState == State.Started`, nothing more. A session is therefore
"not started" in `Loading` (map generation), `Ready` (the barrier, "Lobby not ready yet, waiting..."),
`LoadingNextLevel` (portal entered, next scene loading) and `Endgame`. In those states a patch with
that guard runs vanilla on **both** peers, and a host-only send in its postfix stays silent.

Decompiled, what does and does not keep running in the barrier:

- **`MyTime.Pause` (VA `0x1803E5B20`) only sets `MyTime.paused` and switches physics to script
  mode.** It does not touch `Time.timeScale`, so every MonoBehaviour `FixedUpdate`, `Update` and
  coroutine keeps running. Physics (triggers, collisions) does not.
- `MyTime.Update` (VA `0x1803E5E80`) stops advancing `MyTime.time` and `stageTimer` while paused, but
  **`MyTime.time` is never reset per stage** (`OnNewStageStarted`, VA `0x1803E5940`, resets
  `stageTimer`, not `time`). The client log shows `t=30.83` inside the first barrier.
- `EnemyManager.FixedUpdate` (VA `0x180419820`) returns at once while `MyTime.paused`, and it is the
  only caller of `SummonerController.Tick`, which drives every summoner and the stage timeline. So no
  summoner spawns, timeline miniboss or stage boss can happen in the barrier.

### The proven case: `EffectManager.SpawnTumbleWeeds`

1. `MapEventsManager.Start` (VA `0x180499F00`) creates a `MapEventsDesert` when the current map is
   Desert, and `MapEventsManager.FixedUpdate` (VA `0x180499EC0`) calls its `Tick` (vtable slot 4) on
   every physics step.
2. `MapEventsDesert.Tick` (VA `0x180473EB0`) does nothing on the final arena (`MapController.isFinalBossStage`),
   and otherwise calls `EffectManager.SpawnTumbleWeeds(1)` when
   `lastSpawnedTumbleweedTime + tumbleweedSpawnInterval <= MyTime.time`. The constructor (VA
   `0x180474010`) sets the interval to 2.2 and leaves `lastSpawnedTumbleweedTime` at 0.
3. With `MyTime.time` already past 2.2, **the first tick of every Desert stage spawns one tumbleweed
   at once**, inside the barrier, on both peers. `SpawnTumbleWeeds_Prefix` returned true before
   Started, and `InteractableTumbleWeed.OnEnable_Postfix` (the host's `TumbleWeedSpawned` send) is
   silent before Started.
4. Result: the host's tumbleweed is never replicated, and the client keeps a local one of its own,
   which freezes when the session starts (`FixedUpdate_Prefix` returns false for a client) and can
   never despawn (`Despawn_Prefix` returns false for a client).

**The log agrees.** The 2026-10-19 host log has exactly one
`TumbleWeed not found in SpawnedObjectManagerService when processing OnTumbleWeedDespawned.` after
each of the three Desert stages' barriers, and none after the final arena's — the one stage where
`Tick` returns early. The 2026-10-18 host log has it after two of its three Desert stages; the line
appears only when that tumbleweed despawns, so a stage that ends first logs nothing.

**Fix (`da6f2f0`).** `SpawnTumbleWeeds_Prefix` gates on `HasNetplaySessionInitialized`: a client in a
session never spawns one (it instantiates the host's from `TumbleWeedSpawned` directly, not through
this method); the host drops a spawn before Started. Dropping is enough: `Tick` has already moved
`lastSpawnedTumbleweedTime` on, so the next one comes 2.2 s after the barrier releases and goes
through the replicated path.

**UNVERIFIED:** not run in-game. **Confirms fixed as:** no `TumbleWeed not found ...
OnTumbleWeedDespawned` line in a Desert run's host log, and no motionless tumbleweed on the client.

### The audit

Every host-authority patch whose guard is `HasNetplaySessionStarted()`. "Barrier" means any of the
not-Started states above. VAs are from `megabonk-re/build-21750826/dump.cs`.

| Patch | Can it fire before Started? Evidence | Fix |
|---|---|---|
| `FinalFightController.SpawnBoss_Prefix` | **Yes** — the arena cinematic calls it ([OB-8](#ob-8)). | Fixed in `272e887` (OB-8). |
| `EnemyManager.SpawnBoss_Prefix` | **Yes**, through the one above ([OB-8](#ob-8)). | Fixed in `272e887`: client gated on Initialized, host logs a pre-Started spawn. |
| `PlayerXp.AddXp_Postfix` | **Yes** — XP gained in the barrier ([OB-31](#ob-31)). | Fixed earlier (OB-31). |
| `EffectManager.SpawnTumbleWeeds_Prefix` | **Yes** — see above. CONFIRMED by decompile and the host log. | **Fixed in `da6f2f0`.** |
| `InteractableTumbleWeed.OnEnable_Postfix`, `Despawn_Prefix`, `FixedUpdate_Prefix` | Only for a tumbleweed that exists before Started, and the spawn above was the only source. | None needed once the spawn is gated. |
| `EffectManager.SpawnTornadoes_Prefix` / `_Postfix` | **Yes, and harmless.** `MapEventsManager.Start` → `MapEventsDesert.Init` (VA `0x1804734E0`) → `SpawnTornadoes(25)`, once per Desert stage at load, on both peers. Raw bytes at VA `0x180501BA0` read `mov rdi,[rbp+0x118]`, the `tornado` prefab (Ghidra prints `tumbleweed`, one slot off). | None. `TornadoesSpawned` carries only a count and the client instantiates its own, so both peers end with 25 local tornadoes either way. `Init` is the only caller, so the replicated path never runs in practice. |
| `DesertStorm.FadeIn_Prefix` / `FadeOut_Prefix` | No. Only from `MapEventsDesert.TickStorms` (VA `0x180473C10`), which compares `nextStormTime` with `MyTime.stageTimer`; that does not advance while paused. LIKELY: a stale `stageTimer` before `OnNewStageStarted` resets it is not ruled out (call order not decompiled). | None. |
| `EnemyManager.SpawnEnemy_Prefix` / `_Postfix`, `ChallengeSummoner.SpendCredits_Prefix` | No. Callers: the four `SpendCredits` (summoners, under `EnemyManager.FixedUpdate`, paused), `FinalFightController.SpawnGoons` (under `SpecialAttacks`, below), `SpawnBoss` (OB-8), `InteractableEgg/Gravestone.Interact` (player interaction). | None. |
| `FinalFightController.SpecialAttacks_Prefix`, `SpawnOrbs*_Prefix` / `_Postfix` | No. Only from `FinalFightController.FixedUpdate` (VA `0x180492750`), which does nothing while `boss` is null (Ghidra labels the slot `pylons`), and after OB-8 the boss does not exist before Started on either peer. | None. |
| `FinalFightController.StartPylons` (seed and OB-7 ids) | No. Callers `OnEnemyDamage` and `StartPhase` (data reference only) need the boss. The 2026-10-19 host log has `OB-7: registered 11 pylon(s)` after the final barrier released. | None. |
| `EffectManager.CheckChestSpawn_Prefix`, `ExploderEnemy_*`, `SpawnPickupOrb_*`, `PickupManager.SpawnPickup_Prefix` | No. Callers are enemy death (`EffectManager/PickupManager.OnEnemyDied`), pots, tumbleweed interaction and `PickupOrb.OnCollisionEnter`. No enemy exists before Started (summoners paused, boss held), and collisions need physics. | None. |
| `SpecialAttackController.UseSpecialAttack_Prefix`, `InteractableCharacterFight.SpawnEnemy_Prefix` | No. `UseSpecialAttack` only from `SpecialAttackController.Tick` (an enemy's); `InteractableCharacterFight.SpawnEnemy` has data references only (an interaction). | None. |
| `ChargeShrine.Complete_Prefix`, `ChargeShrine/BossPylon/BossLamp.OnTrigger*` | No. `Complete` only from `ChargeShrine.Update` once charged, and charging and triggers need a player in a trigger, which needs physics. | None. |
| Enemy, movement, projectile, weapon and aura patches (`Enemy.*`, `EnemyMovementRb.*`, `Projectile*`, `Rocket.*`, `WeaponInventory.Tick`, `CombatAura`, `PickupOrb`, `DetectInteractables.TryInteract`) | No world state for them to act on: no enemy exists before Started. UNVERIFIED per method — not decompiled one by one. | None. |
| Map generation and placement (`GenerateTileObjects`, `SpawnInteractables`, `RandomObjectPlacer`, `MapGeneration/*`, `EffectManager.OnMapGenerationComplete`, `InteractableMicrowave/ShadyGuy.Start`, `BossLamp.Awake`, `GraveyardBossRoom.Activate`, `UnityObject.Instantiate` boss spawner) | Yes, by design — and these already gate on `HasNetplaySessionInitialized`. | None. |
| `UnityObject.Instantiate` open-chest gate | No. The open-chest effect follows a chest interaction. | None. |

**Not covered by this audit:** the `LoadingNextLevel` window (portal entered, old scene still
alive for a few frames) and `Endgame` are also not-Started, so the same guard opens there; nothing
there was looked at.

**Confirmed, 2026-10-21 Endless run (host plus two bots, eleven stage changes, including Desert rooms and the Ent and Pharaoh arenas; the host's log was kept).** The host's log has no `TumbleWeed not found ... OnTumbleWeedDespawned` line across several Desert rooms, and the host player saw no motionless tumbleweed.

<a name="ob-33"></a>

## OB-33 — Steam transport: after the host leaves and re-hosts, the other player cannot join until they restart — **FIXED 2026-10-19, not yet confirmed in play**

**Reported 2026-10-19 by the project owner and their friend** (`UseSteamTransport = true`). A hosts and
B joins with no trouble. A leaves the lobby and hosts a new one: B cannot join it until B restarts
the game. The same happens the other way round.

**Cause, from the code** (no log of the failing session in hand):

1. When the host leaves a Steam lobby, the remaining member stays in it and Steam hands them
   ownership. `SteamLobbyService.RefreshMembersIfDue` recorded the new owner and nothing reacted, so
   B stayed `InLobby` in a lobby with no host.
2. B's session can end without passing through `NetworkHandler.ResetNetworking`, the one teardown that
   leaves the Steam lobby. Its comment already names this exact symptom ("Refusing to join a lobby
   while InLobby ... stuck until they restarted"), fixed then for backing out of the panel only.
3. The panel's Join path (`LobbyPanel.SwitchSession`) calls `INetplaySessionService.Cancel` first, and
   `Cancel` returns before it leaves the lobby once the session is `Idle`.
4. `CreateLobby`, `JoinLobby` and `JoinByCode` all refuse while `InLobby`, so every later host or join
   failed until a restart reset the service. That is why it happens in both directions.

**Fix.** `SteamLobbyService.LeaveStaleLobby`: `CreateLobby`, `JoinLobby` (to a different lobby) and
`JoinByCode` (for a different code) now leave a lobby this peer is still in before going on, logging
`[steam-lobby] Still in lobby N (owner …, M member(s)); leaving it before …`. The mod has no host
migration, so a lobby being replaced is always finished with. The presence bridge only creates from
`None`, so it is unaffected. Also new: `[steam-lobby] Lobby N owner changed A -> B (now ours: …);
the host left.`, so the next log shows the moment it happens.

**UNVERIFIED:** that `SteamNetSessionService` (which shuts the transport down when lobby membership
ends) has torn the old session down before the new join's `AwaitSteamSession` runs; leave and join are
now back-to-back. **Confirms fixed as:** A hosts, B joins, A leaves and hosts again, B joins without
restarting — both directions — and B's log shows the owner-changed and leaving-before lines.

<a name="ob-34"></a>

## OB-34 — Item hits echoed between peers until the enemy died; one client never saw the final boss die — **FIXED, CONFIRMED by log 2026-10-21**

**Reported 2026-10-19** (host plus two test bots, host on a god-mode testing plugin): the final boss died
for the host and player 2, but player 3 kept a boss bar at 14.8K / 8.4M and the objective unticked.

**Evidence.** The `[boss]` lines agree on all three peers (same id 442, same HP) until player 3's copy
drifts: same HP but the position climbs to (-116, 122, 22) and freezes there at 14,751 HP with no death.
At that moment the bandwidth lines show the cause:

| Peer | `EnemyDamaged` sent | Payload |
|---|---|---|
| Host | **~33,000 / s** (to all clients, unreliable) | 2,623 KB/s |
| Player 2 (client) | **~16,800 / s** (to host, reliable) | — |
| Player 3 (client) | **~24,000 / s** (to host, reliable) | 1,235 KB/s |

Earlier runs had a client sending 0.1–4.4 per second. Player 3 also got max-HP rescales for enemies
whose spawns had not arrived and disagreed on the enemy count (host 1 / local 5): its connection was
saturated, the boss's death never landed, and the host eventually dropped it.

**Cause: an echo loop.** A client forwards its own item hits (`Enemy.Damage_Postfix`, any source in
`AllowedDamageSource`). The host applies each one and forwards it to every client, and a client
replays it through `Enemy.Damage` in `OnReceivedEnemyDamaged`, whose postfix saw an item source and
forwarded it again as new. Nothing on that path checked `CAN_SEND_MESSAGES` or suppressed outbound.
With two clients a hit ping-pongs through the host until the enemy dies, and applies again every lap,
which also inflated item totals (player 2's Cursed Doll: 11.5M).

**Fix.** `Damage_Postfix` on a client returns when `CAN_DAMAGE_ENEMIES` is set, which happens only
inside a replay (`OnReceivedEnemyDamaged`, its single setter). A client's own item hits are forwarded as
before; the host still forwards client hits to the other clients.

**Confirms fixed as:** three-player run, client `[bw] EnemyDamaged` back to single or low double digits
per second, the final boss's death reaching every client, and item totals on the end screens in line
with the item.

**Separately still heavy:** the host forwards every hit it applies to every client, one message per hit.
At god-mode damage that alone is tens of thousands per second; aggregating per enemy per tick is the
lever (see `17-enemy-cap.md`).

### 2026-10-19 — host `EnemyDamaged` summed per enemy per tick — **UNVERIFIED, not run in-game**

**Change.** The host no longer sends one `EnemyDamaged` per hit. `EnemyDamageAggregator` sums the hits
it applies per **(enemy, attacker, damage source, flags)** and `NetworkHandler` flushes each sum once
per enemy tick (`Tunables.ENEMY_UPDATE_TICK_RATE`, 40 Hz), just before the enemy delta. The message
and its fields are unchanged. Damage and armor-blocked damage are summed, crit is kept if any hit
critted, knockback is the largest, effect, element and proc coefficient are the first hit's. Clients'
own sends to the host are unchanged.

**Why a sum is safe.** Decompiled on build 21750826: `Enemy.Damage` (VA `0x1804AC7B0`) scales the
damage by a player stat and by `(1 − armor)` unless `(flags & 5) == 5`, subtracts it from HP and clamps;
`RunStats.OnEnemyDamaged` (VA `0x180411930`) adds it to the source's total. Both are linear, so one
replay of the sum gives the same totals as N replays. The flags pick the armor treatment, which is
why they are part of the key. HP is superseded by `EnemyModel.Hp` on the delta either way.

**What changes on a client.**

- **Damage numbers.** `Enemy.Damage` calls `EffectManager.NewDamageNumbers` once per replay, so a
  client sees one number per bucket per tick, showing the sum, instead of one per hit. A few large
  numbers instead of a stream of small ones; they can lag a hit by up to one tick (25 ms).
- **Damage panel (`RunStats`).** Same totals, since it sums. The attacker is part of the key, so
  OB-14's attribution holds per bucket.
- **`EnemyDied` is unchanged** and still `ReliableOrdered`. Before it sends one, the host flushes that
  enemy's pending buckets. A client drops a hit for an enemy it has already removed, so without that a
  swarm enemy killed within one tick would reach no client's damage panel. The killing hit itself was
  never forwarded and still is not: `OnEnemyDied` unregisters the enemy before `Damage_Postfix` runs.
- **Loss.** A dropped packet now loses a tick's sum for one bucket rather than one hit. The expected
  loss is the same, but individual losses are larger. HP is unaffected.

Pending buckets are dropped on a stage change (`PrepareForNextLevel`) and a session reset.

**Measure.** With `LogBandwidth` on, the host's `[bw]` report has a new line:
`EnemyDamaged aggregation: H hits/s -> M msgs/s, R hits per message`.

**Check in a three-player run** (host + two bots, `LogBandwidth` on all three, a late-game swarm or
the god-mode plugin):

1. Host `[bw]`: the `EnemyDamaged` row's `/s` against the aggregation line. **M** should match that
   row. **R** should be well above 1 at high damage. At the 2026-10-19 rate (~33k hits/s) expect M in
   the low thousands at most, roughly 40 Hz × the number of enemies being hit at once.
2. Host `[bw]` total KB/s compared with 2026-10-19's 2.6 MB/s at the same moment.
3. Clients: damage numbers still appear on enemies the host and other players hit, fewer and larger.
   Nothing looks missing.
4. End screens: each client's damage panel for its own weapons is in line with the host's, as it was
   before.
5. Final boss and stage boss deaths reach every client (OB-34's original symptom), and no client's
   `[boss]` HP drifts from the host's.

**Seen while reading (UNVERIFIED, from code):**

- **A client's item hit reached third peers twice — fixed 2026-10-19.** On the host,
  `NetMessageRouter` rebroadcast a client's `EnemyDamaged` with `SendToAllClientsExcept(attacker)`
  (ReliableOrdered), and the host's own replay of that hit went through `Damage_Postfix` →
  `OnEnemyDamaged`, which forwards every hit it applies to every client. The origin got one copy,
  each third peer two. The damage panel was not doubled: OB-14's `RunStats` prefix drops a
  teammate's hits. The cost was two damage numbers per hit, plus a correctness risk. A replay that
  drives the client's copy to 0 HP kills it, and `EnemyDied_Postfix` reports that death to the host,
  so a doubled hit could end an enemy early for everyone. The rebroadcast also forwarded hits the
  host never applied (paused by OB-13's gate, or already dead on the host). The router now only
  applies the hit; the host's aggregated forward reaches every client, the origin included. The
  origin needs that copy, because a client never applies its own hit. Confirms fixed as: in a
  three-player run, a bot's item hit shows one damage number per tick on the other bot, not two,
  and the host's `[bw]` `EnemyDamaged` sends/s equal the aggregation line's msgs/s. Before, the
  rebroadcast added its own sends to that row.
  Low rate since 3035c49, so this is hard to see by eye.
- **Armor may be applied twice on a client.** `Enemy.Damage` writes the scaled damage back into the
  container, so the postfix forwards the post-armor value and the client's replay scales it again.
  Clients would then show smaller numbers and damage-panel totals than the host on armored enemies.
  Not a new issue; aggregation neither causes nor fixes it.

## Confirmed in play 2026-10-18, outside the numbered entries

- **Shared XP sent as gains** (no more level drift; see OB-20's side effect). No `level` disagreements.
- **A dead player's aura** no longer deals damage — nothing observed; closed by the project owner.

## Not in this file

- Client `NullReferenceException` storm — live lead in
  [`12-session-handover.md`](12-session-handover.md). **2026-08-07 narrowed it decisively:** with
  `WriteUnityLog = true` on *both* peers for the first time, the host recorded **0** NREs during the
  whole run against the client's **8,422**, while the host's Unity channel was demonstrably live
  (29 `Can not play a disabled audio source` warnings spread across the session). The asymmetry is
  now measured rather than assumed. `[unity-exc]` confirms `SetStackTraceLogType(..., ScriptOnly)`
  was applied on both peers and **still no managed frame appears**, which resolves the open
  either/or: the thrower is native game code, not managed mod code.
- Remote projectile / rocket representation — now tracked as [OB-25](#ob-25), with evidence and fix options.
- ~~Host BepInEx logging no Unity-sourced lines~~ — **fixed and confirmed.** It was
  `[Logging.Disk] WriteUnityLog`, which defaults to `false` and is a separate gate from
  `[Logging] UnityLogListening`. See [`05-local-testing.md`](05-local-testing.md).

## OB-35 — Clients throw a NullReferenceException every frame from the tumbleweed interpolator — **FIXED and CONFIRMED in play (2026-10-19)**

**Observed 2026-10-19**, Desert, three players (host plus two test bots). Each client's log has about
290 copies of one exception and the host's has none:

```
Il2CppInterop.Runtime.Il2CppException: System.NullReferenceException
   at UnityEngine.GameObject.GetComponent[T]()
   at TumbleWeedInterpolator.InterpolateSnapshot(...)
   at TumbleWeedInterpolator.PerformInterpolation(...)
   at TumbleWeedInterpolator.Update() line 28
```

The first copies appear right after the stage-2 boss died.

**Cause, read from the code:**

- **The interpolator outlives its tumbleweeds.** `SpawnedObjectManagerService` creates the
  `TumbleWeedInterpolator` once and marks it `DontDestroyOnLoad`. The tumbleweeds it tracks are
  ordinary scene objects.
- **Nothing cleared it on a level change.** `ResetForNextLevel` cleared the service's own tables
  but never the interpolator.
- **An entry left for a destroyed tumbleweed throws.** Any tumbleweed destroyed without a
  `TumbleWeedDespawned` message (a scene unload, or the game destroying it) stays registered. It is
  Unity-null (the native object is gone but its managed proxy lives on), so `GetComponent` throws
  every frame.
- **Clients only.** Only clients register tumbleweeds for interpolation, which is why the host log
  is clean.
- **Ids restart too.** `ResetForNextLevel` also resets object ids to 0, so a stale entry could have
  matched a new tumbleweed's id.

**Fixed:**

- **The interpolator is cleared on a level change.** `ResetForNextLevel` now calls `Clear()` on it.
- **A destroyed tumbleweed is dropped as a backstop.** `Update` drops any entry whose tumbleweed is
  Unity-null, using a reused list so it allocates nothing per frame. It logs this once per level:
  `[tumbleweed] OB-35: dropped N destroyed tumbleweed(s) from interpolation`.
- **`UnregisterTumbleWeed` no longer destroys an already-destroyed object.**

**UNVERIFIED, not run in-game.** Confirmed when a Desert run reaches stage 3 with no
`TumbleWeedInterpolator` exception on any client. A `[tumbleweed] OB-35: dropped` line means the
backstop caught a tumbleweed destroyed mid-level without a despawn message, which is worth a look of
its own.

**Confirmed, 2026-10-19 second run.** The run went Desert stages 2–3 and on to the final arena.
Both clients' logs have zero `TumbleWeedInterpolator` exceptions, against about 290 each in the
previous run. No `[tumbleweed] OB-35: dropped` line appeared, so the clear on a level change was
enough on its own.

## OB-36 — Falling off the final arena desyncs every position on that peer — **FIXED, CONFIRMED in play 2026-10-21**

**Observed 2026-10-19**, three players, final arena. The final boss's HP matched on all three peers
at every sample. Its position matched for the first three samples, then both clients' copies sat at
**about 1.9 times the host's coordinates on every axis**, for the rest of the fight:

| Host | Both clients |
|---|---|
| (−3, 49, 13) | (−5, 93, 27) |
| (−53, 45, −8) | (−101, 87, −15) |
| (−72, 42, 14) | (−137, 81, 27) |

A fixed ratio on all three axes is not drift. It is two peers dequantizing over different ranges:
`Quantizer` maps positions over ±`WorldSize.x / 2`, so the same short decodes to a coordinate
scaled by the ratio of the two sizes.

**Cause:**

- **The final arena's range was never stored as its own.** `MapGenerationFinalBoss` sets the
  range to 600, but `OriginalWorldSize` keeps the previous stage's map size, set by
  `MapGenerationController`.
- **A fall off the map restores that stale size.** `PlayerMovementPatches.TeleportPlayerBackToBounds_Prefix`
  restores `OriginalWorldSize`. Its callers, by xref on VA `0x180343870`, are
  `InteractableGhostBossLeave.Teleport` (leaving the graveyard boss room) and
  `WorldColliderTrigger.OnCollisionStay`: the out-of-bounds trigger.
- **So one peer leaves the shared range.** Falling off the arena put that peer on the old Desert
  size while everyone else stayed on 600. From then on every position it sent was mis-scaled, and
  so was every position it received.
- **Graveyard has the same shape.** Its range is 5000, but the restore went back to the raw map
  size.

**LIKELY rather than confirmed.** No log line said which peer hit the trigger. Both bots switched
between the same two samples, and they follow the host closely and teleport to it when stuck. The
ratio is consistent with the Desert map's size against 600, but that size is not in the log.

**Fixed:**

- **`OriginalWorldSize` now means "this level's range".** `MapGenerationFinalBoss` sets it to 600,
  and `MapGenerationController` sets it to whatever range it applied (5000 on Graveyard), so every
  restore goes back to the range the other peers use.
- **The restore logs once.** It now prints
  `[world] OB-36: teleported back to bounds; position range A -> B`. A and B should be equal
  everywhere except when leaving the graveyard boss room (7500 -> 5000).

**UNVERIFIED, not run in-game.** Confirmed when a fall off the final arena logs `600 -> 600` and the
`[boss] ... sees` positions keep matching across peers.

## OB-37 — A teammate's weapon hits ran the host's on-hit items, never their own — **FIXED, CONFIRMED in play 2026-10-21**

**Found 2026-10-19** from OB-14's filter. On Player2, the host's items (`GlovePower`, `IceCube`,
`Bonker`, `SpicyMeatball`) arrived in replays carrying Player2's id. See OB-14's "second run"
subsection.

**What the game does, decompiled (build 21750826).** A hit's item effects always come from
`MyPlayer.Instance`, the local player:

- **On-hit procs.** `Enemy.DamageFromPlayerWeapon` (`0x1804AC700`) and `DamageFromPlayerOther`
  (`0x1804AC650`) call `Damage`, then the local `ItemInventory.PostDamage` (`0x180442BF0`). That
  runs every on-hit item in the inventory it is called on, once more per Overpowered Lamp (EItem
  26).
- **Pre-attack modifiers.** Both `WeaponUtility.GetDamageContainer` overloads run the local
  `ItemInventory.PreAttack` (`0x180442E90`).

**What that did in netplay:**

- **The host's items ran on everyone's hits.** The host simulates every player's weapons, so every
  teammate hit got the host's pre-attack modifiers and on-hit procs, credited to the teammate.
- **A teammate's on-hit items barely ran at all.** Clients do not run their own weapon hits:
  `ProjectileBase.HitEnemy` and `CombatAura.FixedUpdate` return `isHost`. So a teammate's on-hit
  items ran only where their own machine happened to call `PostDamage` itself.
- **Where they did run, it was client-only damage.** The example here is Tony McZoom's Zap passive
  (`PassiveAbilityZooma.OnEnemyDamage`, `0x1804800F0`). It is a listener on every enemy hit, so on a
  client it fired inside the host's replayed hits and called `DamageFromPlayerOther`. That ran the
  client's `PostDamage` (Player2's Thunder Mitts, `GloveLightning`) and applied damage on the client
  only, while the replay had damage open.
- **`RemoteItemCopies`' premise does not cover on-hit items.** Its doc says each player's own game
  runs their items. That holds for `Tick` and event items, not for on-hit procs.

**Fixed** in `Patches/Items/HitItemOwnership.cs`:

- **Host.** When the tracker attributes the hit to a teammate, `PostDamage` and `PreAttack` on the
  host's inventory run that teammate's inventory instead.
  - For `PreAttack`, the owner comes from the `GetDamageContainer` weapon argument, because no
    tracker scope exists yet at that point.
  - `Enemy.DamageFromPlayerWeapon` now holds the `WeaponHitAttribution` scope across the whole
    call, because that scope used to close when `Damage` returned, before `PostDamage` ran.
- **Client.** It skips `PostDamage` entirely. A client applies no hits of its own, so the host is
  now the one place each hit's procs run. Without this, procs that already reached the host
  through a client's item forward would run twice.
- **Log.** `[items] OB-37: running <id>'s on-hit items for their hit instead of the host's` prints
  once per session.

**Not covered:**

- **Item numbers still read the host's stats.** For example, `ItemGlovesLightning.GetDamage`
  scales by a `MyPlayer` field, and crit comes from the static `PlayerStats.GetStat`.
- **Unattributed hits are unchanged.** A hit the host cannot attribute (`UNRESOLVED-WEAPON`) still
  runs the host's items.
- **Zap itself is unchanged.** A client's Zap passive still applies client-only damage inside a
  replay. That is a separate defect, not fixed here.
- **Heals go to the host.** On-hit items that heal or buff "the player", such as lifesteal, affect
  `MyPlayer`, which is the host, even when the teammate's copy procs. That is the same as before
  for the host's own items.

**2026-10-20 follow-up: three of these gaps are closed.**

- **Stats.** The items' numbers now come from the teammate: [OB-38](#ob-38) covers `GetStat`, and
  `baseDamage` is swapped around the redirected `PostDamage`.
- **Heals.** None of the 27 on-hit or pre-attack item bodies heals or buffs the player (all 27
  decompiled). The heals were in global listeners, which [OB-39](#ob-39) covers.
- **Unattributed hits.** The two causes found are fixed: the portal gap ([OB-41](#ob-41)) and
  debuff ticks ([OB-40](#ob-40)).
- **Zap.** The client-only Zap damage is fixed by OB-39 too.
- **Removed: the client's per-kill proc.** `OnReceivedEnemyDied` used to call `ProcOnHitEffects` on
  every item the client held, for each of its own kills. That was the only way a client's on-hit
  items ever ran. With the host now running them on every hit, it would double them, so it is gone.

**UNVERIFIED, not run in-game.** To confirm, give a teammate an on-hit item the host lacks (Thunder
Mitts, Big Bonk, Spicy Meatball). Then check three things:

- **The teammate's panel** shows that item at their level, with damage growing with their hits.
- **The host's items** no longer appear in `OB-14: kept` lines on the client.
- **The host's log** carries the `[items] OB-37` line.

**Confirmed, 2026-10-21 Endless run (host plus two bots, eleven stage changes, the host's log kept; each bot carried a TestItems loadout).** Each player's on-hit item rows showed at that player's own level, on that player's own panel:

- **Player2:** Cursed Doll 20.0K, Moldy Gloves, Cactus, Mirror.
- **Player3:** Power Gloves, Spicy Meatball, Snek, Slutty Cannon.

The host's panel held only the host's own sources.

<a name="ob-38"></a>
## OB-38 — The host built every teammate's hits from the host's own stats — **FIXED, CONFIRMED in play 2026-10-21**

**Decompiled, build 21750826.** Every stat read on a hit is the static `PlayerStats.GetStat(EStat)`.
It takes no player and answers for the local one. The host builds every teammate's hits, so those
hits read the host's stats:

| What | Stat (EStat) | Where it is read |
|---|---|---|
| Damage | DamageMultiplier (12) | `WeaponUtility.GetDamage`, `GetNewDamage`, the burn and poison debuffs |
| Crits | CritChance (18), CritDamage (19) | `GetDamageContainer`, `GetCritChance`, `GetCritDamageMultiplier` |
| Knockback | KnockbackMultiplier (24) | `GetKnockback`, Eagle Claw |
| Elite damage | EliteDamageMultiplier (23) | `Enemy` |
| Poison | PoisonDamageMultiplier (56) | `DebuffPoison` |

Some numbers bypass `GetStat` entirely:

- **`baseDamage`.** Several on-hit items (Thunder, Cursed and Blood Mitts) and `DebuffFire` read
  `MyPlayer.baseDamage` as a raw field. Per `MyPlayer.CalculateBaseDamage`, that field is
  `defaultBaseDamage` plus the character level, so it only differs between players with shared XP
  off.
- **Item modifiers.** With item modifiers present, `GetNewDamage` reads the local player's
  DamageMultiplier stat components directly, not through `GetStat`.

**Fixed:**

- **Sync.** `PlayerStatsSyncService.TrackedStats` now also carries the combat stats above, plus
  Fire, Ice and Lightning damage, BurnChance, FreezeChance and EffectDurationMultiplier.
  `baseDamage` travels under key 1000. That is not an EStat; `PlayerStatsChanged`'s keyed
  dictionary exists to carry extra keys, and a peer on an older build stores it and never reads it.
  There is no wire change.
- **The override** (`Patches/Items/TeammateCombatStats.cs`). A postfix on `GetStat` returns the
  teammate's synced value for those stats only, and only while one of their hits is in scope:
  - **A weapon accessor called with their weapon.** `GetDamage`, `GetCritChance`,
    `GetCritDamageMultiplier`, `GetKnockback`, `GetDamageProjectile` and the weapon
    `GetDamageContainer` take the owner from their argument.
  - **Otherwise, the tracker.** That covers projectile hits, auras, their on-hit items and their
    debuff ticks.
- **What is never substituted.** Drop chances, gold, XP and the host's own movement keep reading
  the host.
- **The item-modifier path.** `GetNewDamage` with modifiers is rescaled by the ratio of the two
  DamageMultipliers. That is exact for a multiplicative modifier and close for an additive one.
- **Log.** `[stats] OB-38: building <id>'s hit from their own combat stats` prints once per session.

**UNVERIFIED, not run in-game.**

- **To confirm:** give a teammate a much higher damage or crit build than the host's. Their end
  screen should track their build rather than the host's.
- **Known:** the OB-1 stats (Projectiles, Size, Bounces) keep their own per-accessor correction.

**Confirmed, 2026-10-21 Endless run (host plus two bots, eleven stage changes, the host's log kept; each bot carried a TestItems loadout).** The host player reported damage and procs that fit each player's own build. No `Kill-attribution anomalies` warning appeared, and no `has no owner` sample, on any peer.

<a name="ob-39"></a>
## OB-39 — Hit and kill listeners on items and passives fired for everyone's hits, on every machine — **FIXED, CLOSED 2026-10-20 on play evidence**

**Decompiled.** These listeners are subscribed to the static enemy-hit and enemy-death events.
Each machine raises those events for every hit and kill applied there, whoever made it, and holds
a live copy of every player's items and passive. So every copy reacted to every player's events,
with the effect landing on the local player:

| Listener | VA | Effect | Before |
|---|---|---|---|
| `PlayerHealth.OnEnemyDamaged`, `TryLifestealHit` | `0x180429320`, `0x18042A330` | lifesteal heal | the host healed off everyone's hits, each client off every replayed hit |
| `ItemDemonBlade.OnEnemyDamaged` | `0x1804409B0` | heal on crit | same |
| `ItemKevin.OnEnemyDamaged` | `0x18045F900` | counts hits, then damages its own player | same |
| `ItemDemonicSoul`, `ItemDemonicBlood.OnEnemyDied` | `0x180454910`, `0x180454280` | kill stacks | stacked on everyone's kills |
| `ItemJoesDagger.OnEnemyDamage` | `0x18045ED60` | stacks for its pre-attack bonus | every copy stacked |
| `ItemBorgor.OnEnemyDied` | `0x18043BCE0` | burger drop | one roll per player per kill |
| `ItemSoulHarvester.OnEnemyDied` | `0x180465B70` | death projectile | one per player per kill; on clients, client-side |
| Zap, Plague, Bullseye passives | `0x1804800F0`, `0x18047B730`, `0x180474EA0` | damage | every copy; on clients, damage applied there only |
| `WeaponPassiveBloodMagic.OnEnemyDied` | `0x1804334C0` | weapon stacks | every copy |

**Fixed** in `Patches/Items/EventListenerOwnership.cs`, with two rules depending on where the
effect lives:

- **Owner's machine.** For effects on a player (heals, self-damage, stat stacks), only the local
  player's copy runs, and only for its own hit or kill. Clients own their HP, and their stats
  reach the host through `PlayerStatsChanged`.
- **Host.** For effects in the simulation (damage, projectiles, pickups, stacks that feed a hit
  the host builds), only the host runs them, and only the copy belonging to whoever made the hit
  or kill.

"Whose hit" is the tracker. On the host, that is the attacker the hit was scoped to; null means
the host. On a client, it is the replay's `AttackerId` or the death's `DiedByOwnerId`.

**Known effect:** a client's lifesteal now rolls once per replayed message. The host combines hits
into one message per enemy per update (OB-34), so a client gets about 10% fewer rolls than hits at
the measured 1.0–1.3 hits per message.

**UNVERIFIED, not run in-game.** Two things to watch:

- **Lifesteal.** The host should no longer heal off a teammate's hits.
- **Zap.** A teammate's Zap should still zap, now on the host.

The classification of each listener comes from reading its decompiled body.

**Closed 2026-10-20** at the host player's call. **The basis.** This fix has no log line of its own, so a quiet log is not evidence either way. What supports closing it is behaviour across the 2026-10-20 runs:

- **The listeners' own symptoms are gone.** Holy Book plays only for its holder (OB-48). A teammate's Bloodmark is credited to them (OB-50, OB-52). End screens show owners' rows and no LVL 0 teammate rows.
- **The host stays clean.** It keeps no heals or stacks off teammates' hits.

Reopen if a heal, stack or proc is seen landing on a player who does not own the item.

<a name="ob-40"></a>
## OB-40 — A teammate's burn and poison ticked as the host's — **FIXED, CONFIRMED in play 2026-10-21**

**Decompiled:**

- **`Enemy.AddDebuff`** (`0x1804AB190`) buffers the debuff.
- **Every type ticks through the base `EnemyDebuff.Tick`** (`0x1804A9E20`); subclasses override
  only `MyTick`.
- **A debuff remembers only its enemy and its ticks left**, not who applied it.

So every tick ran unattributed. On the host that meant the host's DamageMultiplier,
PoisonDamageMultiplier and `baseDamage`, the host's kill credit for a damage-over-time kill, and
the host's damage panel.

**Fixed** in `Patches/Enemies/DebuffAttribution.cs`:

- **Record.** On the host, `AddDebuff` records the attacker the tracker holds, per enemy and debuff
  type.
- **Reopen.** `Tick` reopens that scope when nothing else holds the tracker, and swaps
  `baseDamage` for the burn debuff.
- **Latest application wins.** A host or unattributed re-application drops the record. Several
  players stacking one debuff are credited to the last one who applied it.
- **Cleared on a level change.**

**UNVERIFIED, not run in-game.**

**Confirmed, 2026-10-21 Endless run (host plus two bots, eleven stage changes, the host's log kept; each bot carried a TestItems loadout).**

- **Debuffs stay with their owners.** Bloodmark and Poison appear only on the panels of the players who hold their items: Bloody Cleaver and Glove Poison on the bots.
- **The host's Poison row is its own.** The host held Moldy Cheese.
- **No Bloodmark on the host's panel.**

<a name="ob-41"></a>
## OB-41 — Some teammate hits had no owner and ran as the host's — **FIXED, CONFIRMED by log 2026-10-20**

"Some hits still use the host's items" meant hits the host could not trace to a player. Those run
with the host's stats, items, lifesteal and kill credit.

**Causes found:**

- **The level change.** `GetNetPlayerByWeapon` searches the spawned avatars, and
  `ResetForNextLevel` clears them at the portal while teammate projectiles are still in flight.
  This is the 2026-10-19 OB-14 Wireless Dagger row.
- **Debuff ticks.** See [OB-40](#ob-40).
- **An unguarded dereference.** `GetNetPlayerByWeapon` also dereferenced `Inventory` unguarded. A
  NetPlayer gets its inventory a step after it spawns, so in that window it could throw inside a
  hit prefix.

**Fixed:**

- **A lookup that survives the level change.** The five attribution sites (projectile hit, aura
  tick, constant-attack scopes, the hit-level note, and OB-38's weapon scope) now use
  `PlayerManagerService.GetWeaponOwnerId`, which reads the inventory store.
- **The crash.** `GetNetPlayerByWeapon` is null-guarded.
- **The diagnostic counts what matters.** UNRESOLVED-WEAPON used to include the host's own weapons,
  which is why its log line said to "read the share, not the count". The host's own hits are now
  counted separately, so any UNRESOLVED-WEAPON is a teammate hit that lost its owner.
- **Samples.** The first five per session log
  `[attribution] OB-41: a <projectile> hit has no owner -- weaponBase ...`, naming the projectile
  kind for the next fix.

**Not covered, and corrected 2026-10-20.** This entry first listed Cursed Doll, Bob and Holy Book
as running unattributed. Only Holy Book did; see [OB-42](#ob-42). **UNVERIFIED, not run in-game.**

**Confirmed by log, 2026-10-20 21:01 run (host plus two bots, Forest stage 1, ended in a game over before any stage boss died).** No peer logged an `[attribution] OB-41: ... has no owner` sample, and the host never printed its `Kill-attribution anomalies` warning. That warning prints only when a 5 s window has an unbalanced unset, an overwrite, or an `UNRESOLVED-WEAPON` hit, so no teammate hit lost its owner all session.

<a name="ob-42"></a>
## OB-42 — Holy Book ran on every machine and counted every player's healing; forwarded item hits lost their procs — **FIXED, CONFIRMED in play 2026-10-21**

**Sources.** The item pages on megabonkinfo.org (Cursed Doll, Bob (Dead), Holy Book) were read
against the decompiled bodies, build 21750826:

| Item | Page | Decompiled |
|---|---|---|
| Cursed Doll | curses random living enemies anywhere, 30% max HP/s (bosses: (8 + level) × 0.7); proc coefficient 1 | `Tick` (`0x18043FC70`); damage through `DamageFromPlayerOther` |
| Bob (Dead) | a ghost per 14 units the player moves; 1.5 × (8 + level) × damage multiplier; can crit | `Tick` reads `MyPlayer`'s movement. `SpawnGhost` calls `EffectManager.SpawnGhostProjectile` (`0x180501370`) with `1.5 × baseDamage` and Duration × 10; the ghost is an `ItemProjectile`, a `MonoBehaviour` with its own hit, not a `ProjectileBase` |
| Holy Book | healing received becomes damage around the player every 1.5 s; radius (5 + copies) × Size; proc 0.5 | `OnHeal` (`0x18045D100`) adds `hpHealed` and **ignores which player healed**; `Tick` (`0x18045D250`) damages around `MyPlayer.Instance`, scaled by `MyPlayer.baseDamage` and Size |

**Where each one ran:**

- **Cursed Doll and Bob are fine.** `RemoteItemCopies` already gates the teammate copies' `Tick`
  on the host (2026-10-18). Only the owner's game runs them, with the owner's own stats, level and
  movement. Their hits reach the host through the client's item forward, attributed to the
  sender, so they were never unattributed.
- **Holy Book was ungated:**
  - **Your copy of a teammate's Holy Book.** It ran on the host and turned the host's healing
    (and everyone's) into damage around the host, unattributed, as the host's damage.
  - **Every copy.** Because `OnHeal` ignores its argument, every copy also counted healing that
    was not its player's.

**A regression found while reading this.** `caf8a18` (OB-37) stopped clients from running
`PostDamage`. But a client's forwarded item hit is applied on the host with a bare `Enemy.Damage`,
which never runs on-hit items. So since that commit, a teammate's Cursed Doll (proc coefficient 1),
Bob and Holy Book (0.5) hits procced nothing.

**Fixed:**

- **Holy Book** (`RemoteItemCopies.ItemHolyBookPatches`). `Tick` and `OnHeal` skip a teammate's
  copy, and `OnHeal` counts only the local player's own heals.
- **Forwarded hits** (`SynchronizationService.OnReceivedEnemyDamaged`, host only). After applying a
  client's forwarded hit with a proc coefficient above 0, the host calls `PostDamage`. OB-37
  redirects that to the sender's items, because the tracker holds the sender. That matches what
  the game's `DamageFromPlayerOther` does, exactly once per hit.

**UNVERIFIED, not run in-game.** To confirm:

- **Holy Book.** A teammate with Holy Book should deal damage around *themselves* when *they*
  heal, and none around the host.
- **Procs.** A teammate with Cursed Doll and an on-hit item (Ice Cube, Big Bonk) should see that
  item's row grow on their end screen.

**Confirmed, 2026-10-21 Endless run (host plus two bots, eleven stage changes, the host's log kept; each bot carried a TestItems loadout).** Holy Book damage appears on its holder's panel only (Player3, 55). The host player confirmed in an earlier run that the effect no longer plays on players who do not hold it (OB-48).

<a name="ob-43"></a>
## OB-43 — Item audit: every item now runs on one machine, as its owner — **FIXED, CONFIRMED in play 2026-10-21**

**Method.** All 86 `ItemBase` subclasses in `dump.cs` were listed with their real (non-stub)
method bodies. Every `Tick`, event handler and spawn body was decompiled (80 functions). The
ambiguous ones were cross-checked against megabonkinfo.org: Cursed Doll, Bob (Dead), Holy Book,
Kevin, Spiky Shield and Energy Core.

**Two facts drive this.** Every machine holds a live copy of every player's items, subscribed to
the game's static player events. The host also ticks every teammate's copy
(`NetPlayer.Update` -> `PlayerInventory.PhysicsTick`), under the position-request queue that makes
`MyPlayer`, `GameManager.GetPlayerInventory` and `PlayerStatsNew.GetStat` answer for that
teammate.

| Kind | Items | Where it runs now | Stats |
|---|---|---|---|
| Stat only | Battery, Beacon, Boss Buster, Brass Knuckles, Clover, Echo Shard, Forbidden Juice, Giant Fork, Golden Glove, Gym Sauce, Key, Medkit, Oats, Old Mask, Pot, Pumpkin, Skuleg, Slippery Ring, Tactical Glasses, Time Bracelet, Turbo Socks, Unstable Transfusion, Wizard's Hat, Wrench, Shattered Wisdom | owner's stats, on the owner's game | owner's; the host reads the synced combat set (OB-38) |
| On-hit / pre-attack (27 bodies) | Big Bonk, Bloody Cleaver, Dragonfire, Eagle Claw, Joe's Dagger, Giant Fork, the five Mitts, Grandma's Tonic, Ice Crystal, Ice Cube, Lightning Orb, Moldy Cheese, Phantom Shroud, Quin's Mask, Slutty Cannon, Snek, Spicy Meatball, Tactical Glasses, Unstable Transfusion, Weeb Headset, Boss Buster, Brass Knuckles | host, as the owner (OB-37); their ticks keep running on the host to reset proc caps | owner's, plus baseDamage, plus position (below) |
| Hit or kill listeners | Demon Blade, Kevin, Demonic Soul and Blood (owner's game); Joe's Dagger, Borgor, Soul Harvester (host) | OB-39 | owner's |
| Timed damage | Cursed Doll, Bob (Dead), Holy Book, Energy Core, Bob's Lantern, Electric Plug | owner's game only; hits forwarded and procced on the host (OB-42) | owner's (local) |
| Reactions to the player | Cactus, Toxic Barrel, Golden Shield, Speed Boi, Cowards Cloak, Mirror, Phantom Shroud, Flappy Feathers, Scarf, Credit Card (both), Bloody Cleaver (lifesteal proc), Gasmask (stage start) | owner's copy only, and only for its own player's `PlayerHealth` | owner's (local) |
| Player effects on a timer | Beefy Ring, Campfire, Idle Juice, Rollerblades, Golden Sneakers, Gamer Goggles, Sucky Hoof, Cowards Cloak, Phantom Shroud, Spiky Shield, Gasmask, Mirror, Kevin, Demonic Blood and Soul | owner's game only | owner's (local) |
| Interaction | Ghost | OB-2 | — |

**New in this pass:**

- **Owner-only gates** (`Patches/Items/OwnerOnlyItems.cs`) for the "reactions to the player",
  "player effects on a timer", Energy Core and Bob's Lantern rows.
  - **What a teammate's copy used to do.** It reacted to *this* machine's player: a teammate's
    Golden Shield paid the host gold, their Mirror could block the host's damage, their Cactus
    retaliated for the host, and their Credit Card changed the host's chest prices.
  - **What the timed-damage pair did.** Energy Core and Bob's Lantern fired a second set of hits
    from the host.
  - **Sucky Hoof** keeps working: a client's own pickup pull already asks the host
    (`OnWantToStartFollowingPickup`).
- **More stats substituted.** Thorns (3) is now synced; GetWeaponDamage reads it. Thorns, Size,
  Duration and ProjectileSpeed join the substituted set, for the on-hit items the host runs.
- **Position.** While the host runs a teammate's on-hit items, the teammate is pushed onto the
  position-request queue. Slutty Cannon, Power Gloves, Big Bonk, Lightning Orb and Blood Mitts
  then start from them, not from the host.
- **Init-time size.** Spicy Meatball and Quin's Mask cache their size when initialised. On the
  host's copy of a teammate's item, that now runs as the teammate.

**What is still the host's, on purpose or by limitation:**

- **Unattributed hits.** A hit the host cannot attribute to anyone still counts as the host's.
  OB-41 now names these.
- **Approximate stacking and caching.**
  - A debuff stacked by several players credits the last one who applied it (OB-40).
  - `GetNewDamage`'s item-modifier path is a ratio (OB-38).
  - The init-time size of Spicy Meatball and Quin's Mask refreshes only when the item's amount
    changes, not when the teammate's Size later changes.

**UNVERIFIED, not run in-game.** First check: the BepInEx log at startup has no Harmony errors.
This pass adds about 30 hooks, each checked against `dump.cs` names and parameters.

**Confirmed, 2026-10-21 Endless run (host plus two bots, eleven stage changes, the host's log kept; each bot carried a TestItems loadout).** Across both bots' loadouts, every item's damage landed on its owner's panel, at its owner's level. The only exception was Player3's run-end Cactus replay, which is a separate edge (OB-14). The host player reported no item running for the wrong player.

<a name="ob-44"></a>
## OB-44 — With two stage bosses, clients opened the portal after the first died — **FIXED, CONFIRMED by log 2026-10-21**

**Reported 2026-10-19.** In stage 2, after the host killed the boss, the host saw no portal but
both bots did. A bot walked through it and the run moved on.

**What the logs show.** The host spawned **two** Pharaoh2s from one interaction (ids 233 and 234,
the same frame, a unit apart). Then:

- **One died.** Both clients logged `Boss defeated, activating portal.`
- **The other was still alive** on every peer: the host's next sample showed 1 enemy, and the
  desync digest read `enemies 1/1`.
- **A bot used the portal** about 10 seconds after the spawn. The host correctly had none.

**Decompiled (build 21750826):**

- **`SummonerController.SpawnStageBoss`** (`0x180487C20`) spawns `GameManager[+0x60] + 1` bosses,
  so more than one is vanilla.
- **`InteractableBossSpawner.Interact`** (`0x1804C5670`) keeps the returned list.
- **`OnEnemyReleasedFromPool`** (`0x1804C5B80`) removes each boss from the list. Only when the list
  is empty, and `CanSpawnPortal` holds, does it activate the portal.
- **`CanSpawnPortal`** (`0x1804C5540`) requires `MapController.index < runConfig.mapTierIndex` and
  not the last stage.

Ghidra's applied field names on this class are one slot off; the offsets were matched to
`dump.cs`.

**The defect.** A client opens the portal itself (`OnBossDefeated`). It did so on the first
stage-boss death, from both its own `OnEnemyDied` and the host's `EnemyDied`.

**Fixed.** Both call sites now open the portal only when no other living stage boss is
registered. When one is still alive they log `[boss] OB-44: a stage boss died but another ... is
still alive; the portal waits for it.`

**Not changed here, and the cause of the original report.** A client did not check
`CanSpawnPortal`'s stage and tier condition. That turned out to be why the host had no stage-2
portal at all; see [OB-46](#ob-46).

**UNVERIFIED, not run in-game.** To confirm, a stage with two bosses (stage 2 in this run) should
show its portal on all peers only after the second boss dies.

**Confirmed by log, 2026-10-21 Endless run (host plus two bots; Graveyard start, nine stage changes across Forest, Desert and Graveyard, with Ent and the Ghost King as boss rooms). The host's log was overwritten by a relaunch, so this rests on both bots' logs.** With two stage bosses, both bots logged `OB-44: a stage boss died but another (id ...) is still alive; the portal waits for it.`, then went on together.

<a name="ob-45"></a>
## OB-45 — Test tooling: a fixed item loadout and an end-screen report in the log — **ADDED 2026-10-20**

Not a bug. These are tools for verifying OB-37 to OB-44 without relying on chest rolls or on
reading three screens:

- **`[Testing] TestItems`** (`Helpers/TestLoadout.cs`) grants the listed items once per session at
  run start, through `ItemInventory.AddItem`, so they replicate like a pickup.
- **The end-screen report** (`Patches/RunEndReport.cs`) is a postfix on
  `GameOverDamageSourcesUi.Start`. It logs every `RunStats.damageSources` row with
  `DamageSource.GetLevel` (the panel's own LVL), plus kills.

Usage and a loadout that covers every item group: `05-local-testing.md`, "Testing specific
items". **Not run in-game yet.**

**Release builds exclude it (2026-10-20).** The loadout and the test bot are behind `#if DEBUG`. A
Release DLL was checked to contain none of `BotController`, `BotMode`, `TestLoadout` or
`TestItems`. The report stays in all builds.


<a name="ob-46"></a>
## OB-46 — On the tier's last stage the host correctly had no portal; clients opened one anyway — **FIXED and CONFIRMED in play (2026-10-20)**

**Reported twice** (2026-10-19 and 2026-10-20). After the stage-2 boss, the host saw no portal and
both bots did. A bot walked through it and the run went on to a third stage.

**The host was right; this is vanilla design.** Decompiled, build 21750826:

- **`InteractableBossSpawner.CanSpawnPortal`** (`0x1804C5540`) is
  `!MapController.IsLastStage() && MapController.index < runConfig.mapTierIndex`.
- **`OnEnemyReleasedFromPool`** (`0x1804C5B80`) opens the portal only when that holds. Either way it
  raises `A_BossDefeated(canSpawnPortal)`.
- **`ObjectiveUi.OnBossDefeated`** (`0x1803E7BD0`) shows `survive` for false and `enterPortal` for
  true. Ghidra prints the base as `enterPortal`; by `dump.cs` offsets the pair is
  `survive`/`enterPortal`.
- **`DeathScreen.OnBossDefeated`** (`0x180531EA0`) sets `tierVictory` when
  `MapController.IsTierFinalStage()`.

So on the tier's last stage, killing the boss opens no portal: the objective becomes "Survive" and
the tier counts as won.

**This run's tier.** The `RunStarted` line prints `runConfig.mapTierIndex`, and it read **1**.
Stage 1 (index 0) passes `0 < 1` and the host opened its portal. Stage 2 (index 1) fails `1 < 1`, so
the host, correctly, opened none in both runs.

**The defect.** A client opens the portal itself in `OnBossDefeated` (OB-44's call sites). It did so
unconditionally and raised `A_BossDefeated(true)`. So the bots saw a portal the game never meant
to exist, and using it took every peer to a stage the tier does not have.

**Fixed.** The client asks the same `bossSpawner.CanSpawnPortal()`:

- **Portal.** It activates the portal only when that is true.
- **Event.** It raises `A_BossDefeated` with the real value, so its own objective and victory state
  match the host's.
- **Log.** It prints `Boss defeated; the tier's last stage has no portal (OB-46).` when there is
  none.

**Confirmed 2026-10-20, Tier 1 Forest** (`mapTierIndex 0`, so stage 1 is the tier's last):

- **No portal anywhere.** No peer got a portal after the stage boss.
- **Same objective everywhere.** All three showed "Find the Boss Portal ✓, Defeat the Boss ✓,
  Survive...".
- **Both bots logged it:** `Boss defeated; the tier's last stage has no portal (OB-46).`

That is the game's intended flow.

### Notes for a future endless-run mode

**Superseded by the plan in [`18-endless-and-projectile-cap-plan.md`](18-endless-and-projectile-cap-plan.md)** (2026-10-20), where the two differ.

This is what an "endless" option would have to change, and where. **All of it is UNVERIFIED as a
design.** Only the decompiled rule above is established.

- **The only gate is `InteractableBossSpawner.CanSpawnPortal`** (`0x1804C5540`):
  `!MapController.IsLastStage() && MapController.index < runConfig.mapTierIndex`.
  - A postfix forcing it true on **every** peer would open the portal on the tier's last stage.
  - Since OB-46 the clients ask the same method, so one patch covers host and clients alike. It
    must be the same on every peer, so it belongs in a synced lobby setting, not in a per-player
    config.
- **`IsLastStage()` is the second wall.** On a map's last stage (Forest/Desert stage 3) a portal
  would lead to an index past `mapData.stages`. Endless needs a decision there:
  - **Wrap:** back to stage 0, or to a random stage.
  - **Repeat:** the last stage again.
  - **Advance:** to another map.

  Each is a new stage load. That goes through `MapController` (index, `currentStage`), which the
  mod drives with `RunStarted` / `PortalOpened`, and every peer must pick the same next stage. So
  the host chooses and the existing level-change messages carry it; the wire format stays unchanged
  if an existing field can carry it.
- **What the "last stage" flow drives,** in case endless keeps or suppresses it:
  - `A_BossDefeated(false)` raises `ObjectiveUi`'s "Survive" (`0x1803E7BD0`).
  - `DeathScreen.OnBossDefeated` (`0x180531EA0`) sets `tierVictory` via
    `MapController.IsTierFinalStage()`. That is the victory state the end screen reads.
- **Scaling past the tier.** `GameBalanceService` keys its multipliers on the stage index,
  switching on 0/1/2, so an index past 2 needs a rule, for example clamping or extrapolating. The
  game's own difficulty curve is time-based (`EStat.Difficulty`, OB-26).

<a name="ob-47"></a>
## OB-47 — A teammate's row slipped past the damage-panel filter while the local inventory was missing — **FIXED, CONFIRMED in play 2026-10-21**

**What the 2026-10-20 report showed.** The host's end-screen report showed `Mirror LVL 0 997`. The
host held no Mirror; a bot did. The host log had neither an `OB-14: kept 'Mirror'` line nor an
`OB-14: recording 'Mirror'` line.

**How that can happen.** Of the prefix's paths, only one records a hit silently: the local
inventory is null for a moment. That happens during a level load, or with the player object gone.

- **The teammate check gave up.** `IsTeammatesSource` returned "not a teammate's" as soon as it
  found no local inventory.
- **The diagnostic hid it.** `ReportForeignSource` spent its once-per-source mark before
  discovering the inventory was missing, so the one log line for that source never printed.

**Fixed:**

- **Teammate check.** A missing local inventory no longer ends it; the teammate check alone decides.
- **Diagnostic.** It marks a source only once it can actually check it.

The path is LIKELY, not seen directly, because nothing logged it. **UNVERIFIED, not run in-game.**

**Also this run (2026-10-20): the bot and the revive fight.** While the host was dead, a bot chased
the revive ghost (GhostGrave4, 169K HP). Whenever it got stuck it teleported to its "owner", which
was the dead host's body, so it kept landing back at the corpse; teleports went 62 -> 64, and the
ghost lost about 15K in 90 s. During the revive fight the bot now teleports next to the ghost. The
ghost's HP itself is unchanged: boss HP scaled by team difficulty and cut to 2/12 on the first
revive (`EnemyManagerService.RebalanceIfNeededReviverEnemy`).

**Confirmed, 2026-10-21 Endless run (host plus two bots, eleven stage changes, the host's log kept; each bot carried a TestItems loadout).** No teammate row slipped through on the host, or on Player2, across eleven stage changes. Each stage change is exactly when the local inventory goes missing.

<a name="ob-48"></a>
## OB-48 — The Holy Book effect played on the host, who held no Holy Book — **FIXED (visual), CONFIRMED in play 2026-10-20 21:01**

**Reported 2026-10-20.** The host saw the Holy Book effect on their own character. They held no Holy
Book; a bot did. The host's report had no Holy Book damage row, and its log no `kept 'HolyBook'`
line, so no Holy Book damage was recorded on the host.

**Decompiled:**

- **`HolyBookEffect`** is a component on the player.
- **Its `Awake`** (`0x1804983B0`) subscribes `OnUse(radius)` to the static `ItemHolyBook.A_OnUse`.
- **`ItemHolyBook.Tick`** (`0x18045D250`) raises that event each time any copy radiates.
- **The game's own `Refresh`** (`0x180498AE0`) shows the book only when the local inventory holds
  one; `OnUse` has no such check.

So any Holy Book ticking on a machine plays the effect on that machine's player.

**Fixed (visual).** `HolyBookEffect.OnUse` plays only when the local player holds a Holy Book.

**Not yet named.** Which book ticked on the host remains open. OB-42 gates a teammate's copy, so
some copy was evidently not recognised as one. `Tick` now logs once, in that case:
`[items] OB-48: a Holy Book ticked here though we hold none (owner ...)`. The next run's host log
names the path. **UNVERIFIED, not run in-game.**


<a name="ob-49"></a>
## OB-49 — The bots, then the revived host, ended up outside the world during a revive fight — **FIXED, CONFIRMED in play 2026-10-20 21:01**

**Reported 2026-10-20**, Tier 1 Forest, with screenshots. Chasing the host's revive ghost, the bots
teleported outside the map, and after the revive the host stood outside it too.

**What the host log shows:**

- **The ghost goes underground.** The second revive ghost was sampled at **(242, −56, 261)**, with
  Forest ground around y −20, so it flies through and below terrain. The first was seen at y +10.
- **The host died below the ground.** The host's second coffin was at **(260.3, −34.4, 262.0)**,
  14 units below the ground, so the host was already outside the world before that death.

**Two causes:**

- **The bot.** The previous fix (324f7c2) teleported a stuck bot *to the ghost*, which can be inside or under the
  terrain.
- **The revive.** `ReviveLocalPlayer` set only `player.transform.position`. The player's
  `Rigidbody` keeps its own position and the next physics step can put it back. The test bot has to
  set `rb.position` for its own teleports to stick, which is the same effect. **LIKELY:** nothing
  logged where the host actually stood after the revive.

**Fixed:**

- **The bot** (`BotController.GroundUnder`). During the revive fight a stuck bot lands on the
  ground under the ghost, using a downward raycast from above.
  - **Sanity band.** The hit is accepted only within 25 units of the coffin's height, since the
    owner died on the ground there.
  - **Fallback.** Otherwise the bot lands at the coffin.
- **The revive.** `ReviveLocalPlayer` also sets `rb.position` and clears the velocity. It logs
  `[revive] OB-49: respawned the local player at (x, y, z).`, so the next run shows where a revive
  put the player.

**Not changed.**

- **The ghost.** It is GhostGrave4, a flying boss; its movement is the game's.
- **Ending a bot run.** A bot run cannot reach the end screen, because the bots never die
  (`BotInvincibilityPatches`). It ends only when the host wins or quits.

**UNVERIFIED, not run in-game.**


<a name="ob-50"></a>
## OB-50 — A teammate's Bloodmark executes counted as the host's — **FIXED, CONFIRMED in play 2026-10-20 21:01**

**Seen 2026-10-20.** This was a two-player Tier 1 Forest run: the host, plus Player2 played by hand
with the bot off. The host's end-screen report had a `Bloodmark 213` row. The mark comes from
Player2's Bloody Cleaver.

**Decompiled (build 21750826):**

- **`DebuffBloodmark.AddStacks`** (`0x1804A65F0`) is where the damage lands. It computes
  `DamageMultiplier × baseDamageMultiplier × MyPlayer.baseDamage × stacks`, and executes the enemy
  once that reaches its HP.
- **`OnEnemyDamaged`** (`0x1804A68B0`) only re-queues the mark, when a hit brings the enemy below
  it.
- **`MyTick`** is an empty stub, and **`OnRemove`** (`0x1804A69A0`) only unsubscribes.

So the execute happens neither inside the hit nor inside `EnemyDebuff.Tick`, which is all OB-40
scopes. It ran unattributed on the host, with the host's DamageMultiplier and `baseDamage`.

**Fixed.** `AddStacks` reopens the scope of whoever applied the mark, through the same
`DebuffAttribution.BeginTick` as `Tick`. That gives the applier's tracker, their combat stats
(OB-38) and their `baseDamage`.

**Also from this run, all fine:**

- **No LVL 0 rows** on either report.
- **Cactus, Mirror and Cursed Doll** appear only on Player2's screen.
- **Two filter catches.** The host's filter logged `kept 'Dragonfire'` once (one unattributed
  Dragonfire hit kept off the host's panel), and no other teammate item leaked.
- **Death and game over.** The run ended normally: Player2 died, a revive ghost spawned while only
  the host was alive, and then the host died too.

**UNVERIFIED, not run in-game.**


<a name="ob-51"></a>
## OB-51 — A client held 5–6 more enemies than the host for the rest of a run — **FIXED (relay + pooled ids), CONFIRMED in play 2026-10-20 21:01**

**Seen 2026-10-20.** Two-player Tier 1 Forest: the host, and a mortal test bot (`BotInvincible
false`). From digest #102 (t ≈ 606 s) to the end, the bot's desync check reported
`enemies host N / local N+5` (or N+6) on 62 consecutive digests: host 18 / local 23, then host 33 /
local 39.

**What it is not:**

- **A transient.** The previous Forest run stayed in sync to the end (319/319).
- **A death on the client.** The client never died mid-run. The host died once and was revived at
  client line 618, long before the drift began.
- **A missing broadcast for Bloodmark.** Bloodmark kills through `DamageFromPlayerOther`, and both
  `Enemy.EnemyDied` overloads broadcast.

The extra entries are registry entries the host no longer has. That points to a missed death, or a
host-side removal that is not replicated. The logs carry counts only, so they cannot say which
enemies.

**Diagnostic.** `EnemyInterpolator` now records when the host last sent each enemy. When the client
holds more enemies than the host, the desync warning also prints the longest-silent registry
entries, at most once a minute:

`[desync] OB-51: N more enemies here than on the host; the longest-silent entries: <id> <type> hp <hp> [DEAD] silent <s>; ...`

**Limits.** The host's stream carries only changes, so a still enemy can go quiet legitimately;
silence is a hint, not proof. Entries that are `DEAD`, destroyed, or of one recurring type would
name the leak.

**OPEN.** Needs the next run's `OB-51` line.

### 2026-10-20 second run — 186 extra enemies, alive and frozen on the client

Two-player Tier 1 Forest, mortal bot. The bot died four times and was revived three times. The
desync grew to **186 more enemies on the client than on the host**, and stopped growing once the
stage boss was dead. The new report named the entries:

- **Goblins, Golems and BoomerSpiders,** all at very low HP (1–20).
- **Alive on the client, and silent for minutes.** None was marked `DEAD`, and the host had sent
  some of them nothing for over 400 s. They are enemies that died on the host while the client kept
  them, alive and frozen.
- **Some BoomerSpiders were `silent never`.** The host never sent them a single update, which means
  they died very soon after spawning.

**Ruled out:**

- **Deaths arriving before their spawn.** `enemiesDiedBeforeSpawn` is checked when the queued spawn
  drains.
- **The Bloodmark execute itself.** `AddStacks` kills through `DamageFromPlayerOther`, and both
  `EnemyDied` overloads broadcast.

**Correlates with, not yet proven:** more Bloodmark executes (+5 at Bloodmark 2,395; +186 at
8,134). The host also logged 11 `Enemy not found ... OnEnemyDied` warnings, most of them during
the first revive fight.

**Known side issue.** A client never removes a BoomerSpider on `EnemyDied` (that waits for its
explosion), so a spider killed without exploding stays in the registry. That inflates the count,
but it does not explain the living Goblins and Golems.

**Diagnostic added.** The client records every `EnemyDied` id it receives. Each `OB-51` entry now
ends in either `died-msg-RECEIVED` (the host sent the death and this peer failed to apply it) or
`no-died-msg` (the host never sent it), which says which side to fix. The set is cleared per stage
and per session.

### Cause: the host relayed a client's kill to everyone except the killer

Found by reading the death path end to end, after the 2026-10-20 17:02 run (+186 on a bot).

**How a client reports a kill.** `OnReceivedEnemyDamaged` replays the host's hits with the tracker
set to `AttackerId` and does not suppress outbound messages. So when a replayed hit takes the
client's copy to 0 HP, the client's own `EnemyDied` postfix runs:

- it removes the enemy locally;
- it sends `EnemyDied` to the host with `DiedByOwnerId` = that **attacker**.

**How the host passed it on.** `NetMessageRouter` (host) applied the death and relayed it with
`SendToAllClientsExcept(enemyDied.DiedByOwnerId)`. That id is the killer, not the sender.

- **Attacker is the host, or the sender itself:** every peer that still had the enemy got it.
- **Attacker is a third player:** say bot 2 reports a kill made by bot 3's hit. The host removed
  the enemy, bot 2 had already removed it, and the one peer skipped was bot 3, which still held it.

The host never streams an enemy it no longer has, so bot 3's copy stayed alive and frozen at the
low HP its own copy had reached. That matches the report:

- Goblins, Golems and BoomerSpiders at 1–20 HP;
- silent for minutes;
- arriving in batches, one per AoE kill.

**Why it grew with Bloodmark, and the honest answer to "is it the Bloodmark change".**

- **The patches do not drop deaths.** OB-40/50/52 are void prefixes and finalizers that only set
  the tracker and swap stats; they never skip a game method.
- **They did raise how often this bug fired.** Before OB-40 a debuff tick had no attribution, so a
  client stamped its own id as `DiedByOwnerId` and the relay skipped only the sender, which was
  harmless. Now a debuff kill carries its owner, often a third player, which is exactly the losing
  case. The defect was in the relay from the start; the attribution fix made it common.
  **LIKELY**, from the code and the correlation (+5 at Bloodmark 2,395, +186 at 8,134).

**Fixed.** The host relays a client's `EnemyDied` to **all** clients. The message has no sender
field, so the sender gets its own kill back. That is harmless:

- it has already removed the enemy, so the id is unknown there;
- it lands in `enemiesDiedBeforeSpawn`, which is cleared per stage, and ids are never reused
  within one.

No wire change.

**Ruled out by decompiling, build 21750826:**

- **A death being dropped by the game.** `Enemy.EnemyDied(dc)` (`0x1804AD060`) returns early only
  if the enemy's dead flag is already set. It stops movement, runs `DeathBehaviour`, stops all
  coroutines and raises `A_EnemyDied`, and it never pools synchronously.
- **The host's own broadcast.** `OnEnemyDied` sends `ReliableOrdered` to all clients.

**Separate, not fixed: timed despawns.**

- **How they happen.** `Enemy.TryDespawn` (`0x1804B0640`, reached only through a function pointer)
  starts the `Despawn` coroutine inline once `MyTime.time` passes `despawnAtTime` (+0x98). It does
  not call `Enemy.Despawn()`, so patching that method would never fire. The coroutine
  (`0x1804BD2A0`) ends in `ReleaseToPool` (`0x1804AFC50`: `SetActive(false)`, pool release,
  `ClearAllDebuffs`, `A_EnemyReleasedFromPool`) with no `EnemyDied`.
- **What follows.** Nothing unregisters a despawned enemy, on any peer.
- **Why it is not this bug.** The entry stays in the registry on the host too, so the host keeps
  streaming it and the counts match. It cannot explain a client-only surplus.
- **Diagnostic.** Whether it happens at all is unknown, so `Enemy.ReleaseToPool` now logs
  `[desync] OB-51: enemy N (...) went back to the pool alive and still registered (k so far, ...)`
  on the first and every 25th time. If that shows up, replicate it.

**Also unchanged:**

- **BoomerSpider entries.** A client never removes a BoomerSpider on `EnemyDied`; that waits for
  its explosion. This produced the two `hp 0 DEAD` entries.
- **The host warnings.** The 11 `Enemy not found ... OnEnemyDied` warnings are the host's own
  deaths of enemies it had already unregistered.

**Confirm in play:** the `OB-51: N more enemies here than on the host` warning should stay at or
near 0 on both bots, with Bloodmark or Unstable Transfusion in a teammate's build. **UNVERIFIED,
not run in-game.**

### Second cause: pooled objects kept their ids on the client (2026-10-20 17:55)

**The run.** The relay fix was in the build, and a bot still ended **114 ahead** of the host.

- **Deaths did arrive.** 99 of the 114 entries were tagged `died-msg-RECEIVED`, and the client log
  had no exception.
- **One object, several ids.** Four of them were Goblins at exactly HP 8 and exactly 130 s silent.
- **Pooling on the client only.** The bot logged **575** live enemies going back to the pool while
  registered (Ghosts, BoomerSpiders, a few Goblins). The host logged **none**.

**What happened.**

1. A client pooled a live enemy itself, most likely through the game's own timed despawn. The host
   still had it.
2. The registry kept the old id.
3. The pool handed the same object to the next `SpawnedEnemy`, so two ids pointed at it.
4. A death removed one id. The other stayed registered for the stage, silent, at the object's last
   HP, and so did the death-received tag set by the first.

**Fixed:**

- **Registry** (`EnemyManagerService`). The registry indexes each entry by the object's instance
  id. Registering an object drops any older id still pointing at it, and logs
  `enemy object reused for N while M still pointed at it`.
- **Client release** (`Enemy.ReleaseToPool` postfix). A client unregisters whatever id a pooled
  object held.
- **Host release.** The host only logs. Its deaths unregister in the `EnemyDied` postfix, which
  must still find the entry to broadcast.
- **Timed despawn** (`Enemy.TryDespawn`). It no longer runs on clients; only the host decides that
  an enemy despawns.

**LIKELY** that the 575 releases were timed despawns. The release log names the enemy, not the
path.

**Left open.**

- **Host-side timed despawn.** If the host itself despawns an enemy, nothing tells the clients. It
  read 0 in this run.
- **Host-side alias drop.** If the host's registration drops an alias, that is not broadcast
  either.

**Confirm in play:**

- the `more enemies here` warning stays near 0;
- `went back to the pool alive` on a client should become rare.

**UNVERIFIED, not run in-game.**

**Confirmed 2026-10-20 21:01** (host plus two bots, both bots holding Bloody Cleaver and Unstable
Transfusion):

- **No surplus.** Zero `more enemies here` warnings on any peer.
- **Counts match.** Every digest was in sync, ending at `enemies 183/183`.
- **One live pooling, everywhere at once.** A GoldenSkeleton (id 1263) went back to the pool alive
  on the host and on both bots. Both bots unregistered it, and the host dropped the stale id when
  the object was reused for 1350.
- **Timed despawn.** The client-only despawns of the earlier run (575) are gone.

Which fix removed which share is not separable from one run. Both causes were real.


<a name="ob-52"></a>
## OB-52 — Bloodmark's second execute, on removal, ran unscoped on the host — **FIXED (second attempt), CONFIRMED in play 2026-10-20 21:01**

**Seen 2026-10-20**, with OB-50 already in the build. The host's end screen had `Bloodmark 2,395`.
The host confirmed holding no Bloodmark item (no Bloody Cleaver, no Slurp Gloves); the bot held
Bloody Cleaver. The host log read `recording 'Bloodmark' ... unattributed`, so at the execute no
owner record existed.

**Decompiled.** An xref on `Enemy.AddDebuff` (`0x1804AB190`) gives the Bloodmark appliers:

- `ItemBloodyCleaver.ProcOnHitEffects` and `Tick`;
- `ItemGlovesBlood.ProcOnHitEffects` and `ItemUnstableTransfusion.ProcOnHitEffects`;
- **`DebuffBloodmark.OnEnemyDamaged`** (`0x1804A68B0`) itself. When **any** hit brings the marked
  enemy under the mark's damage, it calls `AddDebuff(Bloodmark)` again to queue the execute.

**The defect.** OB-40 lets the latest application of a debuff decide its owner, and drops the
record when the application is unscoped. The host's own hit on Player2's marked enemy was
unscoped, so that self-re-application looked like the host applying the mark, and erased
Player2's record. The execute (`AddStacks`, OB-50) then ran unattributed.

**Fixed.** While `DebuffBloodmark.OnEnemyDamaged` runs, `DebuffAttribution.OnAdded` ignores
applications: a mark re-adding itself is not a new owner. The execute then reopens Player2's scope
through OB-50. **UNVERIFIED, not run in-game.**

### The first fix was wrong; seen again at 17:55

**The run.** The host still showed `Bloodmark 1,550`, again with no Bloodmark item. Both bots now
held Bloody Cleaver. The log still read `recording 'Bloodmark' ... unattributed`.

**The misread.** Read with `dump.cs` offsets, Ghidra's field names in `OnEnemyDamaged` are off by
one slot, as the decompilation guide warns: the call is `HashSet<EDebuff>.Add`. The only
`HashSet<EDebuff>` on `Enemy` is `debuffsToRemove` (0x130); `debuffsToAdd` (0x138) is a
`Dictionary`. So **`OnEnemyDamaged` never re-adds the mark. It queues the mark for removal** when
a hit leaves the enemy at or under the mark's damage. The guard above could never fire, and it has
been removed.

**The real path** (all decompiled):

1. `Enemy.DebuffTick` (`0x1804ACC60`) ticks each debuff through virtual slot 4 (the base
   `EnemyDebuff.Tick`, scoped by OB-40).
2. It then drains `debuffsToRemove` through `Enemy.RemoveDebuff` (`0x1804AFD70`), with no tracker
   scope.
3. `DebuffBloodmark.OnRemove` (`0x1804A69A0`) unsubscribes. Unless the removal is from death, or
   there are no stacks, or the enemy is at 0 HP, it then deals the mark's stored `damage` through
   `Enemy.DamageFromPlayerOther`.

That stored damage was computed by `AddStacks` under the owner's scope (OB-50). The hit itself is
the second Bloodmark execute, and it ran as the host's.

**So Bloodmark deals damage in exactly two places:**

- **`AddStacks`**, once the stacks reach the enemy's HP. Scoped since OB-50.
- **`OnRemove`**, the "a hit brought it low enough" execute. Unscoped until now.

`MyTick` is an empty stub.

**Fixed.** `DebuffBloodmark.OnRemove` reopens the mark owner's scope, exactly as `AddStacks` does
(`DebuffAttribution.BeginTick` / `EndTick`):

- **Credit.** The execute is the owner's.
- **Damage panel.** It goes on the owner's panel, not the host's.
- **On-hit items.** It runs the owner's on-hit items through the host's redirect (OB-37).

**Ruled out:**

- **The teammate's Bloody Cleaver copy on the host.** Its `Tick` (`0x180439FA0`) marks every enemy
  in its lifesteal tally, but its `OnLifestealProc` is already owner-only (OB-43), so that tally
  stays empty on the host.
- **Poison.** It does its damage in `MyTick`, inside the scoped `EnemyDebuff.Tick`, and its
  `OnRemove` deals none. The host's `Poison 2,508` row is a separate question: whether the host
  has a poison source of its own is not yet checked.

**UNVERIFIED, not run in-game.**

**Confirmed 2026-10-20 21:01.**

- **Host.** No `Bloodmark` row, and no `recording 'Bloodmark' ... unattributed` line.
- **Player2.** Its own end screen carries `Bloodmark 8,198`, the mark's owner credited.
- **Poison.** This run the host held Glove Poison itself (`GlovePoison LVL 1`), so its `Poison` row
  is its own.


<a name="ob-53"></a>
## OB-53 — A bot teleported into a wall, died outside the map, and its coffin could not be reached — **FIXED, CONFIRMED in play 2026-10-20 21:01**

**Reported 2026-10-20**, two-player Tier 1 Forest with a mortal bot, with a screenshot. The bot
teleported toward the host but ended up outside the map. Then it died, and the host could not
revive it.

**From the logs:**

- **Teleports.** The bot teleported to its owner **7 times** in about a minute.
- **No out-of-bounds catch.** It logged no `[world] OB-36` line, so the game's out-of-bounds trigger
  never caught it.
- **No revive.** The host log has no trace of the coffin being used: no `OB-11` re-centre and no
  revive-ghost spawn. Every earlier revive was a client using the host's coffin; this was the first
  time the host tried to revive a client.

**Cause, LIKELY.** It is a chain; the coffin's position was not logged.

1. **The stuck-teleport lands beside the owner, blind.** It used `owner + (2, 0.5, 0)` with no
   check. Beside the host's stone wall, that is inside geometry, and the bot fell out of the
   playable area.
2. **The coffin follows the body.** The mortal bot died out there, and `SpawnReviver` places the
   coffin at the dead player's position, which was unreachable.

**Fixed:**

- **Bot teleport.** The spot beside the owner is used only if a downward ray finds ground there
  within 3 units of the owner's height. Otherwise the bot lands exactly on the owner, which is a
  known in-world position. `GroundUnder` takes the band as a parameter.
- **Coffin (host), in three steps:**
  1. **Ground directly under the death point** (within 6 units): the coffin stays there. That covers
     a normal death, even under a tree.
  2. **A surface 1–6 units above it:** the coffin moves up to it. That covers a coffin sunk into the
     ground or a shallow dip.
  3. **Anything else:** the coffin is placed beside the nearest living player, on checked ground.
     "Anything else" means deep under the map, inside a hill or mountain, or in the void.

  Step 3 exists because a ray from above lands on the *top* of whatever is there: under or inside a
  desert hill, a straight lift would have put the coffin on an unclimbable peak. A living player's
  position is reachable by definition. The revived player respawns at the coffin, so they come back
  in the world too.

  The host confirmed afterwards that the coffin could not be found at all, and looked to be in or
  under the ground, which is this case. A death on the surface, even under a tree,
  stays put. Clients get the position through `SpawnedReviver`.
- **Logging, so the next run settles the LIKELY:**
  - `[revive] OB-53: coffin for <id> at <pos>` when a coffin is placed;
  - `... died under the surface ...; coffin moved up to <pos>` when it is moved;
  - `... coffin for <id> used; revive fight starting.` when it is used.

**UNVERIFIED, not run in-game.** Confirmed when a mortal bot's coffin can be used by the host and
the revive completes.

**Partly confirmed, 2026-10-20 21:01 run (host plus two bots, Forest stage 1, ended in a game over before any stage boss died).**

- **The echo is gone.** The clients' `[bw] EnemyDamaged` ran at 0.1 to 3.7 messages a second, against the storm of 2026-10-19.
- **Aggregation was not exercised.** The host's line read 31 to 45 hits a second at 1.0 hits per message, a load where nothing combines.
- **Not seen yet:** the final boss's death reaching every client. The run never reached the final arena.

**Confirmed by log, 2026-10-21 Endless run (host plus two bots; Graveyard start, nine stage changes across Forest, Desert and Graveyard, with Ent and the Ghost King as boss rooms). The host's log was overwritten by a relaunch, so this rests on both bots' logs.** The final boss (Ent3, 9.5M HP) lost HP identically on both bots. Every peer then left through the portal its death opens (`Stage change 4` on both). The clients' `[bw] EnemyDamaged` peaked at 2.8 messages a second.

**Confirmed by log, 2026-10-21 Endless run (host plus two bots; Graveyard start, nine stage changes across Forest, Desert and Graveyard, with Ent and the Ghost King as boss rooms). The host's log was overwritten by a relaunch, so this rests on both bots' logs.** In the Ent arena a bot logged `[world] OB-36: teleported back to bounds; position range 600 -> 600.` That `[boss]` positions kept matching afterwards could not be compared against the host.

**Confirmed, 2026-10-21 Endless run (host plus two bots, eleven stage changes, including Desert rooms and the Ent and Pharaoh arenas; the host's log was kept).** Falls off the final arena logged `[world] OB-36: teleported back to bounds; position range 600 -> 600.` three times across the peers, and the host player reported no desync afterwards.

**Closed 2026-10-21** at the host player's call. It has not reappeared since 2026-10-11. It was seen only during the failsafe storm, which OB-21's fix ended, and no later log has a failsafe storm (the 2026-10-21 logs have at most one failsafe line per peer). Reopen it if the client's camera leaves its character again.

<a name="ob-54"></a>
## OB-54 — Two leftovers from a full log review of the 2026-10-21 runs — **FIXED 2026-10-21, not yet confirmed in play**

**1. The Ghost King's spawn threw on every client, and clients got no boss music.**

`GraveyardBossRoom.CheckSpawnBoss` (`0x180505F90`, decompiled; Ghidra's labels are one slot off,
so read against `dump.cs` offsets) runs when `isFightingBoss` is set and `spawnBossAtTime` has
passed:

1. sets `hasSpawnedBoss`;
2. spawns the Ghost King with `EnemyManager.SpawnBoss`;
3. uses the returned boss for its armor and the lamp colliders;
4. plays `musicTrackBoss` and updates the objective.

On a client `SpawnBoss` returns null by design (OB-8), so step 3 threw, once per fight, and step
4 never ran there.

**Fixed:** on a client the method is replaced by its check, the flag and the music. The boss, its
armor and the objective already arrive with the host's `SpawnedEnemy`. The lamp colliders only
matter to the host's physics.

**2. A charge shrine's finishing effect was sent to clients as a spawned object.**

`RandomObjectSpawner_Postfix` matched scene objects to a spawner's prefabs with
`name.StartsWith(prefab.name)`. So `ChargeShrineFinishFx` matched the `ChargeShrine` prefab, was
sent as a spawned object, and no client has a prefab by that name: four per Ghost King room, each
retried until given up.

**Fixed:** only a real copy matches, meaning the prefab's own name, `Name(Clone)` or `Name (n)`.

**Also seen, not changed:**

- **One shared-experience failsafe at Forest 2.** A client reported for an encounter that the host
  had not opened yet. The failsafe released it after 20 s, the host's release followed, and digests
  stayed in sync.
- **`Could not find original InventoryHud or itemContainerPrefab`** each time teammate cards are
  built. This is present since the original code: the teammate cards find no `InventoryHud` to
  copy, so they likely show no weapon and tome icons. Not investigated.
- **The host's one Unity `NullReferenceException`** as an encounter window opened. Player.log had
  been overwritten by another copy of the game, so there was no stack trace.

**UNVERIFIED, not run in-game.**

## OB-55 — Shared-experience failsafe fired three times in Endless Crypt2, then the client left for Forest3 alone — **PARTLY FIXED 2026-10-22, not yet confirmed in play**

**Seen:** 2026-10-22, Steam transport, host plus one friend, Endless with Shared Experience on.
Rounds 0 to 5 released normally. Rounds 6, 7 and 8 each paused the host with no reward window
(`[pause] encounterWindow=False MyTime.paused=True`), the client never reported, and the host's
failsafe released each after 20 s. No error was logged. During round 8 the client used the Crypt2
exit and loaded Forest3 alone; the host stayed in Crypt2, the client waited on "Still waiting for
the host to name a round" and the connection dropped.

**Cause, CONFIRMED from the logs and code:** in Crypt2 the host could not find any object the
client interacted with by its netplay id. The client's `Crypt Pots` counter climbed to 8 while the
host's stayed at 2, and every host stall follows one of the client's `InteractableUsed`. On a miss,
`OnReceivedInteractableUsed` in shared experience paused, showed "Waiting for other player(s)
choices..." and joined the barrier, assuming the sender had a reward window open. For a pot the
sender opens none and never reports, so the receiver was frozen until the failsafe. The crypt exit
hit the same branch, so the host paused instead of following the client out.

**Fixed:** a miss now logs `[interactable] ... has no match here (N registered); skipped.` and
never pauses or joins the barrier. If the missing object really was a reward the sender is
choosing from, the sender waits on its own failsafe instead of freezing the receiver.

**Not fixed, UNVERIFIED:** why the host's registry has no match for Crypt2's objects. In Crypt1 it
matched. `SpawnedObjectManagerService.ResetForNextLevel` clears the registry and restarts ids at
1; if it runs after Crypt2's `RsgController` registration, the client holds ids the host has
forgotten or reused. Two diagnostics now log the order: `[objects] Crypt generated: registered N
...` and `[objects] Next level: clearing N registered object(s)`. Until this is fixed, a client
leaving a crypt can still split the session across two maps.

**2026-10-22, later run (9 stage changes, 2 boss loops): the reset-order hypothesis is DISPROVED.**
On every stage change the host logged `Next level: clearing` *before* `Stage change` and before
`Crypt generated`, including Forest1 to Crypt2. Two new facts: Crypt2 logged `Crypt generated`
**twice** in a row (125 objects each; the second pass is a no-op per object), and the host also
generates a crypt in the Ghost King and Graveyard surface rooms, where the client has none and gives
up matching 25 objects each time. The failing case (Crypt1 to Crypt2, crypt to crypt) was not
repeated, so its cause is still open.

**Related fix, same day:** `OnReceivedInteractableUsed` fell through after a successful final-portal
exit and looked up object 0. In shared experience that used to pause the receiving peer behind the
barrier on every final-boss exit; it now returns. The final-portal lookup also threw instead of
warning when no portal existed.

## OB-56 — Item visuals drawn on one machine only; a teammate's Soul Harvester never fired — **family 1 FIXED, confirmed in play 2026-10-24; families 2 to 4 open**

**Seen:** 2026-10-22, the client could not see the Soul Harvester "red ghosts" or Energy Core orbs,
although both items worked (their end-screen damage panels list them).

**Cause, CONFIRMED by decompiling (build 21750826):** the mod runs each item on exactly one machine
(kill and on-hit listeners on the host, OB-37/OB-39; timed and self-reaction items on the owner,
OB-43). Anything the item spawns exists only there; nothing replicated it. An audit of all ~86
items found four families:

| Family | Items | Chokepoint |
|---|---|---|
| `ItemProjectile` | Soul Harvester, Energy Core, Ghost, Bob (Dead) | `Set` (0x1804D1CC0) creates; `CheckCollision` (0x1804D0C90) is the only damage path |
| `Rocket` (item) | Slutty Cannon | `Rocket.Set` with `weaponBase == null`; damage in `Rocket.HitEnemy` (0x1804F3CB0) |
| `EffectManager` / `EffectPlayer.Play` | Zooma, Mirror, Electric Plug, Cactus, Holy Book, the 5 gloves, Toxic Barrel | visual-only methods, capturable inside a scope set at the ownership decision points |
| Pooled one-shot effects | Bonker, Ice Cube, Grandma's Tonic, Quin's Mask, Spicy Meatball, Snek, Cursed Doll, Plague, Bullseye | no shared game method; a scoped capture on `ObjectPool<GameObject>.Get` (UNVERIFIED that Harmony can patch it) or per-item hooks |

A client never sees even its **own** on-hit effects, since the host runs `PostDamage` for it.

**Full audit** (per-item VAs, pool offsets, `ItemProjectile` internals, proposed hooks for families
2 to 4): [`reverse-engineering/02-item-visual-audit.md`](../reverse-engineering/02-item-visual-audit.md).

**Fixed (family 1):** tag 85 `ItemProjectilesSpawned` (protocol 5). A postfix on `ItemProjectile.Set`
queues the values `Set` computed (direction, speed, lifetime, up-time, index, range, source) and the
owner; one batch per frame goes host-to-all or client-to-host (the host draws and relays). Receivers
take the same pool, mark the instance, call `Set` with zero damage and overwrite the computed fields;
an orb copy orbits the owner's model. `CheckCollision` on a marked copy ends it on any enemy without
damage (prefix-false). Homing targets are picked locally, so copies can diverge cosmetically.

**Also fixed:** `ItemSoulHarvesterPatches` (pre-OB-39) only let the local player's own copy run, so
together with OB-39's `RunOnHost` a teammate's Soul Harvester never fired on the host. Removed, with
its `itemProcs` counter. The 2026-10-22 panels showed 36.9M for the host's copy and 542K for the
client's.

**2026-10-24 playtest:** Energy Core and Ghost/Bob copies showed on Player2; Soul Harvester showed
nowhere, and the client's end screen had no SoulHarvester row. The host sent no
`ItemProjectilesSpawned` at all, so the teammate's copy still never fired. **Cause, CONFIRMED by
decompiling:** `ItemSoulHarvester.Init` (0x180465A20) subscribes `OnEnemyDied` to static offset 0x20
of `Enemy`, which dump.cs names `A_EnemyReleasedFromPool`, not `A_EnemyDied` (0x8, used by Borgor,
Plague and Blood Magic). It fires when the corpse returns to the pool, after the kill's tracker scope
has closed, so `RunOnHost` saw every death as the host's. `OnEnemyDied` itself has no chance roll:
2 souls (`numProjectiles`, ctor) per release.

**Fix, not yet run in game:** the host notes the killer by enemy instance id in the `Enemy.EnemyDied`
prefixes (`EventListenerOwnership.NoteKiller`). The Soul Harvester prefix checks that killer
(`RunOnHostForKill`) and scopes the call to them, so the `ItemProjectile.Set` postfix shares the souls
under the right owner. An enemy released without a recorded death keeps the old rule.

**2026-10-24 second playtest, souls and explosions still missing.** Two findings:

1. **Player2 ran the old build.** `D:\Games\Megabonk` still had the 2026-10-23 21:38 DLL (no
   `NoteKiller`, no `PlayRocketExplosion`); only the Steam install had the new one. The Debug auto-copy
   goes to `MegabonkPath` only, so a second local install must be updated by hand.
2. **A teammate's items went dead on the host from stage 2 (CONFIRMED by decompiling).**
   `NetPlayer.Destroy` called `inventory.Cleanup()`. A stage change destroys the NetPlayer, yet the host
   keeps that `PlayerInventory` in `playerInventories` and hands it to the next NetPlayer without `Init`.
   `ItemInventory.Cleanup` (0x180442570) calls each item's `Cleanup` (vtable +0x188, slot 5 assuming the
   usual 0x138 base) and clears `itemsWithOnHitProcs`; `ItemSoulHarvester.Cleanup` (0x180465770) removes
   itself from `Enemy` static 0x20. So from stage 2 every static listener and on-hit proc of a teammate's
   items stopped on the host, not only Soul Harvester. Stage changes themselves do not clean the local
   inventory: `PlayerInventory.Cleanup` (0x1804E02A0) is called only from `MapController.RestartRun`,
   `StartNewMap` and `TryCleanupInventory`. The rebuild path in `PlayerManagerService` (Destroy, then
   Initialize) had the same flaw.

   **Fix, not yet run in game:** the NetPlayer skips the cleanup while the inventory is still the kept one
   (`IsKeptInventory`); `Disconnect` now cleans it when it drops it, and the session reset already did.

The host did spawn and send souls in stage 1 of this test. Reading that from `[bw]` needs care:
`BandwidthDiagnostics.Record` adds bytes x recipients but counts every call as a send, so the host's
relays of client batches to zero other clients pull its `B/send` below one soul (31 to 49 B against
73 B). After stage 1 the line fell out of the top-8 list. An `[item-fx]` summary (sent, drawn per kind,
copies ended on an enemy, at most every 10 s) now makes this direct.

**Confirmed in play, 2026-10-24 (fourth test, both installs on the same build):** Soul Harvester souls
show for Player2's copy. The host's `[item-fx]` "sent" count kept rising after the stage change
(28 at the start of stage 2, 80 by the end), so the teammate's item survives the NetPlayer rebuild.
Counts agree across the two logs: the host sent 80 and Player2 drew 80 souls; Player2 sent 242 and the
host drew 242 orbs. 58 of 80 soul copies and 208 of 242 orb copies ended on an enemy, as the originals
do. Ghost/Bob was not exercised (0 drawn). Local test, 0% loss.

**Not done:** families 2 to 4. Not visuals and needing their own sync: Borgor (a host-only burger
pickup that heals `MyPlayer`), Dragonfire's damaging firefield, Golden Shield money, Campfire and Bob's
Lantern (instantiated once, not pooled).

**Leads from the audit, UNVERIFIED (read from source, not logs):**
- Lightning Orb: the client's replay of `LightningStrike` (tag 41) may re-forward the host's hit to
  the host, which applies it again (the damage forward does not check `CAN_SEND_MESSAGES`).
- Weapon rockets: clients skip `Rocket.ProjectileDone`, which spawns the explosion, so clients likely
  never see weapon-rocket explosions.


## OB-57 — A teammate's Shadowstep and Flex reacted to the local player's evades and hits; a client's own were dropped — **FIXED 2026-10-24, UNVERIFIED, not run in-game**

Found by the `Init` audit in `docs/reverse-engineering/02-item-visual-audit.md` ("Same mechanisms
as Rocket, Soul Harvester and Energy Core", part 1). Not seen in play. Every machine holds a live
copy of each teammate's passive (OB-14), and both passives subscribe to static `PlayerHealth`
events that fire for this machine's player only. OB-39 gated the enemy-event listeners. It did
not gate these.

**Shadowstep.** `OnEvade` (VA `0x18047D210`, decompiled) is on `PlayerHealth.A_Evaded`. It damages
the evaded enemy through `DamageFromPlayerOther`, source `"Shadowstep"`. The damage is the enemy's
current HP, or a wave-scaled amount for a boss, so it is an execute. `isExecute` is set, but
`Enemy.Damage` (`0x1804AC7B0`) does not read it.

- On the host, a teammate's copy executed enemies on the host's evades.
- On a client, all copies (the owner's own too) were lost. `Enemy.Damage` is host-only, and
  `Damage_Postfix` forwards only sources in `AllowedDamageSource`. That set held only `EItem`
  names, and `"Shadowstep"` is not one.

**Flex.** `OnCheckStopDamage` (`0x180477FA0`) is on `PlayerHealth.A_CheckStopDamage`. It does nothing
until the cooldown is up. The cooldown is a **static** field, `flexReadyAtTime`, shared by every
copy. When the cooldown is up, it zeroes the local player's damage (`BossDamage` is only scaled
down; a `BypassAll` hit is not blocked unless it is from Kevin or `HealthRegen`), and calls `UseFlex`
(`0x180478340`). `UseFlex` does these things:

- starts the shared cooldown;
- gives that copy a stack and a damage stat (`SetStat`);
- hits and debuffs the enemies within its radius of the local player, source `"Flex"`;
- shows the popup and plays the sound.

So a teammate's Flex blocked the local player's hits, and it could take the cooldown before our
own copy did. On a client, the owner's own Flex hits were lost in the same way as Shadowstep.

**Fixed.**

- `PassiveAbilityShadowstep.OnEvade` and `PassiveAbilityFlex.OnCheckStopDamage` run only for the
  local player's own passive (`EventListenerOwnership.RunForLocalPlayerEvent`), compared by
  pointer. This does not use the tracker's attacker: these events are not hits, and on a client
  the attacker is null outside a replayed hit.
- `EnemyPatch.AllowedDamageSource` also holds `"Shadowstep"` and `"Flex"`. A client's own hit now
  goes to the host, which applies it in the client's tracker scope and runs the client's on-hit
  items through `PostDamage`, as for a forwarded item hit (OB-42).

**Limits.**

- A client's Shadowstep sends the enemy HP that the client sees. Client HP follows the host's
  replays, so it is usually equal or higher, and the execute still kills. If the client HP is
  lower, the enemy can live.
- `isExecute` is not on the wire, so the host may draw it as a normal damage number.
- Flex's debuff (`AddDebuff(8, …)`) and knockback on a client are not looked at here.
- `A_Evaded` and `A_CheckStopDamage` carry no `PlayerHealth`. If the host's teammate
  `PlayerHealth` copies also raise them, that is a separate leak, the same as for Mirror and
  Phantom Shroud (OB-43).

**To confirm in play** (at 2–5 % loss):

1. A client with Shadowstep evades: the enemy dies on every peer, and the client's damage row
   shows Shadowstep.
2. The host evades while a client has Shadowstep: no execute.
3. A teammate has Flex: the host is not protected by it.
4. A client's own Flex blocks a hit and damages nearby enemies on the host.

## OB-58 — Received weapon projectiles could end early on clients; received mines exploded on their own — **FIXED 2026-10-24, confirmed in a hands-off run (0% loss)**

Found by the audit in `docs/reverse-engineering/02-item-visual-audit.md` ("Same mechanisms as
Rocket, Soul Harvester and Energy Core", parts 2 and 3). Not seen in play.

**Cause (decompiled, CONFIRMED in code; effect UNVERIFIED in play):**

1. **Lost tag, same as OB-25.** `OnReceivedSpawnedProjectile` tagged each received projectile's
   component with a `DynamicData` "netplayId", but the interpolator keeps only its `GameObject`.
   `Il2CppObjectPool` caches wrappers weakly, so once the component's wrapper was collected the tag
   was gone. `ProjectileBase.ProjectileDone_Postfix` then let the client end the copy on its own clock
   while the interpolator kept moving it. Every synced weapon except rockets (already fixed) was
   exposed.
2. **Mines exploded locally.** Nothing patched `ProjectileMines`. On a client a received mine's
   `MyFixedUpdate` (0x1804EEF10) checks for enemies and calls `Explode` (0x1804EE9A0), which draws
   from `explosionPool`, damages (blocked on clients) and calls `ProjectileDone`. With the tag held,
   that was refused, so the mine stayed and could explode again at every check until the host's
   `ProjectileDone` arrived.

**Fix:**

- `ProjectileBasePatches` keeps received projectiles in a `HashSet<int>` of `GetInstanceID()`, marked at
  the spawn site, forgotten when the interpolator removes the copy, cleared with the session. The
  `ProjectileDone` guard checks it first and the tag second.
- ~~`ProjectileMinesPatches` skips `MyFixedUpdate` on clients for received mines, and
  `ProjectileInterpolator` draws a mine's explosion on removal.~~ Replaced the same day; see the
  hands-off runs below.

**Hands-off runs, 2026-10-24** (`run-local-session.ps1`, host `Mine, Rockets`, client `Mine`,
Desert, 9 min each; `[explosions]` counts the host's mine `Explode` and weapon-rocket
`ProjectileDone` against the explosions each client draws):

- **The first fix over-drew mines: host 42 explosions, client 710.** The assumption above that "a
  mine ends only by exploding" was wrong. `ProjectileBase.CheckTimeout` (0x18042BB30) ends a mine
  whose duration runs out with `ProjectileDone` and no explosion, and most mines end that way, so
  drawing one on every removal was wrong.
- **Revised fix:** a received mine runs its own `MyFixedUpdate` on the client again, so it explodes
  only when an enemy is inside its radius, as on the host. A postfix on `Explode` hides the spent mine
  (its refused `ProjectileDone` would have), so it cannot explode twice. The removal-time explosion
  in `ProjectileInterpolator` is now for rockets only.
- **Result after the revision**, at four matching 30 s check points: rockets 667/667, 728/728,
  795/795, 859/859 (host finished / client drew). Mines 42/28, 66/40, 101/64, 119/73: the client
  never over-draws, and draws about 60% of the host's. The rest are mines whose trigger enemy the
  client sees differently (already killed by the host, or interpolated elsewhere). Exact mine
  explosions would need the host to send them (a new message, protocol bump).
- No mod errors and no `Look rotation` warnings on either copy.

**Exact mine explosions (same day, protocol 6).** Copying `TryInit`'s trigger radius onto received
mines (`ProjectileMinesPatches.InitReceived`; `TryInit` 0x1804EF150 sets checkRadius = 2 x
clamp(1 + 0.3 x (attack size - 1), 1, 3), constants read from `.rdata`) raised the client to 77-88%
(197 / 151), not 100%: the client sees enemies an interpolation delay late and the host kills some
first. So the host now sends tag 86 `MineExploded` (position, `projectileRadius` as scale) from
`Explode`, a received mine again skips `MyFixedUpdate` on clients, and a client draws exactly the
host's explosions. `InitReceived` stays for the scale-in look.

Hands-off result, aligned on the shared `[desync]` digest numbers: mines 21/21, 22/22, 47/47
(host exploded / client drew). The same run first showed rockets 0 drawn for two minutes: a test-only
race, the host's `TestWeapons` grant reached the client before it had spawned the host's player and
was dropped (`Player not found ... OnReceivedWeaponAdded`), so the client ignored host rockets until
a level-up re-sent the weapon. `TestLoadout` now grants 5 s after run start.

**Final hands-off run** (both fixes): rockets 245/245, 364/364, 406/406, 449/449 and mines 20/20,
22/22, 43/43, 43/43 (host / client), exact from the start; no dropped `WeaponAdded`, no mod errors, no
`Look rotation` warnings.

Not covered: packet loss, and whether received non-explosive projectiles now last exactly as long as
the host's (no counter for that).

## OB-59 — Received Poison Flasks exploded on the client's own collisions, repeatedly; received Bluetooth daggers kept hitting — **FIXED 2026-10-25, confirmed in a hands-off run (0% loss)**

Found by the audit in `docs/reverse-engineering/02-item-visual-audit.md` ("Weapon projectiles with
rocket- or mine-like problems", part 2). Not seen in play.

**Cause (decompiled, CONFIRMED in code):**

1. **Poison Flask.** The overrides `HitEnemy` (0x1804EFDC0) and `HitOther` (0x1804EFDE0) only call
   `ExplodeFlask` (0x1804EF5D0). It detaches `effect` (0x80) to the flask's position, toggles it, sets
   its local scale to `explosionSizeDefault` (0x88) x attack size, plays it, damages and poisons the
   enemies in `explosionRadius` x attack size, draws `ProjectileHit` for each, then calls
   `ProjectileDone`. The base `HitEnemy` patch does not cover these overrides, so a client ran all of
   it on a received copy at its own collisions; its `ProjectileDone` was refused, so the copy kept
   flying and could explode again. `explosionSizeDefault` is filled from the effect's scale in
   `TryInit` (0x1804F0240), which a client never runs on a received copy, so the client's own
   explosions were drawn at zero scale.
2. **Bluetooth.** `StepMovement` (0x1804E65D0) calls `HitTarget` (0x1804E6320) on reaching
   `lastTarget`: damage, `ProjectileHit`, a bounce count, then `ProjectileDone` or the next target. On
   a client the refused `ProjectileDone` let a received copy keep choosing and hitting targets.

**Fix (protocol 7, tag 87 `FlaskExploded`, host to all clients, `ReliableUnordered`):**

- `ProjectilePoisonFlaskPatches`: on clients, received flasks skip `HitEnemy` and `HitOther`. The
  host reads the flask's id in an `ExplodeFlask` prefix (its `ProjectileDone` drops it) and sends id,
  position and the effect's scale from the postfix. The client plays that on its copy's own `effect`
  (detached, destroyed 10 s later) and hides the copy. A copy already gone draws nothing and is
  counted.
- `ProjectileBluetoothPatches`: on clients, received copies skip `HitTarget`. The host's hits
  still reach the client as damage numbers through `EnemyDamagedBatch`. Their hit sparks are not
  drawn, the same as for other weapons (handover 21, item 2).
- `ProjectileInterpolator.GetProjectile` and `IProjectileManagerService.GetReceivedProjectile` find a
  received copy by the sender's id.
- `ExplosionDiagnostics` adds an `[explosions] flasks:` line: host flasks exploded and Bluetooth
  hits; client flask explosions drawn, misses, and refused local calls.

**Hands-off runs, 2026-10-25** (`run-local-session.ps1`, Desert, one stage):

Run 1, 9 min, host `PoisonFlask, BluetoothDagger`, client `PoisonFlask`. Flasks (host exploded /
client drew), each side's first line after the same `[desync]` digest:

| Digest | #1 | #13 | #25 | #37 | #49 | #61 | #73 | #85 | #97 |
|---|---|---|---|---|---|---|---|---|---|
| Host | 1 | 32 | 66 | 110 | 160 | 209 | 286 | 392 | 516 |
| Client | 1 | 32 | 67 | 110 | 160 | 211 | 289 | 394 | 522 |

The counts are equal at four digests. Where they differ, the client's line was written later than
the host's: each side throttles its own line, so neither line is a snapshot at the digest. No copy
was gone when its explosion arrived (0 misses all run). Bluetooth: the host hit 766 times. The client
refused 102 local `HitTarget` calls, so the old repeat path was still being reached and is now blocked.

Run 2, 5 min, host and client `PoisonFlask`: last lines host 204 / client 203, 0 misses.

Neither run had mod errors, `Look rotation` warnings or exceptions in `Player.log`. Client captures
showed purple flask bursts at the host's character.

Not covered: packet loss; whether the client's explosion looks the same as the host's (stills
only); Bluetooth's missing hit sparks on clients (handover 21, item 2); the 10 s effect lifetime
(UNVERIFIED: the particle length was not read).

## OB-60 — Weapon hit sparks never drawn on clients, or drawn on the client's own clock — **FIXED 2026-10-25, confirmed in a hands-off run (0% loss)**

Found by the audit in `docs/reverse-engineering/02-item-visual-audit.md` ("Weapon projectiles with
rocket- or mine-like problems", part 1). Not reported in play.

**Call graph (decompiled and xref'd, CONFIRMED in code):**

- Every weapon spark goes through `EffectManager.EnemyHitEffect(hitPos, moveDir, hitEnemy, EWeapon,
  weaponHitEffect, useSfx)` (0x1804FC150). It maps the weapon to a source string (a static
  `Dictionary<EWeapon, string>` on `EffectManager`) and calls the string overload (0x1804FBE40). That
  takes an `AttackHit` from `PoolManager.GetProjectileHit` (0x18040A700), puts it at `hitPos` facing
  -moveDir (identity for a zero moveDir) and calls `AttackHit.Play` (0x1804DA9B0: particle system and
  `RandomSfx` only).
- Pools are keyed by source. `weaponHitEffect` only seeds a new pool, and null seeds it with
  `powerupPrefab`.
- The 15 callers of the `EWeapon` overload:
  - `WeaponAttack.ProjectileHit` (0x1804504D0), which passes `prefabHit` (0x30) and has 18 callers
    of its own.
  - Dexecutioner `SpawnEffect`, which also passes `prefabHit`.
  - These pass null: Aegis `UseShield`, `CombatAura`, `IceAura`, `DamageZone`, Firefield /
    Axe / Bananarang `TryPopDamage`, Black Hole `CheckDamage`, Dragon's Breath, `LaserBeamAttack`,
    `RotatingProjectiles` (Chunkers).
  - `XpDamager`, which passes `EWeapon.None`.
- Items call the string overload directly (Cactus, Electric Plug, Holy Book, Mirror, `PlayerHealth.Retaliate`,
  `ItemProjectile.ProjectileDone`); those are item visual family 3, not covered here.
- On clients:
  - Every weapon projectile hit path and Aura are host-only.
  - Aegis and the laser run on their owner's machine only, and the host does not run a client's.
  - Frostwalker, Chunkers, Dragon's Breath, the melee, Scythe and Shotgun zones, Sniper, Whirlwind and
    Dice still run as local copies, damage blocked, and drew sparks at their own collisions.

So a client drew none of the host-only sparks, and drew local ones that did not match the host's
hits.

**First fix, replaced the same day:** drawing one spark per weapon entry of `EnemyDamagedBatch`. It
reached only 72-86% of the host's count, guessed at debuff ticks, and missed every spark that
bypasses `ProjectileHit` (the host counter counted `ProjectileHit` only).

**Fix (protocol 8, tag 88 `EnemyHitEffects`, host to all clients, `ReliableUnordered`):**

- `EnemyHitEffectPatches` (prefix on the `EWeapon` overload, enemy hits only):
  - **Host:** queues each call: weapon, quantized position, direction x127, and whether a prefab was
    passed and whether it used sfx. `NetworkHandler` flushes the queue once per enemy tick, and a
    batch also goes out when it reaches 120 entries.
  - **Client:** refuses every local call except replays and calls inside the local player's own Aegis
    `FixedUpdate` or laser `HitEnemy` / `StopLaser`.
- `WeaponHitSparks.OnReceived` replays each entry through the same overload, passing the attack's
  `prefabHit` where the host passed one, so the same pool is used.
- `[hit-fx]` line: host sparks sent; client sparks drawn from the host, local ones refused, own
  Aegis/laser ones kept.

**Hands-off run, 2026-10-25** (9 min, Desert, one stage, host `Axe, Katana, Aura, Frostwalker`,
client `Aegis, SpaceNoodle`). Each side's first `[hit-fx]` line after the same `[desync]` digest:

| Digest | #13 | #25 | #37 | #49 | #61 | #73 | #85 | #97 |
|---|---|---|---|---|---|---|---|---|
| Host sent | 27 | 52 | 119 | 150 | 473 | 590 | 765 | 1317 |
| Client drew | 27 | 52 | 119 | 150 | 473 | 589 | 765 | 1317 |
| Client refused (local) | 12 | 33 | 76 | 96 | 257 | 319 | 403 | 620 |
| Client kept (own Aegis/laser) | 4 | 8 | 12 | 13 | 41 | 51 | 60 | 98 |

Exact at seven of eight digests. #73 is one apart, which is log timing, since #85 is exact again.
The 620 refused are sparks the client used to draw on its own clock. No mod errors, no
`Look rotation` warnings, no exceptions in `Player.log`. Two client warnings at connect time ("Not
connected to host", "Disconnected peer 0 not found") came from a dropped first P2P attempt before
the lobby, which the retry fixed; not from this change.

Not covered:
- Packet loss: a reliable channel, so sparks arrive late rather than never.
- How the sparks look: not looked at.
- Other clients' Aegis and laser sparks: owner-run, so the host never draws them, and a third
  player still sees none.
- Item sparks: string overload, item family 3.

## OB-61 — Received weapon projectiles drawn at their default size on clients — **FIXED 2026-10-25, confirmed in a hands-off run (0% loss)**

Found by the audit in `docs/reverse-engineering/02-item-visual-audit.md` ("Weapon projectiles",
part 3). Not reported in play.

**Cause (decompiled, CONFIRMED in code):** a client instantiates a received projectile from its prefab
and never runs the setup that sizes it:

- `WeaponAttack.SpawnProjectile` (0x1804506F0) sets the root `localScale` to the prefab's x
  `GetSize()` (0x1804503A0). `GetSize()` = (attack size x `projectileSizeMultiplier` -
  `projectileSizeMultiplier`) + 1.
- `ProjectileBase.Set` (0x18042C870) seeds `baseProjectileRadius` (0x34) from `projectileRadius`
  (0x30) when it is 0, then sets `projectileRadius` = base x `GetSize()`.
- `TryInit`:
  - Arrow (0x1804E0CA0) and Bananarang (0x1804E3970) set the trail start width to the prefab width
    x attack size.
  - Axe (0x1804E1990) sets it to attack size x 0.25 (`.rdata` 0x18262EC44).
  - Melee, Katana and the swords size their zones from attack size, and so does Scythe, which scales
    the root again for a big hit. These run on the client's copy at run time.

Attack size is `WeaponUtility.GetAttackSizeMultiplier` (0x1804347A0): `weaponStats[9]` x
`PlayerStats.GetStat(9)` (the local player's static stats), capped by `maxSizeMultiplier`. The OB-1
postfix in `WeaponUtilityPatches` already returns the owner's synced stat there, so a client can
compute the host's value itself, and run-time readers on the copy already got it.

**Fix (no protocol change):** `ReceivedProjectileSize.Apply`, called at the spawn site for every
received projectile, repeats that setup: `projectileRadius`, root scale, and the Arrow, Bananarang and
Axe trails. Mines keep `InitReceived`. Poison Flask is skipped because its `TryInit` resets the scale
to one and its explosion scale comes from the host (OB-59). Rockets are skipped too.
`ProjectileSizeDiagnostics` logs `[size]`: the host's `GetSize()` sum over projectiles it sends,
against the client's over the copies it sizes.

**Hands-off run, 2026-10-25** (9 min, Desert, one stage, host `Bow, Axe, Bananarang, Katana`, client
`Revolver`). The size upgrades came from the bots' level-up picks. Each side's first `[size]` line
after the same `[desync]` digest:

| Digest | #13 | #37 | #61 | #85 | #97 |
|---|---|---|---|---|---|
| Host: projectiles / size sum / resized | 367 / 374.40 / 74 | 1014 / 1089.49 / 613 | 2039 / 2222.66 / 1500 | 3417 / 3764.99 / 2796 | 4217 / 4684.86 / 3557 |
| Client: same | 367 / 374.40 / 74 | 1014 / 1089.49 / 613 | 2039 / 2222.66 / 1500 | 3417 / 3764.99 / 2796 | 4217 / 4684.86 / 3557 |

Identical at every digest, so the client sizes each copy with the host's value. This also confirms
in game the OB-1 `GetAttackSizeMultiplier` postfix, which had been UNVERIFIED. No mod errors, no
exceptions in `Player.log`.

Not covered: how the sizes look (not looked at); packet loss (no new message).

## OB-62 — Item visuals drawn only on the machine that ran the item — **FIXED 2026-10-25, confirmed in hands-off runs (0% loss); some kinds not exercised**

Closes OB-56 families 2 to 4 and the non-visual items of
`docs/reverse-engineering/02-item-visual-audit.md`. Every item runs on exactly one machine (the host
for procs, the owner for reactions), so anything it drew existed only there. Gameplay was unaffected
except for Borgor: its burgers existed only on the host, so only the host could eat them.

**Decompiled (CONFIRMED in code):**

- **Pooled one-shots.** Bonker 0x18043B230 (bonkPool 0x1E0), Ice Cube 0x18045DBE0 (freezeFxPool
  0x1C0), Grandma's Tonic 0x18045BF40 (0x198), Quin's Mask 0x1804631A0 (0x248), Spicy Meatball
  0x1804671E0 (0x1B0), Snek 0x180464CF0 (0x250), Plague 0x18047B730 (poisonPool 0x128), Bullseye
  0x180474EA0 (explosionPool 0x1D8), Cactus 0x18043C750 (0x230) and Cursed Doll 0x18043FC70 (0x1F0)
  all take an object from a pool, then position it.
  - Both `PoolManager.CreateObjectPool` (0x1804094C0) and `CreateObjectPoolTimeout` (0x180409290)
    install `OnTakeFromPool` (0x18040ABF0) as the get callback. That callback names every object an
    item takes, including newly created ones.
- **Item hit sparks.** These are the string `EnemyHitEffect` (0x1804FBE40). By xref its callers are
  Cactus, Electric Plug, Holy Book, Mirror, `PlayerHealth.Retaliate`, `ItemProjectile.ProjectileDone`
  and the `EWeapon` overload. Every item caller passes a null prefab, and the pool is keyed by source,
  so a replay with the source lands in the same pool.
- **`ZapEffect` (0x1805025D0), `SpawnMirrorFx` (0x180501720), `ElectricalPlugEffect` (0x1804FBC80).**
  Each has one caller by xref (Zooma, Mirror, Electric Plug).
- **Player-attached effects.**
  - The five gloves (e.g. Power 0x18045A980) instantiate their EM prefab once, parent it under
    `MyPlayer.feet` (0xA0) at up x 0.01 (`.rdata` 0x18262EC1C), scale it by the radius and play it.
    The host runs teammates' gloves, so it drew a teammate's glove effect on the host's own player.
  - Toxic Barrel: `Activate` 0x180468080 → `ToxicBarrelEffect.OnUse` 0x180498A20.
- **Slutty Cannon.** `ProcOnHitEffects` (0x1804646F0) calls `Rocket.Set(MyPlayer + up, ...,
  weaponBase null, useGenericPool true, source)`. `CheckCollision` → `HitEnemy` damages, and
  `ProjectileDone` draws the explosion.
- **Not visuals:**
  - Dragonfire (0x180454C90): a `Firefield` from firefieldPool 0x1C8 that damages in
    `CheckDamage`/`TryPopDamage`.
  - Borgor (0x18043BCE0): `Borgar` heals `MyPlayer` on any player-layer collision
    (`OnCollisionEnter` 0x18048AC10).
  - Campfire (`StartFire`/`EndFire` on a `Campfire` that only animates).
  - Bob's Lantern (`Explode` 0x18043A990): EM `lanternExplosion`, a `KillEnemyTrigger`.
  - **Golden Shield is not a bug.** `MoneyUtility.SpawnMoney` (0x18046A6E0) flies a `MoneyFlying` and
    calls `PlayerInventory.ChangeGold` on the owner's machine, which the mod already syncs.

**Fix (protocol 9, tag 89 `ItemVisualEffects`):**

- `Helpers/ItemVisualSync` and `Patches/Items/ItemVisualPatches.cs` / `ItemObjectVisualPatches.cs`
  capture each kind where it ran. Batches go out once per frame from `NetworkHandler.Update`. The host
  sends to all clients. A client sends to the host, which draws the batch and relays it to the other
  clients.
- Receivers replay as the game draws it:
  - Pooled: the same pool, the sender's transform; a dead pool entry is retried.
  - Sparks, Zap and Mirror: the same call.
  - Player effects: one instance per owner and kind under that player.
  - Rockets: a copy that flies, never collides, and ends through its own `ProjectileDone` where the
    sender's ended.
  - Firefields: zero damage, with `CheckDamage`/`TryPopDamage` skipped.
  - Burgers: a copy any player can eat. An eaten burger is removed everywhere else.
  - Campfire: a copy per owner.
  - Lantern: a copy with its `KillEnemyTrigger` removed.
  - A replayed pooled object carrying a `DamageZone` has its `FixedUpdate` skipped.
- On the host, a teammate's glove effect is moved to that teammate.
- A client's own Aegis and laser sparks (owner-run, so the host never draws them) are sent as item
  sparks under the weapon's `EffectManager.weaponNamesCache` source.
- `[item-vfx]` logs sent, drawn and missed counts by kind on each side.

**A crash found on the way (CONFIRMED):** the first build patched `ToxicBarrelEffect.OnUse`. IL2CPP had
folded it and `HolyBookEffect.OnUse` into one function at 0x180498A20. With Holy Book on the client,
every Holy Book use recursed through the patch until the client died of a stack overflow (Windows
event 1000, exception 0xC00000FD, no log line). Found by bisecting hands-off runs with a file trace;
the capture now sits on `ItemToxicBarrel.Activate`. CLAUDE.md has the trap.

**Hands-off runs, 2026-10-25** (9 min each, Desert, one stage). Last line on each side; lines are
throttled independently, so a few apart is timing:

| Kind | Run A sent / drawn | Run B sent / drawn | Final run sent / drawn |
|---|---|---|---|
| Pooled, host → client | 1726 / 1721 (5 missed) | 788 / 767 (21 missed) | 1429 / 1431 (3 missed) |
| Pooled, client → host | 845 / 845 | — | 865 / 863 |
| Player effects (gloves, plug, barrel) | 131 / 131 | 139 / 139 | 80 / 80, 1 / 1 back |
| Item rockets, spawned / finished | 306/302 / 306/302 | 205/204 / 205/204 | 297/292 / 298/293 |
| Item sparks, client → host | 1 / 1 | 14 / 14 | 5 / 4 |
| Dragonfire firefields | — | 237 / 237 | 223 / 224 |
| Borgor burgers | 23 / 23 | 8 / 8 | 9 / 9 |
| Campfire start / end | — | 15 / 15 | 19 / 19 |
| Bob's Lantern | — | 11 / 11 | 11 / 11 |

Run A: host `Bonker, IceCube, SpicyMeatball, Snek, QuinsMask, SluttyCannon, GlovePower,
GloveLightning, GrandmasSecretTonic`, client `Cactus, CursedDoll, ElectricPlug, Mirror, HolyBook,
ToxicBarrel`. Run B: host `Dragonfire, Borgar, LightningOrb, GloveBlood, GlovePoison, GloveCurse,
Bonker, SpicyMeatball`, client `Mirror, ElectricPlug, Cactus, BobsLantern, Campfire, ToxicBarrel,
HolyBook`. The final run had both sets, plus client `Aegis`. The retry for dead pool entries came
after run B. No mod errors, no `Look rotation` warnings, no exceptions in `Player.log`.

**Follow-up runs, same day.** These used two new Debug harness options: `[Testing] TestCharacter` /
`-HostCharacter` to choose the bot's character, and `-ClientVulnerable`. The harness made both bots
invincible, so items that react to their owner being hit (Mirror, Electric Plug, Cactus, Aegis) never
fired.

- Run C (host `TonyMcZoom` for the Zooma passive, client vulnerable with `Mirror, ElectricPlug, Cactus`
  and `Aegis, SpaceNoodle`) exercised the kinds missing above:
  - Zap: 29 / 29.
  - Mirror: 6 / 6.
  - Electric Plug: 26 / 26.
  - Client item sparks plus its own Aegis and laser sparks: 474 / 475.
  - But the client missed 488 of 685 pooled effects in one burst.
- Decompiled cause, CONFIRMED in code:
  - `CreatePooledItem` (0x180409800) and `CreatePooledItemTimeout` (0x1804096E0) return null once a
    pool holds `maxSize` objects.
  - `ReturnToPool` (`OnEnable` 0x180522710, `Update` 0x180522820) times its release on `MyTime`,
    which stops while that player is in a reward screen.
  - So the client's copies stayed out while the host's clock kept draining its own pool.
- Fix: when a pool gives nothing, the oldest copy still out from a replay is released and the pool
  asked again.
- Run D (same, plus host `IceCube, SpicyMeatball`, client `CursedDoll`): exact both ways.
  - Pooled: 1164 / 1164 and 952 / 952.
  - Zap: 40 / 40.
  - Mirror: 9 / 9.
  - Electric Plug: 41 / 41.
  - Sparks: 172 / 172.
  - Burgers: 7 / 7.
  - No misses, no errors, no exceptions.
  - That run never filled a pool (`recycled 0`), so the recycling itself is UNVERIFIED in play.

**Grandma's Tonic, Plague and Bullseye on their own (same day).**

- `[item-vfx]` now also counts pooled entries per pool.
- The bot logs each character's passive when it reaches the character menu. Bullseye is **Bush**,
  Plague is **Amog**, Zap is TonyMcZoom.
- Run E (8 min, host `Bush`, client `Amog`, `GrandmasSecretTonic` on both, host `Revolver`), host
  sent / client drew, at four matching digests:
  - Grandma's Tonic: 41/41, 202/202, 240/240, 276/276.
  - Plague (poisonPool): 24/24, 107/107, 124/124, 139/139.
  - Bullseye (explosionPool): 128/128, 169/169, 184/184, 206/206.
  - The client never sent a Tonic or Plague effect, so each runs only on the host and is drawn once.
  - Cursed Doll effects came from both sides (678/677 host to client, 423/422 back), though no bot was
    given one; they came from chests.
- Run F (4 min, client `CursedDoll` only) shows that is right, not a double tick. Only the owner
  ticks its doll: client sent 376, host drew 376, host sent 0.
- No errors, no exceptions.

**Three copies, burgers eaten (same day).**

- New harness options: `-ThirdPath` (a second client, here `D:\Games\Megabonk - Copy`, whose old
  `MegabonkTogether` plugin was moved to its `BepInEx\disabled-plugins\`), and `-SeekBurgers`
  (`[Testing] TestSeekBurgers`: a following bot walks into burger copies).
- Decompiled: `Borgar.FixedUpdate` is an empty shared stub, so a burger never moves; a player must
  touch it. `DestroyBorgar` (0x18048AB30) deactivates it and releases it to borgorPool.
- **Bug found:** the patch on `DestroyBorgar` forgot the burger's id inside the collision, before the
  collision postfix read it. So no eaten burger was ever sent. The id is now read in the collision
  prefix.
- Run G (9 min). Host `Borgar*3, Bonker`; client A vulnerable with `Aegis, SpaceNoodle`; client B
  vulnerable with `Mirror`; both seeking burgers.
- Burgers eaten:
  - A ate 10 and B ate 5. The host removed 15 originals, A removed B's 5, and B removed A's 10.
- Third player (A's effects reaching B through the host):
  - A's own Aegis/laser and item sparks: A sent 226, B drew 226.
  - A's Cursed Doll effects (from a chest): A sent 124, the host and B drew 124 each.
- B to A: sparks 18 / 18, Mirror 11 / 11.
- Host to both clients:
  - Bonker: 137 / 137 / 137.
  - Rockets: 319/307 on the host, 319/306 and 319/307 on the clients (timing).
  - Campfire: 26 / 26 / 26.
  - Burgers: 21 / 21 / 21.
  - Weapon sparks: 3448 / 3448 / 3438 (B's last line was earlier).
- An earlier three-copy run, before the id fix, also exercised pool recycling (94 recycled on each
  client, nothing missed).
- No errors, no exceptions.

Not covered: packet loss, and how any of it looks.

## OB-63 — A client sent every replayed host Lightning Orb strike back to the host, which applied it again — **FIXED 2026-10-25, confirmed hands-off**

The audit's Lightning Orb lead, now CONFIRMED.

**Cause:**

- The host broadcasts each `WeaponUtility.LightningStrike` as tag 41. The client replays it under
  `Plugin.SuppressOutbound()`, which clears `CAN_SEND_MESSAGES`.
- The strike reaches `Enemy.Damage`. `Damage_Prefix` blocks it on a client (`CAN_DAMAGE_ENEMIES` is
  false).
- `Damage_Postfix` then forwarded any hit whose source is in `AllowedDamageSource`, which is every
  `EItem` name, and it did not check `CAN_SEND_MESSAGES`.
- Lightning Orb's source is "LightningOrb" (an `EItem`). So every replayed Lightning Orb strike and
  bounce was sent back as `EnemyDamaged` and applied on the host a second time. This was real extra
  damage, not a visual.

**Fix:** `Damage_Postfix` returns on a client while `CAN_SEND_MESSAGES` is false. `[dmg-fwd]` counts
the replayed hits it held back that the old code would have forwarded.

**Hands-off, 2026-10-25** (run B and the final run of OB-62, host `LightningOrb`): the client held back
267 and 264 Lightning Orb hits, each of which the host used to apply twice. The final run's counter
also showed 233 Lightning Staff hits, which were never forwarded: their source is not an item name.
The counter now counts only forwardable sources. UNVERIFIED: the host's damage totals were not
compared before and after.

---

<a name="ob-64"></a>
## OB-64 — A reward window opening under the pause menu soft-locks the player — **FIXED 2026-10-28, path confirmed hands-off; the click itself not verified**

**Reported (2026-10-26 two-player session):** a reward popped while a player was in the Esc menu, and
nothing could be clicked. The 20 s failsafe did not free them.

**Cause, decompiled:**

- In netplay the pause menu does not pause (`PauseUiPatches.OnEnable_Postfix`). So the game keeps
  running under the menu, and a level-up or a teammate's chest (Shared XP) reaches
  `EncounterWindows.PopReward`. `PlayerInput.CanInput` (0x1803394D0) checks only the player and the
  camera, so the mod's `PopReward_Prefix` let it through.
- `WindowManager.WindowOpened` (0x18039EAB0) unfocuses the active window (the pause menu:
  `MyButton.SetFocus(false)` on all of its buttons) and focuses the reward window, which is drawn behind
  the menu.
- `PauseUi.CanPause` (0x180556830) and `Pause` refuse while `EncounterWindows.HasEncounter`, so Esc
  could not close the menu either.
- The failsafe never ran for this player. Its clock starts when a peer *reports*, and a player who
  cannot click never reports.

**Fix:** `PopReward_Prefix` closes the pause menu before a reward window opens
(`[pause-menu] Closed the pause menu`). The game never shows both at once. `PauseUi.OnDisable`
unpauses, which is harmless, because the reward pauses again right after.

**Hands-off, 2026-10-28** (`-SharedXp -SkipChestAnimation -OpenPauseMenu`, 12 min): the client bot
opened the menu 47 times. A reward arrived under it 16 times and closed it every time. All 26 barrier
rounds released, with no failsafe on either peer. **Not verified:** the bot picks rewards in code, not
by clicking, so the old soft-lock could not reproduce here. That a person can click the reward after
the menu closes is inferred from the `WindowManager` code, not observed.

<a name="ob-65"></a>
## OB-65 — Skip Chest Animation skipped only gold chests; dropped chests still opened a window for everyone — **FIXED 2026-10-28, confirmed hands-off (0% loss)**

**Reported (2026-10-26):** with the game's Skip Chest Animation on for both players, gold chests
opened with no window, but chests dropped by enemies still showed the window and paused the game.

**Cause:**

- The game reads `CFGameSettings.skip_chest_animation` (0x78) in `OpenChest.OnTriggerStay`
  (0x18046B7F0), `InteractableChest.Interact` (0x180452FB0) and `OpenChestImplementation` (0x180453990).
  When it is 1, the game calls `ChestUtility.OpenChestNoAnimation` instead of `AddEncounter`. So the
  player who picks up a dropped chest already skips.
- Under Shared XP every *other* peer gets `ChestOpened` and `OnReceivedChestOpened` called
  `AddEncounter(ChestNormal)` unconditionally. That is the window players saw.
- It also stalled the barrier. The opener skipped and never reported, so everyone else waited 20 s
  for the failsafe. That explains several of the 2026-10-26 host failsafes. Example: round 21, where
  the client showed `paused 0%` while the host waited. A gold chest had the same hole: a peer who
  could not afford it, or was dead, paused and reported, while the opener with skip on never did.

**Fix:**

- New lobby setting **Skip Chest Animation** (`LobbySettings.SkipChestAnimation`, protocol 11). It
  defaults off.
- `ILobbySettingsService.SkipsChestAnimation()` decides for the session. The lobby setting turns it on
  for everyone. Under Shared XP it alone decides, so every peer agrees. With Shared XP off, the
  player's own game setting also counts.
- `ChestAnimationSkipPatches` sets the game's value for the length of those three calls, then puts
  the player's own value back. The saved setting never changes.
- `OnReceivedChestOpened` calls `OpenChestNoAnimation` instead of opening a window. The
  can't-afford and dead branches no longer pause and report.

**Hands-off, 2026-10-28** (run 2, `-SharedXp -SkipChestAnimation`): the client bot opened 3 chests,
and `[chest-skip]` counted 3 no-window opens on the client and the same 3 replayed on the host. No
window followed a chest use, and there were no failsafes. **Not exercised:** an enemy-dropped chest
picked up by a bot (the bots used gold chests), and a mixed setup where one player's own game
setting differs.

<a name="ob-66"></a>
## OB-66 — Bone projectiles look like a fast-spinning star on clients — **OPEN, cause not found**

**Reported (2026-10-26):** on a client, Bone projectiles spin fast or show several frames at once, so
they look like a star.

**Ruled out (hands-off 2026-10-28, Debug `[proj-diag]`):**

- The interpolator's rotation is not the cause. The host's root forward for a Bone stayed constant
  across 12 snapshots (`fwd -1.01,0.13,-0.10`), so the root does not spin and there is nothing to alias.
- There is no spin component. A received `ProjectileBone(Clone)` holds `Transform, ParticleSystem,
  ParticleSystemRenderer, AudioSource, RandomSfx, ProjectileBasic, AudioSpamFilter, ParticleOpacity`
  and a `SparkTrail` child (`ParticleSystem`, renderer). The bone is drawn by the root particle
  system.
- `ReceivedProjectileSize` changes only scale and radius. `ParticleOpacity.Refresh` changes only
  opacity. `ProjectileBase.Set` reads duration, speed and size stats, and none of them feeds the
  particles.

**Open leads:** which copy looks wrong, a client's own bones or the host's received ones. Also, how
the bone particle's start rotation or rotation-over-lifetime behaves on a copy built by `Instantiate`
rather than from the pool (`Set` re-enables pooled copies, which restarts the system). Needs a capture
or a description from play, then a look at the prefab's particle modules at run time.

<a name="ob-67"></a>
## OB-67 — Returning to a Shady Guy a teammate bought from froze you until the 20 s failsafe — **FIXED 2026-10-28 (solo shop), confirmed hands-off (0% loss)**

**Reported (2026-10-26, Forest, Shared XP):** the client bought from a Shady Guy and the host could
not afford anything. The host went back to the same Shady Guy, still could not afford anything, and
sat on "Waiting for other player(s)" while the client walked around. It happened more than once.

**Cause, decompiled and in the logs:**

- `InteractableShadyGuy.Interact` (0x1804CD400) always opens the shop (`AddEncounter(ShadyGuy)`).
- `OnShadyGuyDone` (0x1804CD5F0) runs when a purchase finishes. Only on the buyer's machine is this
  the Shady Guy being shopped at, so only there does it set `done` (`CanInteract` returns `!done`)
  and invoke `Disappear`, which destroys the object.
- The host's copy stayed up and usable. Using it again opened the host's shop and sent
  `InteractableUsed`. On the client the object was gone: `[interactable] 85195421's interaction with
  object 886 (Interact) has no match here; skipped`, logged three times. The client never joined
  that barrier round, so the host waited for the failsafe each time (host failsafes at rounds 21,
  29 and 31).

**Fix (revised 2026-10-28 at the reporter's request: a player who could not afford anything must be
able to come back):**

- Under Shared XP, a local purchase that sets `done` (prefix/postfix on `OnShadyGuyDone`) sends
  `InteractableUsed` with `Action = Destroy`. No wire change. The receivers only **mark** that Shady
  Guy as bought elsewhere. Theirs stays up and usable.
- A later visit to a marked Shady Guy is a **solo shop** (`TryStartSoloShop`, called from
  `DetectInteractables`). It is not sent, so nobody else gets a window or waits.
  `IEncounterService.IsSoloShop` makes `PopReward_Postfix` unpause, as without Shared XP: a host paused
  in its shop would freeze everyone. It also makes `RewardFinished_Prefix` finish the window without a
  barrier report.
- A purchase on a solo visit sends nothing: everyone else has either bought there or already has it marked.
- Shared XP off is unchanged.
- Logs: `[shady-guy] Bought from ...`, `... was bought from elsewhere; still open here, solo from now on`,
  `Solo shop at a Shady Guy a teammate bought from`. `EncounterPauseDiagnostics` labels a solo shop's
  open-but-unpaused window as intended, not as OB-13.

**Hands-off, 2026-10-28** (`-SharedXp -SeekShadyGuy`; the host bot leaves a Shady Guy's shop unbought
until the client has bought there, then revisits). For four Shady Guys (723 to 726), the host left the
shared shop, the client bought, and the host revisited as a solo shop. The `[pause]` diagnostics show
the host's window open with the game unpaused, and the client opened no window. The host bought on
its solo visit to 726. All 26 barrier rounds released on both peers, with no failsafe.

A later run found a bug in the first solo build: 16 client failsafes in 6 rounds. When the host had
waited at the end of the previous round, `RewardFinished_Prefix` had hidden the reward window
(`SetActive(false)`), and the solo path skipped the line that shows it again. The solo shop opened
invisible, was never finished, and every round after it stalled. `PopReward_Postfix` now re-shows the
window under Shared XP before the solo check. The rerun had 3 solo shops, all 27 rounds released on
both peers, and no failsafe or `has no match`.

**Known cost:** a teammate's shared round that starts during a solo shop waits behind it. The window
is queued, as for any slow chooser, and the teammates' 20 s failsafe applies. The earlier
fix, which finished the Shady Guy everywhere, was also run hands-off and then replaced.
**Not exercised:** three or more players, a purchase while the other peer is still in the same
shared shop, and anything at real packet loss.

**Still open:** the general shape. Any interaction that opens a window on the sender, for an object
already gone on a receiver, still leaves the sender waiting for the failsafe. The receiver skips
without knowing a window opened. Shady Guy was the case that could repeat; others are not known.

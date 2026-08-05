# Shared Experience — audit of the pause/reward barrier

Audit of everything gated on `IsSharedExperienceEnabled()`, prompted by the recurring report that
opening a chest or finishing a shrine leaves **one player stuck behind "Waiting for other player(s)
choices..." while the rest keep playing**, and that the stuck player is freed the moment *someone
else* opens a chest and makes a selection.

That last detail is the strongest clue in the whole report, and it is what this audit is built
around: it says the barrier is not per-encounter. A release generated for a *later* round is what
frees a player stuck on an *earlier* one.

**Status of everything below:** CONFIRMED means traced in the code on this branch. Nothing here has
been reproduced in a controlled test — there is no test suite and the fixes in this document have
not been run in-game.

---

## The protocol, as implemented

Only in shared experience. `MyTime.Pause()` is allowed through
(`MyTimePatches.Pause_Postfix` blocks it in every other netplay mode), so the game really is
paused while the barrier is up.

```
       peer finishes choosing
                │
   EncounterWindows.RewardFinished()
                │
   RewardFinished_Prefix                        ← src/plugin/Patches/EncounterWindow.cs
                │
      ┌─────────┴──────────┬───────────────────────────┐
   queue not empty     barrier already      not yet released
   → return true        released            → SynchronizationService.RewardFinished()
     (keep popping)     → clear + return true      │
                                                    ├── host: count self, and if
                                                    │   IsClosable() → broadcast
                                                    │   CloseEncounter + release locally
                                                    └── client: send EncounterClosed to host
                                                        then hide the window, show
                                                        "Waiting…", return false
```

Host side, on `EncounterClosed` (`UdpClientService.cs:995`): record that player, and if
`IsClosable()` → broadcast `CloseEncounter` and raise it locally.

Every peer, on `CloseEncounter` → `OnCloseEncounter()`:
- `encounterInProgress` → call the game's `RewardFinished()`, which re-enters the prefix above and
  takes the "already released" branch (clear + return true);
- otherwise → `ClearClosedEncounters()` + `MyTime.Unpause()`.

`EncounterService` holds the whole barrier: a set of player ids that have reported, plus a
`forceClose` bool set by a received `CloseEncounter`.

Two properties of that design matter more than any individual bug:

1. **Neither the report nor the release carries a round identity.** `EncounterClosed` carries only
   `OwnerId`; `CloseEncounter` carries nothing at all.
2. **`IsClosable()` is a query with a side effect in practice** — it is what every peer consults to
   decide whether to block, but `forceClose` stays set until some *other* code path clears it.

---

## Findings

<a name="se-1"></a>
### SE-1 — a stale `forceClose` makes the next round unsatisfiable — CONFIRMED

**This is the mechanism behind the reported symptom.**

`RewardFinished_Prefix` returns early, before touching the barrier, whenever the reward queue is
non-empty:

```csharp
var currentQueue = __instance.rewardQueue;
if (currentQueue.Count > 0) //Keep popping reward until queue is empty
{
    return true;
}
```

That path is reached during a release: `OnCloseEncounter` → `encounterWindows.RewardFinished()` →
this prefix. If the peer still has queued rewards at that moment — routine with shared XP, where a
single pickup can trigger several level-ups, and `AddEncounter_Prefix` queues every encounter that
arrives while one is in progress — the release exits **without `ClearClosedEncounters()`**, so
`forceClose` stays `true` on that peer.

From then on, that peer's *next* round takes the "barrier already released" branch immediately:
it clears, returns true, closes its window — **and never reports**. Every other peer waits for a
report that will never be sent.

That is exactly the reported shape: one player continues (the one with the stale flag), the rest
are stuck. And they are freed by the *next* round, because the stale-flag peer eventually reports
again, satisfying the host's barrier — which is still holding the previous round's counts, since
nothing cleared those either.

<a name="se-2"></a>
### SE-2 — the barrier survives teardown and stage changes — CONFIRMED

`closedEncounterPerPlayer` and `forceClose` were cleared **only** by a successful release.
`SynchronizationService.Reset()` (session teardown) and `PrepareForNextLevel()` (stage change) did
not touch them. A session that ended mid-encounter therefore poisoned the next one, in either
direction — instant release with nobody reporting, or a barrier that can never complete. It also
explains reports where only a full game restart recovers.

<a name="se-3"></a>
### SE-3 — the last player to choose is shown "Waiting…" for a round that is over — CONFIRMED

`synchronizationService.RewardFinished()` can complete the round **synchronously**: on the host it
counts itself, sees `IsClosable()`, and re-enters `RewardFinished_Prefix` through
`OnCloseEncounter` → `encounterWindows.RewardFinished()`. When the stack unwinds, the outer call
carries on into its "I am blocked" branch — hiding `activeEncounterWindow`, disabling the UI
particles, showing "Waiting for other player(s) choices…" and returning `false` — for a round that
has already been released.

Cosmetically this is the "stuck" screen players report even when the game has resumed. It also
leaves `activeEncounterWindow` inactive and the particle renderers disabled.

<a name="se-4"></a>
### SE-4 — no failsafe: any hole is permanent — CONFIRMED (this is upstream #88's request)

Nothing bounded the wait. Every hole above, and any not yet found, becomes an unrecoverable run.

<a name="se-5"></a>
### SE-5 — a release can be generated for a round nobody is in — CONFIRMED

`IsClosable()` stays true from the moment the count is met until something clears it, and both the
host's `RewardFinished()` and the `EncounterClosed` handler broadcast `CloseEncounter` whenever
they observe it. A late report for a round that has already been released therefore triggers a
**second** broadcast. A peer that has since opened its next encounter receives it and closes that
window immediately — losing that player's pick.

This is the likeliest explanation for upstream #37's first complaint: *"if player1 selects a chest,
he can not wait for the random selection or stop it"* while player 2 is pressing buttons.

<a name="se-6"></a>
### SE-6 — `IsClosable()` counts every player, including ones that cannot report — CONFIRMED, not fixed

`closedEncounterPerPlayer.Count >= playerManagerService.GetAllPlayers().Count()`. The count is
taken live, so it moves under the barrier. Disconnects are handled (the disconnect path re-checks
and force-closes — see [P1-8](01-critical-fixes.md#p1-8), which also stopped that check being
skipped when the record was already gone), but a player who is loading, mid-teleport, or otherwise
never reaches an encounter is still counted as a participant.

`PopReward_Prefix` returns early — without reporting — when `!CanInput()`. If that condition
persists, that peer never joins the round. With the failsafe in place this now resolves in
`WaitFailsafeSeconds`; without a round identity it cannot be fixed properly.

<a name="se-7"></a>
### SE-7 — shared XP is a last-writer-wins absolute value — CONFIRMED, not fixed

`PlayerXpAddXp` sends the sender's **absolute** `xp` and `leftOverXp`; `OnReceivedAddXp` overwrites
the receiver's values with them and calls `AddXp(0)`.

Two players collecting XP in the same window each overwrite the other with a total that does not
include the other's pickup, so **one pickup is silently discarded**. Delivery is `ReliableOrdered`,
so this is not packet loss — it is the merge rule. The error is unbounded and accumulates over a
run, which matches upstream **#74 "Shared XP breaks after many levels"**.

The fix is to send the *delta* and have each peer add it (the message already carries `Amount`),
or to make the host authoritative for the shared XP total. Both change semantics enough to want a
playtest, and the second changes the wire contract; neither is done here.

<a name="se-8"></a>
### SE-8 — `OnCloseEncounter` dereferenced the UI unguarded — CONFIRMED

`UiManager.Instance.encounterWindows.encounterInProgress` runs from a network callback, so it can
fire mid-teardown. An NRE there abandons the release *after* `forceClose` was set — poisoning the
barrier exactly as in SE-1.

### SE-9 — `ChestWindowUi.b_open` is nulled and often never restored — CONFIRMED, and it is a strong candidate for upstream #93

Non-shared experience only, but the statics are shared with every later session:

```csharp
// Open_Postfix
if (openButton == null) openButton = __instance.b_open;   // static, survives the run
__instance.OpenButton();
__instance.b_open = null;                                  // hides the button

// OnClose_Postfix
if (CurrentRoutine == null) { log; return; }               // ← returns BEFORE restoring
__instance.b_open = openButton;
```

`CurrentRoutine` is only set by `OpeningFinished_Postfix`, which itself returns early when
`!CanInput()`. So on any chest opened while the player cannot input, `b_open` stays **null** — and
the next `ChestWindowUi.Open()` dereferences it. Issue #93's stack is a
`NullReferenceException` on `Component.gameObject` inside `ChestWindowUi.Open`.

The second half is worse: `openButton` is a static that is never cleared, so it can hold a
**destroyed** `MyButton` from a previous run. Assigning that back produces the same crash one step
later, and survives until the game is restarted — which is what #93 reports.

A diagnostic prefix logging which field is null was already in the build before this audit; it
should now name the field on the next occurrence.

---

## What this branch changes

| Finding | Change |
|---|---|
| SE-4 | **Failsafe.** `EncounterService` starts an unscaled clock when a peer reports; `EncounterWindows.LateUpdate` (which runs at `timeScale 0`) checks it, and after `WaitFailsafeSeconds` (20s) the peer calls `ForceCloseEncounter`. A host broadcasts `CloseEncounter`; a client re-sends its own `EncounterClosed` — the likeliest reason it is still waiting is that the host never counted its report, and re-reporting releases everyone rather than only itself. |
| SE-1, SE-2 | `ClearClosedEncounters()` now also resets the failsafe clock, and is called from `SynchronizationService.Reset()` and `PrepareForNextLevel()`, so no barrier state crosses a stage change or a session. |
| SE-3 | After reporting, `RewardFinished_Prefix` checks whether the round was released re-entrantly (`IsWaiting` is false once `ClearClosedEncounters` has run) and returns without the "waiting" UI. |
| SE-8 | `OnCloseEncounter` null-guards `UiManager.Instance` and `encounterWindows`. |
| SE-9 | `b_open` is restored *before* the routine check and only from a live button; `ChestWindowUiPatches.Reset()` and `LevelUpScreenPatches.Reset()` drop the statics, and `NetworkHandler.ResetNetworking()` calls both. |

**The failsafe is a floor, not a fix.** It converts every unknown barrier hole — including ones
this audit has not found — into a 20-second stall with a `Shared-experience failsafe fired after
…s` line in the log naming the peer it happened on. That log line is the thing to collect: it says
whether a hole remains after SE-1/2/3 are closed.

Chosen at 20s rather than #88's suggested 60s because a 60-second stall is indistinguishable from
a hang to a player, and the failsafe is not meant to be reachable in normal play.

---

## What still needs a wire change

The barrier has no round identity, and that is the root of SE-1, SE-5 and SE-6. The fix is a
monotonically increasing round id, allocated by the host and carried on both messages:

- host increments `encounterRound` when it opens a barrier and stamps `CloseEncounter` with it;
- `EncounterClosed` carries the round the peer is reporting for;
- the host ignores reports for a round it is not in; peers ignore a release for a round they are
  not in.

That is what makes a late report harmless and a stale release impossible, and it fixes SE-6 as a
side effect (the host can define the participant set when it opens the round).

**Blocked, deliberately.** Adding a field to `EncounterClosed` or `CloseEncounter` is precisely the
hazard `CLAUDE.md` calls out: MemoryPack serializes positionally, so a version-mismatched pair
would silently corrupt these messages rather than fail. Two routes:

1. append **new** union tags (`EncounterClosedV2`, `CloseEncounterV2`) — allowed, since tags are
   append-only, and old peers simply never send them; or
2. land the protocol version gate first ([P1-3](01-critical-fixes.md#p1-3), deferred to the
   Steamworks migration) and then change the messages in place.

Route 1 is available today and is the recommended next step for this area.

---

## Upstream issue evaluation

`Fcornaire/megabonk-together`. Assessed against this fork's code; upstream may differ where this
fork has already diverged.

| Issue | Assessment |
|---|---|
| **#93** — soft lock when opening chest, `NullReferenceException` in `ChestWindowUi.Open` | **Re-audited in [SE-13](#se-13).** The NRE is [SE-9](#se-9) and the "second chest" timing confirms it; fixed here, with a stated caveat about how `b_open` gets nulled in a shared-experience session at all. The banish-token half is [SE-12](#se-12)'s undiagnosed defect. Almost certainly the same underlying issue as #81. |
| **#88** — permanent softlock at "waiting for other player's choice" | **Same family as SE-1/SE-2.** The reporter's own workaround ("one player can unfreeze the other through interaction") is the stale-barrier signature. Their requested 60s failsafe is implemented at 20s. The other two items in that issue are unrelated to shared experience — see below. |
| **#37** — chest selection cannot be waited out / no pause on interactions | The first complaint maps to **SE-5** (a duplicate release closing a window the player was still using). The "no pause" complaint is by design outside shared experience: `MyTimePatches` blocks `MyTime.Pause` entirely in non-shared netplay, because one player's menu must not freeze everyone. Shared experience is the mode that pauses. The reset complaint is unrelated (see #90's family). |
| **#80** — "waiting for other players stuck" | No log in the issue, but the title is SE-1's exact symptom. Expect the failsafe to convert it into a 20s stall; if it still occurs afterwards, the failsafe log line names the peer. |
| **#76** — shared-experience chest soft-lock (closed) | **Audited in [SE-11](#se-11).** The fix (upstream `8bd58a6`, v4.0.3 — shared gains / local losses, plus a `!chest.CanAfford()` opt-out) is correct for its trigger and is present here. Two defects *in the fix* are fixed on this branch: no duplicate-delivery guard on the delta gold, and an unguarded deref on a per-coin network path. The reporter's actual request — identical prices for everyone — was not implemented, by design. |
| **#81** — shared-experience chest soft-lock (closed) | **Audited in [SE-12](#se-12): closed without any fix.** No commit after the report touches chests or encounters, and a later commit adds logging to hunt a chest bug. The deadlock half is [SE-1](#se-1) — introduced by v4.0.1, the release the reporter was on, whose own changelog claims to prevent deadlocks. The "only banish after the first item" half is undiagnosed; a diagnostic for the leading hypothesis is now in the build. |
| **#77** — random freezes with IL2CPP + shared experience | Consistent with the barrier, but "random freeze" also covers the main-thread stalls this fork has been fixing elsewhere. Not attributable without a log. |
| **#74** — shared XP breaks after many levels | ⚠️ **Reassessed after reading the issue body — the first assessment here was wrong.** It is a barrier freeze, not XP arithmetic. Full analysis in [SE-10](#se-10). |
| **#88 (2nd item)** — quantity tome / projectile-count shrine do nothing for guests | Not shared-experience. Not evaluated in this audit. Starting point: the projectile patches index by `projectileIndex` against `attackQuantity` (`ProjectileAxePatches.CalculateAngleOffset`), and remote spawns are reconstructed from the sender's message rather than re-simulated, so a stat that changes projectile *count* is the kind that would not survive that path. Needs its own investigation. |
| **#88 (3rd item)** — graveyard final boss cannot be damaged by guests | Not shared-experience. Damage authority — `Plugin.CAN_DAMAGE_ENEMIES` and the boss-room patches — is the place to look. Related to **#66** ("one player cannot see boss", closed). Not evaluated. |
| **#91** — guest stuck at "waiting for host", softlock after starting map | Lobby/handshake, not the encounter barrier. Overlaps this fork's own open work on the two disconnect paths racing ([P1-8](01-critical-fixes.md#p1-8)). Not evaluated. |
| **#90** — progression only saved for the host | This fork already fixed a defect in this area: [P0-5](01-critical-fixes.md#p0-5), the netplay flag that `SaveManagerPatches` reads was never cleared on teardown, so **singleplayer kept skipping saves after any netplay session**. That is a superset of the reported symptom for the guest. Worth re-testing here before treating it as open; note `ModConfig.AllowSavesDuringNetplay` also exists. |
| **#86** — crash on startup on Linux with BepInEx | Environment, not netplay. See [`../PROTON_SETUP.md`](../PROTON_SETUP.md). |
| **#83** — error while hosting/connecting (closed) | Matchmaking. Not evaluated. |

---

<a name="se-10"></a>
## SE-10 — why the freeze correlates with level count (upstream #74)

**This entry exists to correct an error in the table above.** #74 was first assessed from its
title, "Shared XP breaks after many levels", and mapped to [SE-7](#se-7) — the XP merge rule. The
issue body says something else:

> "after levelling up many many times e,g 100-200, the game eventually desyncs i guess? and we both
> freeze" … both players stuck on "waiting for other player to make choice" after selecting
> upgrades, preventing the run from finishing.

That is the **encounter barrier**, not XP arithmetic. The XP defect in SE-7 is real and separate;
it is not what #74 reports. The lesson is the one this repo keeps relearning: read the report, not
its title.

### Why "after 100-200 level-ups" and not before

**Every level-up is a barrier round.** With shared experience each player's pickups feed everyone's
XP, so the round rate is roughly the whole party's pickup rate. By level 100+ the rounds are
near-continuous, and two things that are unlikely per-round become near-certain:

1. **SE-1's trigger.** The release path exits early — leaving `forceClose` set — when the peer's
   `rewardQueue` is non-empty at that moment. The faster level-ups arrive, the more likely a
   release lands on a peer that still has one queued. Once per run is enough.
2. **Multi-level jumps.** This is where [SE-7](#se-7) feeds in, as a *cause* rather than the
   symptom. `OnReceivedAddXp` overwrites the receiver's absolute XP with the sender's. A peer whose
   value was behind therefore jumps forward by however much it had missed, which can cross several
   level thresholds at once, firing a burst of `AddEncounter` calls — all queued, since one is
   already in progress. That is precisely the non-empty-queue state SE-1 needs.

So SE-7 and SE-1 are one failure chain: a merge rule that produces bursts, feeding a release path
that mishandles bursts. Fixing either weakens the chain; fixing SE-7 alone would not have closed
#74, and mapping #74 to SE-7 alone (as the table originally did) would have led to the wrong fix.

**UNVERIFIED:** that `PlayerXp.AddXp` processes several level thresholds in one call is inferred
from how the mod uses it (`AddXp(0)` after overwriting the total is only meaningful if the method
re-evaluates thresholds). The stripped interop assemblies have no body. Check against the dump
before relying on it.

### Is #74 fixed on this branch?

| Component of #74 | Status |
|---|---|
| **The run-ending part** — "we both freeze", unrecoverable | **Bounded, not fixed.** [SE-4](#se-4)'s 20s failsafe releases both peers and logs. A permanent freeze is no longer reachable, whatever the cause. |
| The most level-rate-sensitive poisoning path | **Fixed** — SE-1. |
| Barrier state surviving stage changes and sessions | **Fixed** — SE-2. |
| Release-during-release leaving a peer on a dead "waiting" screen | **Fixed** — SE-3. |
| NRE in the release path poisoning the barrier | **Fixed** — SE-8. |
| **Root cause: no round identity** | **Not fixed** — SE-5, SE-6. Needs the wire change described above. |
| **XP merge producing the bursts** | **Not fixed** — SE-7. |

**Verdict: not resolved, but no longer run-ending — pending in-game confirmation.** None of this
has been built or played. The expected change in behaviour is that #74's "we both freeze and the
run is over" becomes "a 20-second stall, then play continues", with
`Shared-experience failsafe fired after …s` in the log each time.

**That log line is the measurement.** At level 100+ in a 2-3 player shared-experience run:

- **absent** → SE-1 was the whole of #74 on this fork and the barrier is healthy;
- **present but rare** → a residual attribution hole; the round id (SE-5) is the fix, and the line
  names the peer it happened on;
- **present often** → the barrier is still being poisoned every few rounds; do not ship shared
  experience on the failsafe alone, and prioritise the round id.

### Measured — Run C, 2 players, level 113

> **Result: present but rare.** Four stage transitions, level 113, shared experience on.
> **One** `Shared-experience failsafe fired after 20.0s` line, on the client
> (`re-reporting to the host`). No `[chest #81]`, no `[chest #93]`.
>
> So the middle branch above: **a residual attribution hole remains, and SE-5's round identity is
> the fix.** #74's run-ending freeze is now a single 20-second stall across 113 levels — bounded,
> not fixed.
>
> Two caveats on how much this is worth. It is **2 players**, the minimum, where the barrier is at
> its least contended; three or more is where SE-6's "counted but cannot report" case opens up. And
> the run did not deliberately force overlapping chest windows, which is SE-1's specific trigger —
> so this measures ordinary play at a high level rate, not the adversarial case.
>
> A separate earlier run at 2 players over the internet also fired the failsafe exactly once, on
> the host, which is consistent.
>
> See [`12-session-handover.md`](12-session-handover.md).

### Recommended next step, not taken here

Make `OnReceivedAddXp` apply the **delta** (`Amount`, already on the message) instead of
overwriting with the absolute `Xp`. It converges — every peer sees every delta exactly once, over
`ReliableOrdered`, with the host relaying to everyone but the sender — and it removes the
multi-level bursts that feed SE-1.

Two things to handle when doing it:

- **Ignore an `AddXp` whose `OwnerId` is the local player.** Under absolute semantics a copy
  echoed back to the sender is a harmless no-op; under delta semantics it double-counts
  permanently. The relay's sender-exclusion filter is UNVERIFIED (`RelayEnvelope.ToFilters`, open
  item 9 in [`06-session-handoff.md`](06-session-handoff.md)), so this is not hypothetical.
- **Deltas cannot self-heal.** The absolute value silently repairs any divergence on the next
  pickup; a delta stream does not. That is the trade, and it is the right one only because the
  channel is reliable and ordered and there is no mid-run join.

It is left out of this branch deliberately: it changes progression pacing, and landing it in the
same build as the barrier fixes would make the playtest above unreadable.

---

<a name="se-11"></a>
## SE-11 — audit of upstream #76 and the fix that closed it

**#76** ("Shared Experience chest soft-lock", closed) reported: three players, chest prices of
400 / 500 / 600 and gold of 1000 / 2000 / 300 — **the two players who *could* afford the chest
froze, while the one who could not kept moving**, with a repeating
`NullReferenceException` in the log.

### The fix

Upstream `8bd58a6`, shipped as v4.0.3, and it is in this fork. Two changes:

1. **Gold model reworked.** Only *gains* are shared — `ChangeGold_Postfix` returns early on
   `amount < 0` — and `OnReceivedChangeGold` applies the delta. The previous version computed a
   delta against captured state and clamped at zero on both sides.
2. **A chest opt-out**: `if (GameManager.Instance.player.IsDead() || !chest.CanAfford())` → pause,
   report to the barrier, show "Waiting for other player(s) choices in Chest…".

### Is the fix right? Yes — and the diagnosis behind it is worth stating, because it was left implicit

The inverted symptom is the tell. Before the fix, the peer who *could not afford* fell into the
`else` branch and called `chest.Interact()` anyway. Whatever the game does there for a player who
cannot pay, it did **not** open a reward window — so that peer never reported to the barrier and
never paused, which is why it stayed mobile. The two who could afford opened their windows, chose,
reported, and then waited forever for a third report that was never coming. The opt-out fixes
exactly that.

### But it is an allowlist, and the failure mode of a missing entry is a permanent freeze

`OnReceivedInteractableUsed` is a chain of `if (component != null)` branches, and the ones that can
opt a peer out of a barrier round are enumerated by hand: microwave-without-item, microwave with
too few unique items, balance shrine when dead, moai when dead, **chest when dead or unaffordable**,
shady guy when dead, and object-not-found. Each was added as its own bug report came in — #76 is
the chest entry.

Every unenumerated way of not reaching a reward window is the same bug wearing different clothes.
Ones visible in the code today:

- `PopReward_Prefix` returns early, without reporting, when `!CanInput()` ([SE-6](#se-6));
- a reward window that throws on open never reports — which is [SE-9](#se-9) / #93, and would
  explain #76's *repeated* NRE if it landed there;
- interactable branches with no opt-out at all (`shrineCursed`, `shrineGreed`, `shrineMagnet`,
  `shrineChallenge`, the boss spawners) — none currently open a reward window, so none needs one
  *today*, which is exactly the kind of assumption that breaks when the game updates.

**This is why the failsafe in [SE-4](#se-4) matters more than any individual entry in that list.**
The allowlist makes known cases correct; the failsafe makes unknown cases survivable. They are
complementary, and #76's history — three separate chest soft-lock issues (#76, #81, #93) closed or
open over four months — is the argument for having both.

### Two defects found in the fix itself, and fixed here

**The delta gold model has no protection against a duplicate delivery.** Under the old absolute
model, a `GoldChanged` echoed back to its own sender was a harmless no-op. Under a delta it is a
permanent overcount. The host excludes the sender when relaying, but that exclusion goes through
`SendToAllClientsExcept`, whose relay branch **falls back to an empty filter list on a lookup
miss** — the UNVERIFIED item scheduled for Steamworks Phase 1. `OnReceivedChangeGold` now ignores
any `GoldChanged` whose `OwnerId` is the local player, which makes the delta model safe regardless
of how that resolves. (The same guard is a prerequisite for moving XP to deltas — see
[SE-10](#se-10)'s recommendation.)

**`OnReceivedChangeGold` dereferenced `GameManager.Instance.player.inventory` unguarded**, from a
network callback, on a path that fires for *every coin picked up by any player*. During teardown or
a stage change that is a repeating `NullReferenceException` — a better match for #76's "repeated"
NRE than any one-shot UI failure. Now guarded.

### What the fix deliberately does not do

The reporter asked for **identical gold prices for everyone**. The fix does not do that, and the
issue was closed anyway. With gains shared and losses local, balances diverge by exactly what each
player has spent, while the game's own per-player price escalation diverges too — so `CanAfford()`
disagreeing between peers is not an edge case, it is the steady state, and the opt-out will keep
firing for the rest of the run. That is a defensible design (you spend your own gold), but it is a
different design from the one requested, and it means #76's *underlying* asymmetry is still there
by choice. Worth knowing before treating the chest opt-out as a bug rather than the intended
behaviour.

---

<a name="se-12"></a>
## SE-12 — audit of upstream #81 (closed **without a fix**)

**#81** ("Soft lock when opening chest in shared experience", closed): *"you can collect your first
item, after this every chest will only give you the banish option"*, escalating to **seven banish
screens in a row** and both players trapped on the banish screen. Every run. Mod version **4.0.1**.

### There is no fix. The issue was closed anyway.

Checked against this fork's history, which contains upstream's:

| Commit | Date | What |
|---|---|---|
| `61f2607` | Feb 22 | v4.0.1 — "attempt to fix multiple encounter bug … should prevent deadlocks" |
| — | **Feb 25** | **#81 reported, on 4.0.1** |
| `8bd58a6` | Feb 24 | v4.0.3 — gold rework + the chest `CanAfford` opt-out ([SE-11](#se-11)) |
| `ba18f51` … `24f5004` | Feb 27 – Mar 17 | optimizations, damage rework, packet delivery, tomes |
| `041881b` | later | *"chore: added some logs to identify a chest open issue"* |

Nothing after the report touches chests or encounters. The last entry is the tell: the maintainer
was **still hunting a chest bug** after #81 was closed. Treat #81 as open.

### The deadlock half is SE-1, and v4.0.1 is where it came from

`61f2607` is the commit that added these three lines to `RewardFinished_Prefix`:

```csharp
var currentQueue = __instance.rewardQueue;
if (currentQueue.Count > 0) //Keep popping reward until queue is empty
{
    return true;
}
```

That is [SE-1](#se-1) verbatim — the release path that exits without clearing the barrier. So the
release shipped to fix an encounter deadlock **introduced the deadlock this audit traced**, and
#81 arrived three days later reporting "both players trapped". Its "seven banish screens in a row"
is the other half of the same commit: `AddEncounter_Prefix` queues every encounter that arrives
while one is in progress, and in shared experience every peer's chest interaction feeds that queue,
so seven queued encounters pop one after another.

**That part is fixed on this branch** (SE-1, SE-2, SE-3), with [SE-4](#se-4)'s failsafe behind it.

### The "only banish" half is NOT explained, and is not fixed — LIKELY hypothesis only

The mod never patches the take/banish buttons; nothing in `ChestWindowUiPatches` or
`OpenChestPatches` touches them. So a chest that offers only banish is the *game* deciding that,
and the only way this mod plausibly reaches that decision is by stranding a game static it borrows.

`NetPlayer.AddItem` / `RemoveItem` null out `ItemInventory.A_ItemAdded` / `A_ItemRemoved` while
applying a remote player's item and put them back afterwards — **and until [P1-9](01-critical-fixes.md#p1-9)
on this branch there was no `finally`**. One throw in `itemInventory.AddItem` or
`EffectManager.Instance.OnItemAdded` left the game's own item callbacks dead for the rest of the
process.

"The first item works and every chest after it is broken, until you restart the game" is precisely
that shape. It is **LIKELY, not CONFIRMED**: proving that a null `A_ItemAdded` degrades the chest's
take option needs the IL2CPP dump, and the stripped assemblies have no bodies.

**A diagnostic now tests it directly.** `ChestWindowUiPatches` records whether those statics were
set when the first chest of the session opened, and logs `[chest #81]` once if they later become
null. It assumes nothing about their normal value — it reports a *transition*, so it is silent both
if they are normally null and if they are never stranded.

If that line appears, P1-9's `try/finally` is the fix and the exception it was hiding is in the same
log. If it does not, the "only banish" half needs the dump and a different hypothesis.

<a name="se-13"></a>
## SE-13 — re-audit of upstream #93 (open)

**#93** ("Soft lock when opening chest", open): shared experience, first chest gives both players an
item, **second chest freezes the game for everyone**, *"if any player lacks banish tokens for their
selected item, the screen remains permanently stuck"*, with a `NullReferenceException` on
`Component.gameObject` inside `ChestWindowUi.Open()`.

### #93 and #81 are almost certainly the same defect

Same "first chest fine, second chest broken" timing, same banish involvement, same mode. #93 adds
the stack trace #81 lacked, and was filed after #81 was closed — which is the usual signature of a
closed-not-fixed issue coming back.

### The NRE is SE-9, and the timing is what confirms it

[SE-9](#se-9) is exact about the second chest: `Open_Postfix` nulls `b_open` on the **first** chest;
`OnClose_Postfix` restored it only *below* an early return that fires whenever no invulnerability
routine is running; so the **second** `ChestWindowUi.Open()` dereferences null — a
`NullReferenceException` on a `Component`, which is #93's stack. Fixed on this branch: restored
before that return, only from a live button, and the statics are dropped on session teardown.

**One caveat, stated because it decides whether SE-9 is the whole story.** `Open_Postfix` returns
early in shared experience, so in a *pure* shared-experience session `b_open` is never nulled. For
SE-9 to fire in #93's scenario one of these must hold:

- `IsSharedExperienceEnabled()` was false at that moment — it returns false whenever
  `Mode.EnabledSharedExperience` has no value, i.e. before the match info has been applied; or
- the process had run a non-shared session earlier, and `openButton` / `b_open` state leaked
  across it — the statics were never reset until this branch.

Both are real and both are now closed. But if #93 recurs with the `[chest #93]` diagnostic showing
`b_open=False`, then the NRE is coming from a different field and SE-9 is not the cause.

### Why it freezes *everyone*, not just the player who hit it

Because a broken chest window never reports to the barrier. Whatever stops one peer choosing — an
NRE mid-`Open` (SE-9), or a banish-only window on a player with no banish tokens (SE-12's
undiagnosed half) — that peer never reaches `RewardFinished`, and every other peer waits forever.
The NRE and the banish bug are two different faults funnelling into one symptom, which is why #93
reads as two issues in one report.

[SE-4](#se-4)'s failsafe bounds the symptom regardless of which fault caused it.

### Verdict

| Part of #93 | Status |
|---|---|
| `NullReferenceException` in `ChestWindowUi.Open` on the second chest | **Fixed** (SE-9), pending confirmation, with the caveat above |
| Everyone freezing behind the broken window | **Bounded** by the 20s failsafe (SE-4); the barrier holes it rode on are fixed (SE-1/2/3) |
| "Stuck when a player lacks banish tokens" | **Not fixed** — this is SE-12's undiagnosed half; a diagnostic is now in place |

---

## Fix order

The remaining work is not a list to burn down in any order — three later decisions are *gated on
data this branch produces*, and two of the changes will confound each other's playtest if they ship
together. Recommended sequence, with the reason each step is where it is.

### 0. Build and play what is already here. Change nothing first.

Everything on this branch is unbuilt and unplayed, and **three open questions are answered for free
by one session**:

| Log line | Decides |
|---|---|
| `Shared-experience failsafe fired after …s` — how often, per stage | whether [SE-5](#se-5)'s round identity is urgent or merely correct |
| `[chest #81]` | whether [SE-12](#se-12)'s "only banish" half is already fixed by [P1-9](01-critical-fixes.md#p1-9), or needs the dump |
| `[chest #93]` with `b_open=False` | whether [SE-9](#se-9) was really #93's NRE, or a different field is |

Adding anything before this makes a failure ambiguous, and makes the failsafe counter — the one
quantitative signal this area has ever had — meaningless as a baseline.

**Exit criteria:** it builds; a 3-player shared-experience run reaches stage 2; you have the three
logs.

### 1. Whatever the build breaks.

Stated as its own step because it is the only certainty: 20+ commits, no compiler in the
environment that wrote them. `scripts/checks/csharp_static_checks.py` covers syntax and block
scope, nothing else. The signature changes to look at first are listed in
[`06-session-handoff.md`](06-session-handoff.md).

### 2. [SE-7](#se-7) — shared XP as a delta.

**Why here, and not later:** it is small, it is independent of the barrier, and its one prerequisite
is already done — the duplicate-delivery guard, which landed with the gold path in
[SE-11](#se-11) and is the same guard XP needs. It also removes the multi-level bursts that feed
[SE-1](#se-1), so it *reduces* the load on the barrier before the barrier is redesigned.

**Why alone in a build:** it changes progression pacing. Shipped alongside anything else, a report
of "levelling feels different" cannot be attributed.

**Exit criteria:** two peers' level and XP totals stay identical over a full run; the failsafe count
does not increase.

### 3. [SE-12](#se-12)'s "only banish" half — but only in the branch the data picks.

- `[chest #81]` **appeared** → P1-9 is the fix; confirm the symptom is gone and close it. Cheap.
- `[chest #81]` **absent and chests still misbehave** → the hypothesis is wrong. This becomes a
  reverse-engineering task against `dump.cs` (what makes `ChestWindowUi` offer banish only), not a
  netcode one, and it should be scheduled as such rather than guessed at. Three issues have already
  been closed on guesses here.

### 4. [SE-5](#se-5) + [SE-6](#se-6) — round identity. The actual root cause.

Last of the code work, deliberately:

- it is the largest change and the only one that touches the wire (append new union tags —
  `EncounterClosedV2` / `CloseEncounterV2` — rather than adding fields to the existing messages,
  which is the positional-corruption hazard);
- by then the load-side confounder (XP bursts) is gone, so the failsafe count is a clean
  before/after measurement of whether round identity actually closed the remaining holes;
- **SE-6 falls out of it and should not be done separately.** Once the host opens a numbered round
  it can name the participant set at the same moment, which is the only non-racy way to answer "who
  is expected to report". Fixing SE-6 on its own — e.g. counting only players with an encounter
  open — replaces one race with another.

**Exit criteria:** the failsafe stops firing. That is the whole point of the ordering: after step 4
the failsafe should be dead code in practice, and if it still fires, something is left.

### 5. Only then, reconsider the failsafe itself.

Not before. Once it is provably unreachable, the choice is to lower the 20s or promote its log from
a warning to an error — the *absence* of the line is the health signal. **Do not remove it.** The
history in this document is four separate softlock issues over four months, three of them closed
without a mechanism; the failsafe is the only thing that makes the next unknown one survivable
rather than run-ending.

### Two rules that apply across all of it

**One fix per release, with its changelog entry.** Upstream's issue history is hard to attribute
precisely because it is not: #81 was filed against 4.0.1 while 4.0.3 was already out, and the
"multiple encounter" fix that *introduced* [SE-1](#se-1) shipped in the release that claimed to
prevent deadlocks. A player report is only useful if it names a version that means one thing.

**Leave the opt-out allowlist alone until step 4.** The hand-enumerated cases in
`OnReceivedInteractableUsed` ([SE-11](#se-11)) are load-bearing today. They should be re-derived
*after* round identity exists — at that point "who is in this round" is explicit and the allowlist
can be replaced by it, rather than being pruned on a hunch beforehand.

---

## How to test this

Shared experience, **3 players** — two is not enough, for the same reason the disconnect work needed
three ([`06-session-handoff.md`](06-session-handoff.md)).

1. **The reported repro.** Have two players open chests at nearly the same time, repeatedly, with
   at least one player levelling up during a chest window (that is what fills `rewardQueue` and
   drives SE-1). **Expected:** nobody is left behind a "Waiting…" screen after the others have
   chosen; no `Shared-experience failsafe fired` line.
2. **Deliberately provoke the failsafe.** Have one player alt-tab and sit on their reward window for
   30 seconds. **Expected:** every other peer releases after 20s with the failsafe log line, the run
   continues, and the slow player's own window still works.
3. **Across a stage change and a new session** (SE-2): finish a stage while an encounter is open,
   and start a second run without restarting the game. **Expected:** the first encounter of the new
   stage/run behaves normally — neither instant nor stuck.
4. **#93** (non-shared experience): open chests while teleporting/unable to input, then open another
   chest. **Expected:** no `[chest #93]` diagnostic line, no NRE in `ChestWindowUi.Open`.
5. **#76's repro** ([SE-11](#se-11)): three players with deliberately different gold, one of them
   unable to afford the chest. **Expected:** the player who cannot afford is paused with the
   "Waiting … in Chest" text and everyone releases together; no repeated
   `NullReferenceException`; gold gains stay equal across peers while spending diverges.
6. **#81 / #93** ([SE-12](#se-12), [SE-13](#se-13)): shared experience, open **several chests in a
   row** across the party and take an item each time. **Expected:** every chest offers take/banish
   normally, no `[chest #81]` or `[chest #93]` line, and nobody is trapped on a banish screen. The
   `[chest #81]` line, if it appears, resolves the one hypothesis this audit could not settle.
7. **#74's condition** ([SE-10](#se-10)): a long shared-experience run, past level 100. This is the
   one that needs *duration* rather than a specific action — the defect is a per-round probability,
   so it only shows up once the rounds are near-continuous. Count the
   `Shared-experience failsafe fired` lines per stage; that number is the finding, whatever it is.

Collect all three logs. The lines that matter: `Shared-experience failsafe fired`, `[chest #93]`,
and anything from `OnCloseEncounter`.

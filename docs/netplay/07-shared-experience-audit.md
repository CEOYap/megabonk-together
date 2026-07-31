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

### SE-2 — the barrier survives teardown and stage changes — CONFIRMED

`closedEncounterPerPlayer` and `forceClose` were cleared **only** by a successful release.
`SynchronizationService.Reset()` (session teardown) and `PrepareForNextLevel()` (stage change) did
not touch them. A session that ended mid-encounter therefore poisoned the next one, in either
direction — instant release with nobody reporting, or a barrier that can never complete. It also
explains reports where only a full game restart recovers.

### SE-3 — the last player to choose is shown "Waiting…" for a round that is over — CONFIRMED

`synchronizationService.RewardFinished()` can complete the round **synchronously**: on the host it
counts itself, sees `IsClosable()`, and re-enters `RewardFinished_Prefix` through
`OnCloseEncounter` → `encounterWindows.RewardFinished()`. When the stack unwinds, the outer call
carries on into its "I am blocked" branch — hiding `activeEncounterWindow`, disabling the UI
particles, showing "Waiting for other player(s) choices…" and returning `false` — for a round that
has already been released.

Cosmetically this is the "stuck" screen players report even when the game has resumed. It also
leaves `activeEncounterWindow` inactive and the particle renderers disabled.

### SE-4 — no failsafe: any hole is permanent — CONFIRMED (this is upstream #88's request)

Nothing bounded the wait. Every hole above, and any not yet found, becomes an unrecoverable run.

### SE-5 — a release can be generated for a round nobody is in — CONFIRMED

`IsClosable()` stays true from the moment the count is met until something clears it, and both the
host's `RewardFinished()` and the `EncounterClosed` handler broadcast `CloseEncounter` whenever
they observe it. A late report for a round that has already been released therefore triggers a
**second** broadcast. A peer that has since opened its next encounter receives it and closes that
window immediately — losing that player's pick.

This is the likeliest explanation for upstream #37's first complaint: *"if player1 selects a chest,
he can not wait for the random selection or stop it"* while player 2 is pressing buttons.

### SE-6 — `IsClosable()` counts every player, including ones that cannot report — CONFIRMED, not fixed

`closedEncounterPerPlayer.Count >= playerManagerService.GetAllPlayers().Count()`. The count is
taken live, so it moves under the barrier. Disconnects are handled (the disconnect path re-checks
and force-closes — see [P1-8](01-critical-fixes.md#p1-8), which also stopped that check being
skipped when the record was already gone), but a player who is loading, mid-teleport, or otherwise
never reaches an encounter is still counted as a participant.

`PopReward_Prefix` returns early — without reporting — when `!CanInput()`. If that condition
persists, that peer never joins the round. With the failsafe in place this now resolves in
`WaitFailsafeSeconds`; without a round identity it cannot be fixed properly.

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
| **#93** — soft lock when opening chest, `NullReferenceException` in `ChestWindowUi.Open` | **Root cause identified: SE-9**, and fixed on this branch. The reporter's "if any player lacks banish tokens the screen stays stuck" is a second, separate path worth reproducing — that is the barrier, not the NRE. |
| **#88** — permanent softlock at "waiting for other player's choice" | **Same family as SE-1/SE-2.** The reporter's own workaround ("one player can unfreeze the other through interaction") is the stale-barrier signature. Their requested 60s failsafe is implemented at 20s. The other two items in that issue are unrelated to shared experience — see below. |
| **#37** — chest selection cannot be waited out / no pause on interactions | The first complaint maps to **SE-5** (a duplicate release closing a window the player was still using). The "no pause" complaint is by design outside shared experience: `MyTimePatches` blocks `MyTime.Pause` entirely in non-shared netplay, because one player's menu must not freeze everyone. Shared experience is the mode that pauses. The reset complaint is unrelated (see #90's family). |
| **#80** — "waiting for other players stuck" | No log in the issue, but the title is SE-1's exact symptom. Expect the failsafe to convert it into a 20s stall; if it still occurs afterwards, the failsafe log line names the peer. |
| **#81, #76** — shared-experience chest soft-lock (both closed upstream) | Same family. Closed upstream without a mechanism being named, so treat them as evidence the symptom recurs rather than as fixed. |
| **#77** — random freezes with IL2CPP + shared experience | Consistent with the barrier, but "random freeze" also covers the main-thread stalls this fork has been fixing elsewhere. Not attributable without a log. |
| **#74** — shared XP breaks after many levels | **SE-7**: last-writer-wins on an absolute XP value discards concurrent pickups. Mechanism identified, **not fixed** — the fix changes merge semantics and wants a playtest. |
| **#88 (2nd item)** — quantity tome / projectile-count shrine do nothing for guests | Not shared-experience. Not evaluated in this audit. Starting point: the projectile patches index by `projectileIndex` against `attackQuantity` (`ProjectileAxePatches.CalculateAngleOffset`), and remote spawns are reconstructed from the sender's message rather than re-simulated, so a stat that changes projectile *count* is the kind that would not survive that path. Needs its own investigation. |
| **#88 (3rd item)** — graveyard final boss cannot be damaged by guests | Not shared-experience. Damage authority — `Plugin.CAN_DAMAGE_ENEMIES` and the boss-room patches — is the place to look. Related to **#66** ("one player cannot see boss", closed). Not evaluated. |
| **#91** — guest stuck at "waiting for host", softlock after starting map | Lobby/handshake, not the encounter barrier. Overlaps this fork's own open work on the two disconnect paths racing ([P1-8](01-critical-fixes.md#p1-8)). Not evaluated. |
| **#90** — progression only saved for the host | This fork already fixed a defect in this area: [P0-5](01-critical-fixes.md#p0-5), the netplay flag that `SaveManagerPatches` reads was never cleared on teardown, so **singleplayer kept skipping saves after any netplay session**. That is a superset of the reported symptom for the guest. Worth re-testing here before treating it as open; note `ModConfig.AllowSavesDuringNetplay` also exists. |
| **#86** — crash on startup on Linux with BepInEx | Environment, not netplay. See [`../PROTON_SETUP.md`](../PROTON_SETUP.md). |
| **#83** — error while hosting/connecting (closed) | Matchmaking. Not evaluated. |

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

Collect all three logs. The lines that matter: `Shared-experience failsafe fired`, `[chest #93]`,
and anything from `OnCloseEncounter`.

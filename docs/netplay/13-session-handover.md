# Handover — the bandwidth session

Supersedes [`12-session-handover.md`](12-session-handover.md). Read this first, then
[`06-session-handoff.md`](06-session-handoff.md) for the standing queue.

Branch: `claude/microwave-affordability-barrier` → **[PR #4](https://github.com/CEOYap/megabonk-together/pull/4)**, 10 commits, pushed and open.

**Host egress peak fell 321.1 → 154.9 → 98.6 KB/s across three two-player sessions, and the
measurements were made trustworthy before they were made smaller.** Two soft-lock-class fixes
landed, one of them diagnosed from the first exception this project has ever captured with a usable
stack. Four docs corrected or added, including one recorded finding that turned out to be false and
had already been acted on.

**If you read two things:** *Start here next session* below, and *The pre-Steamworks task list*,
which is what the next branch is for.

---

## Start here next session

The next branch is **pre-Steamworks cleanup**. Order matters; item 1 unblocks the rest.

1. **Rewrite [`02-delivery-method-reference.md`](02-delivery-method-reference.md).** It is stamped
   `main @ 041881b` (~90 commits stale) and its map is scoped to `SynchronizationService.cs` only.
   The four periodic host broadcasts live in `UdpClientService.cs` and are **absent entirely** —
   `SendLobbyUpdate`, `SendEnemiesUpdate`, `SendProjectilesUpdate`, `SendTumbleWeedsUpdate`. Those
   are, by measurement, **over 90% of host egress**. Union tag 68 (`PlayersStateUpdate`) is missing
   too. The migration plan names this doc as a Phase 0 prerequisite — *"the reliability map must be
   correct and documented before it is translated to a new API"* — so it is blocking, and it needs
   no playtest.
2. Then the rest of *The pre-Steamworks task list*.

---

## What landed, and what is actually verified

### Verified in play (two-player session, 2026-08-06, direct P2P @ 65 ms rtt)

| Thing | Commit | Evidence |
|---|---|---|
| **Player stream split** | `a79ea0c` | `PlayersStateUpdate` **98 B/send** — the harness predicted exactly 98. Combined player traffic **19.98 → 7.34 KB/s, a 63% cut**. Heartbeat steady at 5.0/s (max 5.1), so the readiness force-send is not firing spuriously |
| **Sub-MTU chunking** | `0b0dc97` | Projectiles reached 3.9 chunks/tick, enemies 2.5 — so splitting fires under load. **No stream's B/send reached the 1000 B cap** (max 725/733, against 2100/4761 before), i.e. nothing was promoted to `ReliableOrdered` |
| **`[bw]` label split** | `574a252` | Immediately overturned the standing attribution — see below |
| Host egress overall | — | peak **321.1 → 154.9 → 98.6 KB/s**; median **32.1 → 20.8** |

### Landed, NOT run in-game

| Commit | What | Why it is unproven |
|---|---|---|
| `c082f8c` | Microwave affordability barrier | Needs two peers with *different gold* to reproduce |
| `f79da5e` | Null `damageSource` guard | Observed once in one session; rare by nature |
| `1a02504` | Budgeted enemy stream | It ran, but **the per-tick cap never bound** — peak was ~130 enemies/tick against a 240 budget. The deferral behaviour it exists for is untested |

### The attribution that was wrong for three sessions

"`LobbyUpdates` is 65–90% of host traffic, and that is where bandwidth work belongs" was **right
about the bucket and wrong about the stream**. `SendLobbyUpdate` and `SendEnemiesUpdate` share the
`LobbyUpdates` type, so one `GetType().Name` label merged them. Split:

```
LobbyUpdates(players)   19.98 KB/s   flat (min 19.98, max 20.05)   ← the real target
LobbyUpdates(enemies)    3.31 KB/s   median, spiky to 76
```

The *player* stream was the constant floor, re-sending names, skins, character ids and inventories
sixty times a second. Everyone had been optimising the enemy stream.

**The lesson is about the counter, not the streams:** a diagnostic that cannot separate two things
will be believed anyway. It cost three sessions of pointing at the wrong stream.

---

## The pre-Steamworks task list

From a full re-audit of `docs/` against the repo. This is the next branch's scope.

| # | Task | Blocking? | Notes |
|---|---|---|---|
| 1 | **Rewrite the delivery-method reference** | **Yes** — named Phase 0 prerequisite | See *Start here*. No playtest needed |
| 2 | **Fix host BepInEx Unity logging** | Effectively yes | Host has logged **zero** Unity-sourced lines for three sessions. Config says `UnityLogListening = true`; the peers run different BepInEx builds (client be.755, host be.785). Until this is fixed, every "the host does not have this" statement in the repo is unprovable, and Phase 4's "verified under 3% packet loss" cannot be judged |
| 3 | **Trace `RelayEnvelope.ToFilters`** | Yes | Still UNVERIFIED. The plan says explicitly it must not cross the Phase 1 seam unexamined, because after the seam the call sites no longer show the id-space split that makes the hazard visible |
| 4 | **Lobby-ready defects A–D + SE-5 together** | No, but cheapest now | The 0.4.3 re-audit found a shipped implementation pairs retry with `(sessionId, roundId)` stamping — A's fix and SE-5 are the same work. Doing it on the known-good transport is far cheaper than on a new one |
| 5 | Playtest the two unverified fixes | No | Microwave needs unequal gold; `damageSource` is opportunistic |
| 6 | `SendToClient(NetPeer)` → `(uint connectionId)` | No — Phase 1 proper | Listed here so it is not forgotten; it is the one remaining LiteNetLib leak in the interface |

**Explicitly NOT on this list, and struck for good:** *"capture a 6-player bandwidth baseline."* Only
two players are available to this project. It was an open blocker nobody could ever close, carried
across three handovers. Phase 0 now measures 2 in-game and **derives** 4 and 6 from serialized
payloads — a method validated by the 98-vs-98 prediction — with 6-player *behaviour* recorded as
accepted risk. See [`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md)
Phase 0. **Do not quietly re-add it to a later phase.**

---

## New this session: `08-observed-bugs.md`

Four entries in [`08-observed-bugs.md`](08-observed-bugs.md), two reported from play and two found
in the logs:

- **OB-1 Aegis orbit count differs per peer — CONFIRMED, cause exact.** `NetPlayer.cs:401` hardcodes
  `currentAmount = 2` for remote players and their `FixedUpdate` is suppressed, so it is frozen for
  the run. **It is a class, not one item** — the same literal is applied to Chunkers and every
  weapon in that switch is suppressed identically.
- **OB-2 Ghost item summons seen by non-owners — UNVERIFIED, and deliberately not called a bug.**
  The summons deal damage, and enemies here are host-authoritative and replicated, so both players
  seeing them may be correct. One decompile settles it (`SpawnGhost`, VA `0x180457EE0`).
- **OB-3 "Waiting for other player(s)" repeats every ~20 s — CONFIRMED.** Host logged five failsafe
  fires and the client zero, ~20 s apart, each reporting a *fresh* 20.0 s wait. The barrier re-arms
  indefinitely. Mechanism is SE-6, already confirmed and unfixed.
- **OB-4 The failsafe closes a window that is mid-use — CONFIRMED**, full call chain recorded. Same
  root gap as SE-5: a release carries no round identity, so it cannot be addressed to its round.

OB-3 and OB-4 are both arguments for task 4 above.

---

## Live leads, carried forward

### 1. The client NullReferenceException storm — narrowed, still undiagnosed

Still ~2,000–30,000 per session, no managed stack, every session. **Narrowed twice this session
without writing any code:**

- **Last session's discriminator held.** On the one stage of four with no world-gen suppression, the
  storm stopped dead — 2,421 lines with zero NREs while `Look rotation` continued. Every stage that
  logs `Skipping tile object / rail / chest / Quest spawning` storms; the one that does not, does
  not. Consistent across two further sessions.
- **New: managed exceptions here *do* produce full stacks.** The `damageSource` bug surfaced as
  `[Error :Il2CppInterop] During invoking native->managed trampoline` with a complete IL2CPP stack.
  The storm's NREs still carry none. So the storm is almost certainly **not** thrown through a
  managed trampoline — which is the "no managed frames, the thrower is native game code" branch
  `UnityExceptionDiagnostics` was written to distinguish.

### 2. Remote projectiles — unchanged, still needs a design pass

`Look rotation viewing vector is zero` went 23,622 → 0 → 870 across sessions. **Do not read the zero
as a fix**: that session had **no rockets at all** (`SpawnedRocketProjectile` absent from every
`[bw]` sample). Zero rockets, zero warnings — which *corroborates* the rocket attribution rather
than closing it. Four defects deep; do not patch it a fifth time.

---

## Lessons, each learned by being wrong

1. **A diagnostic that cannot separate two things will be believed anyway.** The merged `[bw]` bucket
   sent three sessions after the wrong stream. When adding a counter, ask what it *cannot*
   distinguish before trusting what it says.

2. **Absence of a log line is not absence of the event.** I concluded no coffin fight completed
   because there was no crypt-key traffic. Both players had received the key. The key's *appearance*
   needs no message at all, and the pickup path has **no log statement** — so it reads identically
   whether it ran or not. Before treating silence as a negative result, check that the path would
   have spoken.

3. **A finding about a moving target carries the build it was checked against, or it rots.** "Mod S
   has no retry logic — checked directly" was true when written, false at their 0.4.3, and had
   already been acted on: it was the stated reason lobby-ready work was deprioritised. §5 now carries
   a version stamp and an instruction to put one on every finding in it.

4. **Check whether an open blocker is achievable before carrying it.** "Capture at 6 players" was
   tracked across three handovers by a project with two players.

5. **Measure the thing you are about to change, then predict, then check the prediction.** The
   harness said `PlayersStateUpdate` would be 98 B/send; the build measured 98. That single match is
   what justified replacing an unachievable Phase 0 criterion with a derived one — the method had
   earned it.

6. **Read the compensations before declaring a failure chain.** I traced the coffin's decompiled
   `OnEnemyDied` into a confident prediction that the client never sees the crypt key, without first
   checking that `OnReceivedSpawnedEnemy` explicitly repopulates the set the suppression emptied.

---

## Standing rules, unchanged

- Nothing is verified until it has been run in-game; "builds clean" is stated as exactly that.
- One logical fix per commit; the body says what was deliberately not done and what is unverified.
- Deploy to every install and check the SHA256 matches. **Both peers need PR #4's build** — a peer
  without union tag 68 cannot decode the 60 Hz player stream.
- Remove other mods before a measured run.
- Netcode bugs are mostly invisible at 0% loss — and two instances on one PC is `rtt 0 ms`, which is
  not a LAN test either.
- **Copy both logs before relaunching.** A host log was lost to a game restart mid-analysis this
  session; `LogOutput.log` is overwritten on launch.
- `<Version>` is still 5.1.0 and untouched. Nothing has been released.

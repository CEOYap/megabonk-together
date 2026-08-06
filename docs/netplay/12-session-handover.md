# Handover — the second PC session

Supersedes [`11-session-handover.md`](11-session-handover.md). Read this first, then
[`06-session-handoff.md`](06-session-handoff.md) for the standing queue.

Branch: `claude/build-fixes-first-pc-session`, continuing from where `11` left off.

**26 commits. Six fixes are verified in-game, seven are built but unplayed, one long-running cosmetic
bug was traced to a defect in the client spawn path that affected far more than the thing it was
reported against, and the largest remaining unknown now has a diagnostic pointed at it.**

**If you read two things:** *Start here next session* immediately below, and *The two live leads*.
Those leads are the **client exception storm** (3,032 per session, host zero, instrumented and
awaiting a run) and the **remote-projectile representation**, now four defects deep and due a
rethink rather than a fifth patch.

> ⚠️ **The last build did not load.** `c09a487` failed with a `MissingMethodException` and BepInEx
> aborted the plugin. `0354024` fixes it but has not been launched. Confirm that first.

---

## Start here next session

1. **Launch the game.** `0354024` is unproven and the previous build did not load at all. Nothing
   else matters until the plugin loads.
2. **One short client session, under a minute**, then read `LogOutput.log` for stacks beneath the
   `NullReferenceException` lines. The sampler was lost with the delegate, so every exception now
   writes a stack — a minute is plenty for something that fires ~3,000 times a session, and much
   longer produces an unreadable log.
3. Everything else is in the two live leads above.

---

## Verified in-game

| Thing | Commit | Evidence |
|---|---|---|
| Stage-transition hang was **not ours** | — | A third-party mod on the client only. Removing it fixed transitions outright: `Lobby not ready yet` went 357/364 → 1/3 |
| Shrine charge-start replay | `7c8cdb2` | `No one is charging this shrine; ignoring stop` 4 → 0; second charger now recorded (`still charging` × 11) |
| **Client spawn ordering** | `1e62b31` | The headline fix — see below. Client `[shrine-move]` 3 → 0, rune stone `localPos` now matches host exactly |
| Zero-vector `LookRotation` guards | `16de1e4`, `45a1e3a` | Client `Look rotation viewing vector is zero`: 208,936 → 6,353 → 870. A third site was missed and guarded later in `00c8dc8`; the residual is discussed under the projectile rethink |
| **`[HarmonyFinalizer]` push/pop pairing** | `d350acc` | 36 sites. Verified over an internet session — see below |
| Rocket client suppression | `91d4673` | Partially confirmed: mid-flight explosions largely gone, **but not entirely** — see the projectile rethink |

### The finalizer conversion is verified, and the proof is worth keeping

`d350acc` converted 36 prefix/postfix state pairs to `__state` + `[HarmonyFinalizer]`. Whether a
finalizer fires around an IL2CPP-patched *native* method was the open question — the attribute
existing in `0Harmony.dll` is not the same claim.

Two counters settle it, and neither needed new code:

| Signal | Before | After (both peers, full internet session) |
|---|---|---|
| `Dropped N stale netplayer position request(s)` | — | **0** |
| Kill-attribution anomaly lines | dozens/run, `unset-while-clear` 1–41 | **0 lines — diagnostic entirely silent** |

`TrackerAttributionDiagnostics` returns early only when **all five counters are zero**, so silence
is a positive result rather than an absence. And the combination is conclusive: had the finalizers
never fired, the stale counter would have spiked (pushes never popped) *and* `overwrite-while-set`
would have spiked on the host (sets never cleared). Both zero, while `unset-while-clear` went from
non-zero every run to gone — which is the `ProjectileBase.HitEnemy` imbalance being fixed.

**Use the same trick again.** The cheapest verification for a large unverifiable change is an
existing counter that is silent when healthy and loud when not.

### Run C — the shared-experience answer

2 players, **level 113**, four stage transitions. **One** `Shared-experience failsafe fired` line,
on the client. No `[chest #81]`, no `[chest #93]`, no transform fallbacks.

Against [`07-shared-experience-audit.md`](07-shared-experience-audit.md#se-10)'s table: *present but
rare → a residual attribution hole; the round id (SE-5) is the fix.* Upstream #74's "we both freeze
and the run is over" is now one 20-second stall across 113 levels — **bounded, not fixed**, and now
measured rather than inferred.

---

## The spawn-ordering defect — the important finding

**`Awake` runs synchronously inside `GameObject.Instantiate`, and `Instantiate` clones the source's
active state.** `SynchronizationService.SpawnObject` instantiated from an active prefab and set the
transform three lines later, so every component's `Awake` fired while the clone still sat at the
**prefab's authored position**. Anything caching a transform in `Awake` cached prefab coordinates —
identically for every clone, since they all come from one prefab.

The host is unaffected: its objects are built in place by the game's own tile generator and never
move after `Awake`.

### How it presented, and why that was misleading

As "the charge shrine model is invisible to non-host players, except from a narrow arc". It was not
invisible — every client shrine's rune stone was drawn at one shared point in the world, so you saw
a pile of them only when looking that way, and nothing when looking down at the shrine.

### The measurement that settled it

The game's own `Transform.set_position` writes, captured with a temporary patch:

```
host    := (-60.00, -15.77,  21.85)   ← three shrines, three correct values
host    := (-92.27, -19.63, -25.46)
host    := (-140.00, -15.77,  5.80)
client  := (281.15, 16.60, -67.16)    ← three shrines, one constant
client  := (281.15, 16.60, -67.16)
client  := (281.15, 16.60, -67.16)
```

Stacks on all six carried **no managed mod frames** — the game is the writer on both peers; only the
input was wrong. And the constant held across shrines, across maps and across separate runs, which
ruled out it being any object's position.

Confirmed exactly, not merely plausibly: the shrine prefab's authored position is
`(281.15, 12.36, -67.16)`, the rune stone's local offset is `(0, 4.23, 0)`, and
`12.36 + 4.23 = 16.59`. That is the constant to the decimal.

### The fix, and its blast radius

The clone is now created inactive, positioned, then activated. Three constraints the fix has to
respect, all of them load-bearing:

- **Activate before the `GetComponentInChildren` calls.** Without `includeInactive` those do not see
  components on an inactive hierarchy, so the shady-guy and microwave rarity assignments would
  silently stop working.
- **Restore the prefab's active state in a `finally`.** It is shared mutable state around a call that
  can throw — P0-6's lesson. A leaked `false` deactivates a game prefab for the rest of the run.
- **The desert-grave chain is exempt.** `HandleSpawn` deliberately leaves those inactive for the
  grave sequence to reveal. The name test is now a shared helper so the two sites cannot drift.

**This changes activation timing for every object a client spawns.** That is deliberate — the defect
is in the spawn path, not in `ChargeShrine` — but it is the broadest change on the branch. The
shrine was simply the one prefab whose `Awake`-cached transform had a visible consequence. Anything
else that caches in `Awake` was wrong the same way and is now right; anything that depended on the
old timing would regress. Nothing has been observed to.

---

## Built but NOT verified

All build clean. None has been shown to fix an observed symptom.

| Commit | What |
|---|---|
| `5435487` | Pool double-free: `ProjectileDone` released the rocket to the pool, then `DestroyImmediate` destroyed its parent |
| `8fa1c2c` | Deferred despawn — a remote projectile finishes its visible flight instead of vanishing 100 ms short |
| `3e21869` | `try/finally` on the three globals `OnReceivedEnemyDamaged` opens around `enemy.Damage` |
| `1a19c78` | Per-stage guard so `OnBossDefeated` cannot run twice on the peer that killed the boss |
| `d8ab41b` | The set/call/unset sweep — 12 same-method sites restored in a `finally` |
| `00c8dc8` | The third `LookRotation` site, in `NetPlayer.Initialize` |
| `0354024` | The exception diagnostic, after `c09a487` failed to load — see below |

`8fa1c2c` **cannot** be validated on two instances of one PC (`Bind exception … 10048`, fallback to
port 27016, `rtt 0 ms`). It is about the 100 ms interpolation delay against real event timing and
needs two machines. The one internet run since did not isolate it.

`1a19c78` also remains unproven in the useful sense: the run after it logged exactly one
`Boss defeated, activating portal`, which is equally consistent with the guard working and with only
one call ever happening.

### The last one is a live blocker until you launch

`c09a487` **stopped the plugin loading entirely**:

```
Error loading [MegabonkTogether 5.1.0]: System.MissingMethodException:
Method not found: 'Void LogCallback..ctor(System.Object, IntPtr)'.
```

`0354024` fixes it, but **the game has not been launched with that build**. First thing next
session: confirm it loads. If it does not, `0354024` is a clean revert with nothing depending on it.

### Most of these are guards, not fixes for observed symptoms

`3e21869`, `1a19c78`, `d8ab41b` and `00c8dc8` were all found by reading code, not from a log line,
and every commit body says so. State that honestly if any is ever cited as having fixed something.

**`3e21869` is the more serious of the two.** `Enemy.Damage_Prefix` blocks every client-side enemy
damage unless `Plugin.Instance.CAN_DAMAGE_ENEMIES` is set, and `OnReceivedEnemyDamaged` set it,
called `enemy.Damage`, and cleared it — with no `try/finally`. One throw inside that call latches it
`true` and the client resolves enemy damage locally for the rest of the run: a permanent, silent
divergence from a single exception. The same statement leaked a netplayer position request
([P1-11](01-critical-fixes.md#p1-11)) and a tracker player id ([P1-5](01-critical-fixes.md#p1-5)).

This is [P1-10](01-critical-fixes.md#p1-10)'s defect exactly — 28 `CAN_SEND_MESSAGES` latches became
`Plugin.SuppressOutbound()` — and [P0-6](01-critical-fixes.md#p0-6)'s, where one unguarded string
interpolation latched two statics through 581 consecutive enemy spawns. **Both were fixed, and this
site was missed by both sweeps**, which is P1-6's standing lesson restated: guard the method, then
grep for the pattern anyway.

**That sweep has since been run** (`d8ab41b`), and it found 12 more of the same shape — every
remaining `Plugin.CAN_*` gate plus four position-request sites. All are now `try/finally`. The full
classification is in [P1-10](01-critical-fixes.md#p1-10)'s banner; the short version is that
**same-method sites are now exhausted**, and the remaining exposure is 39 Harmony prefix/postfix
pairs where the open and close are in different methods. Those need `[HarmonyFinalizer]` and the
balanced-stack discipline in [`00-fork-comparison.md`](00-fork-comparison.md) §4.1, which is
P1-11's scheduled work — and `91d4673` proves that class is live, not theoretical.

**`1a19c78`** guards a double-invoke that was already known as a symptom. `OnBossDefeated` opens with
`cam.arrowDict.Clear()` carrying the comment *"Prevent sometimes double add for portal arrow"* —
someone hit it, treated the duplicated minimap arrow as the bug, and cleared the dictionary while
everything after that line kept running twice, including `A_BossDefeated.Invoke`, a game event. A
client that kills the stage boss itself reaches the handler from its own `OnEnemyDied` and again from
the host's `EnemyDied` broadcast. Both call sites are deliberate, so the guard is in the handler.

### The rocket sequence is now four defects deep

Client simulation → pool double-free → despawn timing → **a residue that is still present**, each
only visible once the previous was fixed. `11-session-handover.md`'s rule has been reached and acted
on: see [live lead 2](#2-remote-projectiles--four-defects-deep-stop-patching). Do not patch it a
fifth time.

---

## A fifth implementation exists, and it has already shipped our target architecture

A closed-source Megabonk multiplayer mod runs **BepInEx 6 IL2CPP + Steamworks.NET P2P** — Steam
lobbies for discovery, no rendezvous server, no NAT-punch path, no relay of its own. That is
[`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md)'s target, shipped.

Recorded as **Mod S** in [`00-fork-comparison.md`](00-fork-comparison.md) §5 and **deliberately not
named anywhere in this repository**. It is binary-only with no licence file and no licence field in
its manifest, so all rights reserved. Only protocol and API-surface facts are recorded; nothing is
quoted or ported. Decompiled output lives in `megabonk-re/`, which `.gitignore` excludes wholly.

What it changed in our docs:

- **Poll groups** for the host receive path, **relay-status polling** (not just calling
  `InitRelayNetworkAccess`), and **authentication gating** before connecting — all folded into the
  migration plan's gotchas, none of which it previously covered.
- **Its reliability split independently matches ours**: 55 of 65 messages reliable, and the 9
  unreliable ones are exactly the superseded-continuous-state set. That is
  [`02-delivery-method-reference.md`](02-delivery-method-reference.md)'s rule reached from a
  different lineage, and a direct rebuttal of the Sea-Bass blanket downgrade.
- **One disagreement, left open rather than resolved.** They use `NoNagle` on one-shot events and
  batch explicitly; `01-api-mapping.md` recommends letting Nagle coalesce. Our own `[bw]` counters
  can settle it before Phase 1 commits either way.

> ⚠️ **The retry half of the next paragraph has since been overturned.** It was accurate for the
> build audited that session and is false at their 0.4.3, which retries *and* stamps readiness
> with `(sessionId, roundId)`. Corrected in
> [`00-fork-comparison.md`](00-fork-comparison.md) §5 — read that, not this. Left in place
> because it is why the lobby-ready defects below were deprioritised. The structural half stands.

**Two findings worth keeping because they contradicted expectations.** Their eight-message join
handshake contains **no retry, timeout or resend logic at all** — so it is *not* evidence that
adding retries fixes the lobby-ready defects below. What it suggests instead is structural:
readiness there is a protocol phase with its own messages, while ours is a mutable `IsReady` flag on
the replicated `Player` record, which is exactly why `ResetForNextLevel` and `OnLobbyUpdate` can
clobber it. And their dedicated `BossPortalUnlockedMessage`, guarded by an id set on both sides,
independently corroborated the `OnBossDefeated` double-invoke fixed in `1a19c78`.

**Caveat on all of it:** a changelog says what a bug was called, not what it was. Every item above is
either read from assembly metadata or checked against our own code — nothing is taken on the
strength of their release notes.

## The two live leads

### 1. The client exception storm — instrumented, awaiting one run

**3,032 `NullReferenceException` on the client, 0 on the host**, in one two-player internet session.
Every one the bare Unity form with no stack:

```
[Error  :     Unity] NullReferenceException: Object reference not set to an instance of an object.
```

Present in every client log for several sessions. Briefly and **wrongly** attributed to a
third-party mod — it was still there after that mod was removed. Never diagnosed, because with no
stack there has been nothing to act on.

`c09a487` fixes that: it hooks `Application.logMessageReceived`, calls `SetStackTraceLogType(…,
ScriptOnly)` (a shipped player often defaults these to `None`, which is the likeliest reason the log
carries only a message), and if Unity still returns nothing it captures a managed `StackTrace`
inside the callback — which fires synchronously from the log call, so a managed thrower is usually
still on the stack. It logs the first **six distinct** signatures in full and counts the rest.

**How to read it.** `[unity-exc] distinct #N:` lines:

| Shows | Reading |
|---|---|
| `MegabonkTogether.*` frames | Ours. The frame names file and line. |
| Only game types | Game code reachable from a managed call; the caller is the lead. |
| `<no managed frames — thrower is native game code>` | **Not a failure — that is the answer.** It rules our code out. |

Hooked at `Plugin.Load`, so singleplayer is covered too. "Zero in singleplayer, storm as a client"
would localise this in one sitting.

**The standing hypothesis, deliberately kept out of the code:** clients suppress the game's own
world generation (`Skipping tile object / rail / chest / Quest spawning`) and rebuild from messages,
so game code can dereference objects that were never created. The stack decides it; do not assume it.

### 2. Remote projectiles — four defects deep, stop patching

Rockets have now needed four fixes: client simulation (`91d4673`), a pool double-free (`5435487`),
despawn timing (`8fa1c2c`), and a residue that is **still present** — some rockets explode mid-flight
after all three.

`11-session-handover.md`'s rule applies and is why this is not being patched a fifth time: **when
three fixes in a row are needed in one area, suspect the architecture rather than the fix.**

The design is the problem. A remote projectile is a game object the client *teleports* via
`ProjectileInterpolator` while the game still owns its lifetime, its collision and its expiry — two
authorities on one transform. Every fix so far has been a symptom of that split. The 870 residual
`Look rotation viewing vector is zero` on the client (0 on the host) is very likely the same cause:
game code reading a direction from a transform that is teleported rather than simulated, so it reads
zero. Every `LookRotation` site in the mod is accounted for — three guarded, one that runs twice a
run — so that residual is not another missing guard.

Candidate approaches, none chosen: make the client's remote projectiles inert visuals with no game
components; or give the interpolator authority over the lifetime as well as the transform; or send
projectile *events* rather than streaming transforms. This wants a design pass, not a patch.

## Still open

- **Four lobby-ready barrier defects (A–D)**, found while chasing the stage-transition hang and all
  confirmed by code inspection. `ClientInGameReady` is sent exactly once with no retry;
  `ResetForNextLevel` clears `IsReady` for remote players too; `OnLobbyUpdate` overwrites the whole
  `Player` record including `IsReady`; and `NetworkHandler.Update` stops polling while
  `IsLoadingNextLevel`. Any single loss is still a permanent hang. Dropped in priority once
  BonkTuner was identified as the actual trigger, but **not fixed**. **Priority argument has
  since weakened:** the "retries are not the answer" finding that supported deprioritising this
  was overturned at Mod S 0.4.3 — they ship retry plus `(sessionId, roundId)` stamping, which is
  defect A's fix and our SE-5 in one. See [`00-fork-comparison.md`](00-fork-comparison.md) §5.
- **A latent bug in `SendToHost`:** `gamePeers[0]` is a `ConcurrentDictionary<int, NetPeer>` **key**
  lookup, not "the first peer". It throws `KeyNotFoundException` the moment the host peer's
  LiteNetLib id is not 0.
- ~~**39 Harmony prefix/postfix state pairs**~~ — **DONE** in `d350acc` and verified. The
  set/call/unset problem is now closed in both forms: same-method sites use `try/finally`
  (`d8ab41b`, `3e21869`), cross-method pairs use `[HarmonyFinalizer]` + `__state`. No pop remains
  outside a finalizer or a `finally`, and no push without `__state`, checked mechanically.
- **SE-5 round identity** — Run C says residual, not urgent. Wire change; new union tags only.
- **`Animator.set_speed` NRE in `RestoreDeath`** — unchanged, still undiagnosed.
- ~~**A 6-player bandwidth capture** — still the missing Phase 0 data point.~~ **Struck: not
  achievable.** Only two players are available to this project, so this was an open blocker
  nobody could ever close. Phase 0's exit criteria now measure 2 players in-game and *derive*
  4 and 6 from serialized payload sizes, with 6-player *behaviour* recorded as accepted risk.
  See [`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md) Phase 0. New
  2-player numbers below.

---

## Bandwidth — 2-player data points

Host egress, LAN and internet, consistent with the 3-player baseline in `11`:

```
15–34 KB/s total; LobbyUpdates 65–90% of it at 60–100/s
ProjectilesUpdate second, 0.1–5.7 KB/s, spiking to 294 B/send in a swarm
everything else < 0.1 KB/s
```

`LobbyUpdates` remains where any bandwidth work belongs. `SendProjectilesUpdate` was checked against
the MTU rule and is correct — it promotes to `ReliableOrdered` above `MAX_PACKET_SIZE_BYTES = 1000`.

---

## Lessons, each learned by being wrong

These cost real time this session and are the reason this file is long.

1. **Ghidra's applied struct field names sit a slot out, and it bit twice.** `CLAUDE.md` documents
   this and it still produced two confident wrong conclusions: `Start`/`Complete` were read as
   disabling `meshRenderer` when they touch `zoneRenderer` (0x68 vs 0x70), and `Update`'s rune-stone
   scaling was read off `zonePropertyBlock`. **Decode every field access against `dump.cs` offsets
   before drawing a conclusion from decompiled output** — the giveaway both times was a method being
   called on a type the label could not be (`Renderer.GetPropertyBlock` on a `Transform`).

2. **`MissingMethodException` is raised at JIT time, not at the call — and this cost the session
   twice.** First `GetComponentsInChildren<T>(bool)` in the shrine dump; then
   `new Application.LogCallback(...)` in the exception diagnostic, which **stopped the plugin
   loading at all**. Both times the `try/catch` was *inside* the failing method, and both times it
   never ran, because the body was never compiled and the throw surfaced in the caller's frame.

   Three rules follow, in order of how much they cost:

   - **A catch cannot protect a body that will not compile — only a catch one level up can.**
   - **We compile against `stripped-libs` and run against Il2CppInterop's generated assemblies.**
     They are not the same types. A Unity *event subscription* is the sharpest case: `LogCallback`
     is an ordinary .NET delegate at compile time and is not at runtime. Prefer a plain static call;
     if you must use an interop type, verify it against the *runtime* assembly, not the stub.
   - **Instrumentation must never be able to stop the plugin loading.** The call site in
     `Plugin.Load` is now wrapped in `try/catch` for exactly this. Keep that guard even after the
     diagnostic it protects is deleted.

3. **Cosmetic work goes last, in its own `try`.** The renderer dump was placed *above*
   `SendToAllClients`, so every throw skipped the host's shrine broadcast — the diagnostic disabled
   the fix it was observing. This lesson was already in `06-session-handoff.md` from P1-6, P1-7 and
   P0-6. This was the fourth time.

4. **Drip-feeding diagnostics costs playtests.** Five runs each answered one question and raised the
   next. When a playtest needs a second person, add every field that could bear on the question in
   one build — chain dumps, component state, a change detector — not the single next field.

4a. **The cheapest verification for a large unverifiable change is an existing silent-when-healthy
   counter.** `d350acc` changed 36 hot-path patches to a mechanism nobody had confirmed works under
   IL2CPP. It was validated in one run at zero cost, because P1-11's stale-request counter and the
   kill-attribution counters both scream when the mechanism fails and say nothing when it works.
   Look for that lever before writing a new diagnostic.

4b. **Do not bulk-regex source.** The first attempt at the finalizer conversion mutated the file
   while iterating over match offsets computed against the *old* string, corrupted `Rocket.cs`
   outright and touched 23 files. Reverted with `git checkout` and redone applying edits in reverse
   offset order. If you automate an edit across many files, apply back-to-front and read the diff
   before committing — the compiler catches syntax, not intent.

5. **Name a mechanism only when the data supports it.** Three were asserted here and all three were
   wrong: the Ghidra misread, a shared `runeStone` reference surviving `Instantiate`, and
   animation-driven bones. The measurement that ended it was a stack capture on the actual writer.
   `11`'s rule generalises: **when the fix candidates keep failing, stop proposing and instrument.**

6. **Rule out the environment before debugging the code.** Three sessions of stage-transition work
   ended at a third-party mod installed on one peer only. **Check that both peers run the same mod
   set** before treating a two-peer divergence as a netplay bug.

---

## Standing rules, unchanged

- Nothing is verified until it has been run in-game; "builds clean" is stated as exactly that.
- One logical fix per commit; the body says what was deliberately not done and what is unverified.
- Deploy to every install and check the SHA256 matches.
- Remove other mods before a measured run.
- Netcode bugs are mostly invisible at 0% loss — and two instances on one PC is `rtt 0 ms`, which is
  not a LAN test either.
- `<Version>` is still 5.1.0 and untouched. Nothing has been released.

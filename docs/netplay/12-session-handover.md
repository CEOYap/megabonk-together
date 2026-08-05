# Handover — the second PC session

Supersedes [`11-session-handover.md`](11-session-handover.md). Read this first, then
[`06-session-handoff.md`](06-session-handoff.md) for the standing queue.

Branch: `claude/build-fixes-first-pc-session`, continuing from where `11` left off.

**Nine commits. Four fixes are verified in-game, three are built but unplayed, and one long-running
cosmetic bug was traced to a defect in the client spawn path that affected far more than the thing
it was reported against.**

---

## Verified in-game

| Thing | Commit | Evidence |
|---|---|---|
| Stage-transition hang was **not ours** | — | A third-party mod (BonkTuner) on the client only. Removing it fixed transitions outright: `Lobby not ready yet` went 357/364 → 1/3 |
| Shrine charge-start replay | `7c8cdb2` | `No one is charging this shrine; ignoring stop` 4 → 0; second charger now recorded (`still charging` × 11) |
| **Client spawn ordering** | `1e62b31` | The headline fix — see below. Client `[shrine-move]` 3 → 0, rune stone `localPos` now matches host exactly |
| Zero-vector `LookRotation` guards | `16de1e4`, `45a1e3a` | `Look rotation viewing vector is zero` on the client: 208,936 → 6,353 → **0**. Client log 216,507 → ~9,000 lines |

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

Three projectile commits. All build clean; none has been run on two machines.

| Commit | What |
|---|---|
| `91d4673` | Clients no longer simulate or detonate host-owned rockets |
| `5435487` | Pool double-free: `ProjectileDone` released the rocket to the pool, then `DestroyImmediate` destroyed its parent |
| `8fa1c2c` | Deferred despawn — a remote projectile finishes its visible flight instead of vanishing 100 ms short |

`8fa1c2c` in particular **cannot** be validated by the setup used for the last two runs: those were
two instances on one PC (`Bind exception … 10048`, fallback to port 27016, `rtt 0 ms`). The fix is
about the 100 ms interpolation delay against real event timing. It needs a genuine two-machine run.

### The rocket sequence is three defects deep

Client simulation → pool double-free → despawn timing, each only visible once the previous was
fixed. `11-session-handover.md` records the rule: **when three fixes in a row are needed in one
area, suspect the architecture rather than the fix.** If a fourth rocket defect appears, the remote
projectile representation wants rethinking, not another patch.

---

## Still open

- **Four lobby-ready barrier defects (A–D)**, found while chasing the stage-transition hang and all
  confirmed by code inspection. `ClientInGameReady` is sent exactly once with no retry;
  `ResetForNextLevel` clears `IsReady` for remote players too; `OnLobbyUpdate` overwrites the whole
  `Player` record including `IsReady`; and `NetworkHandler.Update` stops polling while
  `IsLoadingNextLevel`. Any single loss is still a permanent hang. Dropped in priority once
  BonkTuner was identified as the actual trigger, but **not fixed**.
- **A latent bug in `SendToHost`:** `gamePeers[0]` is a `ConcurrentDictionary<int, NetPeer>` **key**
  lookup, not "the first peer". It throws `KeyNotFoundException` the moment the host peer's
  LiteNetLib id is not 0.
- **SE-5 round identity** — Run C says residual, not urgent. Wire change; new union tags only.
- **`Animator.set_speed` NRE in `RestoreDeath`** — unchanged, still undiagnosed.
- **A 6-player bandwidth capture** — still the missing Phase 0 data point. New 2-player numbers
  below.

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

2. **`MissingMethodException` is raised at JIT time, not at the call.** `GetComponentsInChildren<T>(bool)`
   does not resolve under Il2CppInterop; the `try/catch` *inside* the method did not contain it,
   because the body never executed and the throw surfaced in the caller's frame. **A catch cannot
   protect a body that will not compile — only a catch one level up can.** Isolate each risky member
   access in its own small method.

3. **Cosmetic work goes last, in its own `try`.** The renderer dump was placed *above*
   `SendToAllClients`, so every throw skipped the host's shrine broadcast — the diagnostic disabled
   the fix it was observing. This lesson was already in `06-session-handoff.md` from P1-6, P1-7 and
   P0-6. This was the fourth time.

4. **Drip-feeding diagnostics costs playtests.** Five runs each answered one question and raised the
   next. When a playtest needs a second person, add every field that could bear on the question in
   one build — chain dumps, component state, a change detector — not the single next field.

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

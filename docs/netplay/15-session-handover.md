# Handover — three bug fixes, all unverified, and one sitting that confirms them

Supersedes [`14-session-handover.md`](14-session-handover.md). Read that one only for the Phase 5
history; everything still outstanding from it is repeated here.

Branch: `claude/ob11-spawn-position`, 14 commits off `main`, **not pushed**. PR #11 (Phase 5) merged
before this branch started.

**If you read one thing:** *The sitting*, below. Three fixes landed this session and **not one of
them has run in-game** — they are all confirmed only to the level of "compiles against the real
interop and the proxy signatures resolve".

---

## The sitting

One two-player session covers everything. Order matters less than the conditions, which are listed
per item because **three of these will look like a no-op if tested under the wrong conditions**.

### Config, both machines

| File | Key | Value |
|---|---|---|
| `BepInEx/config/MegabonkTogether.cfg` | `[Network] UseSteamTransport` | `true` |
| same | `[Diagnostics] LogBandwidth` | `true` |
| same | `[Diagnostics] LogAllocationRate` | `true` |
| same | `[Diagnostics] LogEncounterPause` | `true` |
| `BepInEx/config/BepInEx.cfg` | `[Logging.Disk] WriteUnityLog` | `true` |

Confirm the `[log-capture]` **info** line at startup on both. A warning there means that copy is not
recording Unity lines and its silence proves nothing.

### 1. Loss-test run 2 — the only unmet Phase 4 exit criterion

`clumsy` on **one** machine: Drop, 3%, filter `udp`. **Start it in the lobby**, not mid-run — that
is precisely what run 1 got wrong.

**Check the rig before trusting the run.** Find a `[bw]` peer line and confirm `quality` is
**below 100%**. At 100% clumsy is not filtering this traffic and the run tested nothing; a void run
and a passing run produce identical logs otherwise.

**Cross at least one level transition under loss.** The readiness barrier is the path this project's
history records as having broken four times, and it has still never been exercised lossy. Everything
run 1 proved was the in-run case.

Procedure: [`../steamworks/07-lossy-link-playtest.md`](../steamworks/07-lossy-link-playtest.md).

### 2. Phase 5 steps 3–5 — never run with a second machine

Join Code, Stop on the connecting window, the Options sub-view round trip, the invite path. Step 5
removed the fallback, so there is nothing to fall back to.

### 3–5. The three fixes from this session

| OB | Confirms the fix | **Condition — get this wrong and it looks like a no-op** |
|---|---|---|
| **OB-11** minibosses spawn near the host | Minibosses start arriving near the **client**; no `OB-11: could not re-centre` in the host log | **Watch ~8 spawns.** Each has a 1-in-N chance of being re-centred, so at two players about half should land near the client. One miniboss proves nothing either way. |
| **OB-9** boss max HP diverges | Boss health bars **agree** between peers | **Use a late-run boss.** The scaling only applies when `GameManager.bossCurses > 0`, so an early miniboss is unaffected whether the fix works or not. |
| **OB-2** ghost summons for non-owners | Only the item's **owner** sees ghosts | **One player holds the Ghost item and the other does not.** Then interact with pots/shrines on both sides in turn. |

### 6. OB-12 — carried over, still unverified

The Shrine Counter panel should now **agree** between peers after each has used interactables the
other did not. The old giveaway was large arbitrary gaps.

### 7. OB-13 — a diagnostic run, not a confirmation

There is no fix for this one. `[Diagnostics] LogEncounterPause` was added this session precisely
because the decisive evidence did not exist; without it the run would only re-observe the symptom.

Open an encounter window with shared experience on, and read the `[pause]` lines:

- **A line with the window open and the game not paused** — the log flags this itself with
  `<-- WINDOW OPEN BUT NOT PAUSED (OB-13)`. That confirms the leading hypothesis: something clears
  the pause underneath a window that is still displayed, and the fix is to stop the
  shared-experience pause sharing one global flag with five other screens.
- **The pause holds for the whole window** — then the damage comes from something that never
  consulted the flag, and the search moves to the mod's own injected components. None of them check
  `MyTime.paused`; the plugin reads it in exactly one file.
- **`[pause] OB-13 diagnostic disabled for this session`** — `Physics.simulationMode` did not
  resolve against this runtime. That is what the guard is for; report it and the diagnostic needs a
  different reader.

### 8. One grep on the client, after

```bash
grep -c '\[Error  :     Unity\] NullReferenceException' LogOutput.log
```

Closes the last open hypothesis from the button fix: the host went 1716-and-7 → 0, but the client's
1716 were never *traced*, only shown to be consistent with the same bug.

### Capturing the logs

Immediately when the run ends and **before either machine relaunches**, take **both** logs from
**both** machines — `BepInEx/LogOutput.log` and `%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log`.

Only the second carries stack traces. Run 1 could not attribute the client's 1716 exceptions for
exactly this reason: only the BepInEx log came back from that machine.

Name the pair with one run identifier, mark which end is host and which had clumsy, and confirm the
session id matches across both files. **A pair that cannot be proven to be one session is worth
nothing.**

---

## What landed this session

Three fixes, three corrected causes, one new bug, one diagnostic, one refactor.

### The pattern worth noticing

**Every entry examined this session had a recorded cause that was wrong**, and in each case the
correction came from reading the game rather than the entry. That is now five for five across two
sessions — OB-12 and the button bug last time, OB-11, OB-2 and OB-9 this time. Treat
[`08-observed-bugs.md`](08-observed-bugs.md) entries as hypotheses, including the ones marked
CONFIRMED.

| OB | The entry said | Actually |
|---|---|---|
| **OB-11** | the spawner anchors on `GameManager.Instance.player` | `MyPlayer.Instance`, the local-player static at offset `0x8`. `GameManager` appears in that body only to fetch a layer mask. |
| **OB-2** | decide it by whether `SpawnGhost` routes through `EnemyManager.SpawnEnemy` | Neither branch. Ghosts are `EffectManager` projectiles, so host authority never applied. Every `ItemGhost` subscribes to one **static** event, and remote players hold real instances locally. |
| **OB-9** | max HP is *derived locally* on each peer | It is assigned outright from the spawn message. The defect is the *sampling moment* — every scaler runs after `SpawnEnemy` returns, and the postfix fires in between. |
| **OB-1** | prefer deriving the count locally *if the formula is pure* | It is not pure. `GetAttackQuantity` adds the **reading peer's** `Projectiles` stat, so deriving would still disagree — and would look correct until somebody took the upgrade. **Withheld by request; needs replication.** |

### The fixes

- **OB-11** — `SpawnBoss_Prefix` takes `ref Vector3 pos` and re-rolls the ring around a randomly
  chosen living player via the game's own `GetEnemySpawnPositionAroundPoint`. No wire change; the
  correction happens before the enemy exists, avoiding the move-a-live-object hazard.
- **OB-2** — `OnInteracted_Prefix` returns false for a remote-owned item. **The guard already
  existed and had no callers**: `IsRemoteItem` was written, implemented and never used, while its
  sibling `IsANetPlayerAbility` is used in exactly that shape. Also closes the missing
  session/ownership guard in the same patch — which turned out to be the same gap seen from two ends.
- **OB-9** — new append-only union tag **76, `EnemyMaxHpScaled`**, sent from a postfix on
  `Enemy.SetSwarmMultiplierHp`. An xref gives that method five callers sharing no other hook, three
  of them the interactables in the report. The client buffers a correction that outruns its spawn,
  because both messages are `ReliableUnordered` — delivery guaranteed, order explicitly not.

### Also

- **OB-13** filed and refined: enemies take projectile damage while an encounter window is paused,
  **shared experience only**. That condition is what makes it a defect rather than a design question
  — with both peers paused there is no legitimate source of damage. All three game projectile update
  paths were decompiled and *do* gate on `MyTime.paused`, so the game's own simulation is not the
  culprit.
- **`Configuration/Tunables.cs`** — every number worth deliberately changing, in one file, each
  annotated with what it trades against. Behaviour-neutral: name substitution only, values
  unchanged. Deliberately kept as `const` rather than moved into `ModConfig`; the reasoning is in
  the file.
- **[`04-performance-and-gc.md`](04-performance-and-gc.md) corrected** where it had gone stale —
  quantization, byte counters and the tick budget were all written as outstanding and are done. Its
  measurement checklist also opened with two steps that **cannot be performed on this game**, the
  Unity Profiler attaching only to a development player.

---

## After the sitting

**`ENEMY_POSITION_THRESHOLD` is the performance change to make first.** Currently `0.1f` in
`Tunables`. Zero correctness risk — it is a sender-side filter, clients interpolate, and the refresh
period bounds staleness regardless — and crucially it is **measurable at two players**, unlike
per-peer culling whose benefit only appears at player counts that cannot be tested here.

Judge `LogAllocationRate` output against **~9,600 `EnemyModel`/s**, not the older 24,000 figure: the
budgeted stream caps the tick at 240 enemies regardless of how many are alive. Read the
**contrast** between swarm and no-swarm, not the absolute. Do not pool before measuring.

## Still open, no fix

OB-1 (withheld by request), OB-3, OB-5, OB-6, OB-10. OB-7 and OB-8 are held pending an in-game
check — no `fix(...)` commit has ever touched pylons or the static boss, so if they now work,
something else fixed them incidentally.

## The rename to OmniBonk

Unchanged from [`14-session-handover.md`](14-session-handover.md): deferred until everything is done
and bug-free, supersedes the release trio, and `Protocol.Version` can reset to 1. Not a
find-and-replace — assembly name, `BepInPlugin` GUID, config file name (renaming it silently resets
everyone's settings), AssetBundle resource name, Thunderstore identity. Its own branch and a
checklist.

## Standing constraints, unchanged

- Two players is the testing maximum.
- Nothing is verified until run in-game. There is no test suite.
- One logical change per commit, with what is unverified stated in the body.
- MemoryPack union tags are append-only. Tag 76 is now taken; the next free is 77.
- Do not name another mod or its author in commits, titles or docs.

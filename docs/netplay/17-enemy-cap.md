# Enemy cap — current state, costs of raising it, and a plan

Written 2026-10-19 as groundwork for a future feature: a configurable (or player-count-scaled)
enemy cap. Nothing here is built. Status legend as in [`../README.md`](../README.md).

---

## What exists today

| Where | Value | Notes |
|---|---|---|
| `GameBalanceService.GetMaxEnemiesSpawnable()` | **500** (2–3 players), **600** (4–6 players), **400** during the Final Swarm | The mod's own cap. CONFIRMED in code. |
| Enforced in `EnemyManagerPatches.SpawnEnemy_Prefix` | `numEnemies >= cap` → spawn refused | Only on the summoner overload `SpawnEnemy(EnemyData, int, bool, EEnemyFlag, bool)`, and only when `forceSpawn` is false. Bosses, forced and scripted spawns ignore it. CONFIRMED in code. |
| `EnemyManagerPatches.GetNumMaxEnemies` postfix | always **1000** in netplay | Tells the game its limit is 1000 so enemies stay aggressive near the cap. The code's own comment asks whether it works. UNVERIFIED. |
| Vanilla game, per [megabonkinfo.org](https://megabonkinfo.org/player-stats/difficulty) | **550** on normal stages; Final Swarm **400** for the first 2 min, then **300** | Third-party. UNVERIFIED against our dump — decompile `EnemyManager.GetNumMaxEnemies` before relying on it. |

**Already a balance question:** at 2–3 players the mod caps *below* vanilla (500 vs 550). The same
article says that outside swarms, higher Difficulty spawns more enemies only as fast as they are killed
(Stage 1 at 200 % Difficulty: about 275 spawns in 90 s if every enemy dies, about 89 if none do). A
strong team therefore sits at the cap, and the cap, not Difficulty, decides how busy the run is.

The mod's Final Swarm cap (400 for the whole swarm) also differs from the article's vanilla value,
which drops to 300 after 2 minutes. UNVERIFIED.

---

## What raising the cap costs

### Network — the main stream does not grow

The host's enemy delta is **byte-budgeted**, not count-based (`Configuration/Tunables.cs`):

- `MAX_ENEMY_TICK_BYTES = 3600`, `ENEMY_MODEL_WIRE_BYTES = 19` → `MAX_ENEMIES_PER_TICK = 189`
- `ENEMY_UPDATE_TICK_RATE = 40` Hz → about **144 KB/s per client**, whatever the enemy count
- The stalest enemies go first (`EnemyManagerService.GetAllEnemiesDeltaAndUpdate`), so above 189 enemies
  they take turns:

| Enemies alive | Each enemy refreshed about every |
|---|---|
| 189 | 25 ms (every tick) |
| 600 | 80 ms |
| 1000 | 130 ms |
| 1500 | 200 ms |

**Effect:** smoothness on the client degrades, not bandwidth. Expect visible stutter or rubber-banding
of client-side enemies somewhere past ~1000, depending on how much they move.
`EnemyInterpolatorManager` smooths between samples, but it cannot hide 150–200 ms gaps on fast
enemies. UNVERIFIED where the line is; measure it.

### Network — what does grow

Event messages scale with spawns, kills and hits, not with the cap directly, but a higher cap means
more of all three:

| Message | Delivery | Scales with |
|---|---|---|
| `SpawnedEnemy` | ReliableUnordered | spawns (one per enemy) |
| `EnemyDied` | ReliableOrdered | kills |
| `EnemyDamaged` | Unreliable from the host | **enemies being hit at once**, one summed message per (enemy, attacker, source) per 40 Hz tick, forwarded to every client |

`EnemyDamaged` is the one to watch. A 2026-10-18 log measured about 180 hits/s at ~10 KB/s on one link,
and on 2026-10-19 the host sent ~33,000/s at god-mode damage. Since then the host sums hits per
enemy per tick (OB-34, UNVERIFIED in play), which makes the rate track how many enemies are being
hit rather than how many hits land. It still multiplies by player count. The `[bw]` aggregation
line gives the hits-per-message ratio. Host upload at six
players is `5 × (144 KB/s + event traffic)`.

### CPU — host first

The host carries most of the cost:

- the game's own AI and physics for **every** enemy;
- the mod's per-enemy hooks every frame (`Enemy.MyUpdate` / `MyFixedUpdate` prefixes, target switching);
- simulating every teammate's weapons against those enemies (the host applies all damage).

The client runs `EnemyInterpolatorManager` for every enemy every frame, and since OB-30 it draws far
enemies too (Unity's frustum culling still skips off-screen ones). The `MyUpdate` / `MyFixedUpdate`
trampolines run per enemy per frame on both peers, even when the distance throttler then skips the body.

### GC

`EnemyModel` allocations are bounded by the same per-tick budget (≤ 189 × 40 Hz), so allocation rate
should not grow much with the cap. Event messages allocate per spawn, kill and hit. Measure with
`LogAllocationRate`.

---

## How to measure before choosing numbers

Turn on, on **both** machines (`[Diagnostics]` in `MegabonkTogether.cfg`):

- `LogFrameTiming` — the `[perf]` line: FPS, 1 % / 0.1 % lows, frames over 33 ms and over 100 ms,
  enemy count
- `LogBandwidth` — the `[bw]` lines, per message type and per peer
- `LogAllocationRate` — the `[alloc]` line

Play the same stage and the same moment (a swarm) at each cap: 600, then 800, then 1000. Compare:

1. Host `[perf]` 1 % low and `>100 ms` count against the enemy count — the CPU ceiling.
2. Client `[perf]` the same way — rendering and interpolation cost.
3. `[bw]` total and `EnemyDamaged` per peer — the event-traffic growth.
4. By eye: client-side enemy smoothness near the cap.

Two-PC runs only for numbers you trust: the [test bot](../superpowers/specs/2026-10-19-test-bot-design.md)
shares one PC with the host, so its FPS readings only compare with other bot runs.

---

## Proposed feature

**Goal:** let the host choose the cap, with sensible defaults, without rebuilding.

1. **Config, host-only.** `[Balance] EnemyCap` (int, `0` = automatic). Only the host's value matters,
   because only the host spawns (`SpawnEnemy_Prefix` refuses spawns on clients). No wire change needed.
2. **Automatic defaults.** Start from vanilla: **550** at 2–3 players instead of 500, so netplay is
   never thinner than solo, and keep 600 at 4–6 until measurements say more is affordable.
3. **Final Swarm.** Decide whether to match vanilla's 400 → 300 after 2 minutes. Decompile the vanilla
   cap first.
4. **Guard rails.** Clamp with `AcceptableValueRange` (e.g. 100–1500) and log the effective cap once
   per stage (`[balance] enemy cap N (config|auto, players P)`), so a test log always says which cap
   it ran with.
5. **If the client stutters past ~1000:** raise `MAX_ENEMY_TICK_BYTES` in step with the cap, or
   prioritise enemies near each client rather than only by staleness. The first costs bandwidth; the
   second costs host CPU.

**Decompile first:** `EnemyManager.GetNumMaxEnemies` (the vanilla cap and what the 1000 postfix really
changes) and the summoners' spawn-count formulas (`StageSummoner.GetNumTargetEnemies`,
`SwarmSummoner.GetNumTargetEnemies`), which read Difficulty. The 2026-10-17 OB-26 work listed both.

**Out of scope here:** per-player Difficulty (OB-26, already averaged on the host) and enemy HP
scaling (`GameBalanceService`).

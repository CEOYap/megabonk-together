# The three enemy limits

Build 21750826. Written 2026-10-25 while building the No Enemy Cap lobby setting. Addresses are VAs
from `megabonk-re/build-21750826/dump.cs`. Constants were read from `GameAssembly.dll`'s `.rdata`.
Status legend as in [`../README.md`](../README.md): everything below is CONFIRMED by decompile
unless marked otherwise.

Three separate limits decide how many enemies can be alive at once. Each one stops spawning on its
own, so lifting the cap means lifting all three. A hands-off test found the third only after the
first two were lifted: spawning stopped at exactly 700.

| # | Limit | Owner | Value | Where it bites |
|---|---|---|---|---|
| 1 | `EnemyManager.GetNumMaxEnemies` | the game | 550; Final Swarm 400, then 300 | `SpawnEnemy`, the summoners, the Final Swarm target |
| 2 | `GameBalanceService.GetMaxEnemiesSpawnable` | the mod | 500 (2–3 players), 600 (4–6), 400 in the Final Swarm | `EnemyManagerPatches.SpawnEnemy_Prefix` |
| 3 | `EnemyManager.maxNumEnemiesPooled` | the game | 700 | the enemy pool's create function |

In multiplayer the mod reports limit 1 as **1000** (`EnemyManagerPatches.GetNumMaxEnemies`). So
before No Enemy Cap, the mod's own limit 2 was the one that actually stopped spawns, at 500 or 600.
That is below the game's 550 for two or three players.

---

## 1. The game's limit: `EnemyManager.GetNumMaxEnemies` (0x180419D60)

```
if (Final Swarm is active)          // a flag on the stage boss list
    return MyTime.time >= 120 ? 300 : 400;   // .rdata 0x18262EDF4 = 120.0
return 550;                          // 0x226
```

Its callers, found by xref:

| Caller | VA | What it does with the limit |
|---|---|---|
| `EnemyManager.SpawnEnemy` (summoner overload) | 0x18041A3F0 | returns null for a non-forced spawn once `numEnemies >= limit` |
| `EnemyManager.CanSpawnEnemy` | 0x180419800 | `numEnemies < limit` |
| `EnemyManager.HasMaxEnemies` | 0x180419DF0 | `limit <= numEnemies`; the summoners stop asking |
| `SwarmSummoner.GetNumTargetEnemies` | 0x180489420 | Final Swarm target: `min(time × 2.5, limit) × multiplier + 50` (`.rdata` 0x18262EE38 = 2.5, 0x18262ECD0 = 50) |

Outside the Final Swarm, `GetNumTargetEnemies` uses `(Difficulty + 1) × (stage × 65 + 240)` instead,
where Difficulty is stat 38. It does not read the limit there.

Ghidra labels `numEnemies` as `zoomValue` in these bodies; the field names are one slot out, as
elsewhere in this build.

**What the mod does.** `EnemyManagerPatches.GetNumMaxEnemies` (postfix) returns 1000 in netplay, so
enemies stay aggressive near the cap. With No Enemy Cap it returns `NO_ENEMY_CAP_LIMIT` (100 000).
That is far past any crowd a run reaches, and small enough that the Final Swarm's float math on it
stays exact.

**Consequence for the Final Swarm.** The target ramps with time and is clamped by this limit. With No
Enemy Cap the swarm keeps growing for as long as it lasts, by about 2.5 enemies a second times the
summoner's multiplier, instead of stopping at 400. UNVERIFIED in play: no hands-off run has reached a
Final Swarm with the setting on.

---

## 2. The mod's limit: `GameBalanceService.GetMaxEnemiesSpawnable`

Mod code, not the game's:

```csharp
if (GameManager.Instance.IsFinalSwarm()) return 400;
return players >= 4 ? 600 : 500;
```

It is enforced in `EnemyManagerPatches.SpawnEnemy_Prefix`, on the summoner overload
`SpawnEnemy(EnemyData, int, bool, EEnemyFlag, bool)`, on the host only and only when `forceSpawn` is
false. Bosses, forced spawns and scripted spawns ignore it, as they ignore limit 1. Clients never
spawn on their own: the same prefix returns false for them, and they build the host's enemies from
`SpawnedEnemy` messages.

**What No Enemy Cap does:** the prefix skips this check.

---

## 3. The enemy pool: `EnemyManager.maxNumEnemiesPooled`

`SpawnEnemy` (0x18041A3F0) takes every enemy from `PoolManager.Instance.enemyPool` (offset 0xC0)
and gives up when it gets null.

- `PoolManager.Start` (0x18040AF40) builds that pool as
  `new ObjectPool<GameObject>(create, OnTakeFromPool, OnReturnedToPool, OnDestroyPoolObject, true, 20, max)`.
  The `max` argument only limits how many released objects the pool keeps, not how many it creates.
- The limit is in the create function, `PoolManager.<Start>b__107_3` (0x18040E8D0). It is an inlined
  copy of `CreatePooledItem`:

  ```
  if (enemyPool.CountAll < EnemyManager.maxNumEnemiesPooled) return Instantiate(enemyPrefab);
  return null;
  ```

- `maxNumEnemiesPooled` is a public static on `EnemyManager` (static offset 0x10). It is 700 in play
  (measured hands-off: spawns stopped at exactly 700 alive), which is why `Get` comes back null there.
  Ghidra prints the pool fields one slot out here too: `enemySpawnFxPool` where it reads `enemyPool`.

The same capped-create pattern backs every pool the game builds with `CreateObjectPool` /
`CreateObjectPoolTimeout`: `CreatePooledItem` (0x180409800) and `CreatePooledItemTimeout`
(0x1804096E0) both return null at `maxSize`. The enemy pool is built separately in `Start`, which is
why patching `CreatePooledItem` did not lift it. OB-62 met the same null return on item effect pools.

**What No Enemy Cap does:** `EnemyPoolSize.Apply` (in `Patches/Enemies/EnemyManager.cs`) saves the
game's value and sets the static to 100 000 when the lobby settings are applied, on every peer.
Clients draw the host's enemies from their own pool, so they need it raised too.
`LobbySettingsService.Reset` (called from `SynchronizationService.Reset` at session end) restores the
game's value.

---

## The No Enemy Cap lobby setting

| Piece | Where |
|---|---|
| Config | `[Lobby] NoEnemyCap`, off by default (`ModConfig.NoEnemyCap`) |
| Lobby button | "No Enemy Cap: ON/OFF" in the Options view (`LobbyPanel.OnEnemyCapToggleClicked`) |
| Wire | `LobbySettings.NoEnemyCap`, protocol 10. Sent by the host just before `RunStarted`; every peer applies the host's value, never its own |
| Limit 1 | `EnemyManagerPatches.GetNumMaxEnemies` reports 100 000 instead of 1000 |
| Limit 2 | `EnemyManagerPatches.SpawnEnemy_Prefix` skips the mod's cap |
| Limit 3 | `EnemyPoolSize.Apply` / `Restore` raise and restore `maxNumEnemiesPooled` |

**Hands-off, 2026-10-25.** Run with `run-local-session.ps1 -TestSpawnEnemies 1200`, adding
`-NoEnemyCap` for the "on" runs. The Debug option `[Testing] TestSpawnEnemies` has the host request
that many skeletons, 30 s into the run, through the capped summoner overload.

| Setting | Spawned of 1200 | Peak alive | Stopped by |
|---|---|---|---|
| Off | 496 | 500 | limit 2 (two players) |
| On, limits 1 and 2 lifted only | 694 | 700 | limit 3 |
| On, all three lifted | 1200 | 1214 | nothing |

In every run the client's enemy count matched the host's at each `[desync]` digest (for example
818/818). No errors, no exceptions.

**Not measured:**
- Frame rate, and host CPU, at these counts.
- Bandwidth. The enemy delta stream is byte-budgeted, so a bigger crowd means slower refreshes per
  enemy rather than more traffic. Event traffic (spawns, deaths, hits) does grow. See
  [`../netplay/17-enemy-cap.md`](../netplay/17-enemy-cap.md) for the cost analysis.
- Behaviour under packet loss.
- The lobby button itself: the tests set the config value directly.

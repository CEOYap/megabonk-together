# Session handover 16 — team Difficulty (OB-26), aiming for **Average**

Written 2026-10-18 to start a fresh session on one job: make the game's **Difficulty** stat agree
between players, using the **average** of every player's Difficulty for the shared world.

Branch `claude/ob11-spawn-position`, pushed to `origin` (`CEOYap/megabonk-together`), head `18d084a`,
110 commits ahead of `main`. Build clean. The full background is OB-26 in
[`08-observed-bugs.md`](08-observed-bugs.md); this file is what you need to act on it.

---

## The decision (already made by the project owner)

| Effect | Should follow | Today |
|---|---|---|
| Enemy HP, speed, knockback resistance, spawn count and spawn rate — **the shared world** | **the average of all players' Difficulty** | the host's alone |
| Damage enemies deal to a player — **personal risk** | **that player's own Difficulty** | already correct — **do not touch** |
| Achievements | local | already correct — do not touch |

**Why Average, not Add or Highest** (chosen after the comparison below):

- The mod already scales enemy HP for player count (`GameBalanceService`, `+0.1` per extra player).
  **Add** would scale with player count a second time — six players at +20% each give ×3.48 enemy
  HP against ×1.83 with Average.
- **Highest** forces one player's choice on everyone in full.
- **Average** is fair to an uncursed teammate and stable at any player count, and a cursed player still
  pays personally because incoming damage stays per-player.

### The maths the decision was based on

Constants read from `GameAssembly.dll` (not guessed):

- Enemy HP, Difficulty part: **`1 + 1.1 × D`** (`DAT_18262ef14` = 1.1, `DAT_18262ec7c` = 1.0)
- Damage to a player, Difficulty part: **`1 + D`**
- Mod player-count HP scaling: **`1 + 0.1 × (players − 1)`** → ×1.1 at two players

`D` is a fraction (+40% = 0.40). Example, host 0% and client 40% (Dicehead), two players:

| Rule | D | Enemy HP = (1 + 1.1·D) × 1.1 |
|---|---|---|
| Add | 0.40 | ×1.584 |
| **Average** | **0.20** | **×1.342** |
| Highest | 0.40 | ×1.584 |
| Today (host only) | 0 | ×1.100 |

The spawn-count and spawn-rate formulas also read Difficulty; their constants were **not** extracted.

---

## What is already established (do not redo)

**Difficulty is `EStat.Difficulty = 38` (`0x26`)**, a per-player stat. It is **never replicated** — not
in `PlayerStatsSyncService.TrackedStats`. `GameBalanceService`'s `DifficultyLevel` (Duo, Trio…) is the
mod's automatic **player-count** scaling and is unrelated to stat 38. It stacks on top of the game's
Difficulty already (the mod multiplies the HP the game computed), so the two do not conflict.

**All 128 callers of `PlayerStats.GetStat`** (VA `0x18042B6A0`) were decompiled. **11 read stat 38**:

| Reader | VA | Runs on | Change it? |
|---|---|---|---|
| `EnemyStats.GetHp` | `0x18041B5E0` | host | **yes → team average** |
| `CombatScaling.GetHpMultiplierAddition` | `0x18046F6B0` | host | **yes** |
| `CombatScaling.GetStageHpMultiplier` | `0x18046FDD0` | host | **yes** |
| `EnemyStats.GetSpeed` | `0x18041BC50` | host (← `Enemy.GetSpeed`) | **yes** |
| `EnemyStats.GetKnockbackResistance` | `0x18041B940` | host | **yes** |
| `BaseSummoner.GetMultiplier` | `0x18046CD20` | host | **yes** |
| `StageSummoner.GetNumTargetEnemies` | `0x1804862E0` | host | **yes** |
| `StageSummoner.GetSummonInterval` | `0x1804864B0` | host | **yes** |
| `SwarmSummoner.GetNumTargetEnemies` | `0x180489420` | host | **yes** |
| `EnemyStats.GetDamage` | `0x18041B290` | **victim's machine** | **NO** |
| `AchievementTracker.OnStatUpdate` | `0x1803FC010` | each machine | **NO** |

`EnemyStats.GetDamage` is reached from `DamageUtility.GetPlayerDamage` (two overloads, VAs
`0x180470420` and `0x1804706B0`) and from enemy special attacks. Damage to a player is computed on the
victim's own machine: health is owner-authoritative (`OnPlayerUpdate` copies each owner's HP onto
their avatar) and no host path damages a teammate's avatar. **That is why it must keep the local
value** — it is already the per-player behaviour we want.

None of the 11 touch rewards, silver, XP or drops.

---

## What to build

### 1. Replicate each player's Difficulty — no wire change

Add `(int)EStat.Difficulty` to `PlayerStatsSyncService.TrackedStats`. The stat block is keyed by
`EStat` as an int, so a new key travels in the existing `PlayerStatsChanged` message (tag 77). It is
polled at 2 Hz and sent on change. `GetRemoteStat(ownerId, 38)` then gives each teammate's value.

### 2. Compute the team average on the host

`average = (local Difficulty + Σ each connected teammate's Difficulty) / player count`.

Decide and document: a teammate whose stat block has not arrived yet — **skip them** (average over the
players you have values for) rather than count them as 0, so a late block does not dip the run.
Dead and disconnected players: follow whatever `GetAlivePlayerCount` vs all-players means elsewhere;
recommend **all connected players**, since a dead player's build still defines the run.

### 3. Feed it only to the nine host readers

**Do not** patch `PlayerStats.GetStat` globally — that would also change `EnemyStats.GetDamage` and
break the per-player incoming damage. Instead:

- A scope flag, e.g. `TeamDifficulty.InScope`, set in a **prefix** and cleared in a **finalizer** on
  each of the nine "yes" readers above (finalizer, not postfix, so a throw cannot leave it set).
- A **postfix on `PlayerStats.GetStat(EStat)`** that, only when `InScope` and `stat == 38` and the
  session is started, replaces `__result` with the team average.

Host only in practice (those readers only run on the host), but guard on `IsServerMode` anyway.

**Check before relying on it:** that `PlayerStats.GetStat` is not inlined into any of the nine readers.
The decompiles show it as a direct call (`PlayerStats__GetStat(0x26, …)`), so a postfix reaches it —
confirm the patch actually fires with a log line on first use (see Traps).

### 4. Make it observable

- Log at Info, throttled (once per stage is enough), the team average and each player's value:
  `OB-26: team Difficulty 0.20 (host 0.00, 82462037 0.40) over 2 players`.
- Add the team Difficulty to the desync detector's `StateDigest`? It is computed on the host only, so
  instead add **each player's raw Difficulty** comparison: the client can check that the host's view
  of the client's Difficulty matches the client's own. Optional; the log line may be enough.

---

## Traps this project has paid for before

1. **A diagnostic that cannot run is worse than none.** Add the Info line from point 4 *with* the fix,
   and confirm it appears in the first test log. OB-11 was "confirmed fixed" twice on a counter that
   could not fire.
2. **`nameof` alone fails on overloads**, and the patch then silently never applies. Check each of the
   nine targets for overloads in `dump.cs` first (`DamageUtility.GetPlayerDamage` has two — but that one
   is not being patched).
3. **Send through `INetTransport`, never `IUdpClientService`** — under the Steam transport the latter
   goes nowhere. Not needed here (step 1 reuses an existing message), but it is how two features sent
   nothing for weeks.
4. **The try goes around the call, not inside the method**, when a method touches an Il2Cpp
   collection or an array-taking API — a `MissingMethodException` is raised at JIT of the containing
   method.
5. **Ghidra's struct field names slip by one or two slots.** Trust `dump.cs` offsets.
6. **Decompile before reasoning.** The formulas above came from decompiles; the spawn formulas have not
   been extracted — do it if a number is needed.

## Standing rules

- One logical change per commit; say what is unverified in the body.
- MemoryPack union tags are append-only (78 `StateDigest`, 79 `ChargeShrineCompleted` are the latest).
- Never name another mod or its author in commits, titles or docs.
- Nothing is verified until it is run in-game. Both players must run the same build.

## How to verify in play

Two players, shared experience on. Host plays a character with no Difficulty sources; client plays
**Dicehead** (whose passive raises Difficulty).

| Check | Expected |
|---|---|
| Host log `OB-26: team Difficulty …` | appears, value is the average of both |
| Enemy toughness | noticeably higher than a run where neither player raises Difficulty |
| Client takes hits | harder than the host does (own Difficulty — unchanged) |
| Swap roles (host Dicehead) | enemy toughness the same as before for the same pair of builds |

## State of everything else

Confirmed fixed in play: OB-1, 2, 4, 5, 6, 7, 8, 9, 11, 12, 13, 15, 16, 17, 19, 20, 21, 22, 24, shared
XP. Closed: OB-3, 18, 23. Built but not yet confirmed in play: **OB-14** (damage panel and kill
credit, attributed at the hit; last fixes `abdafe7`) and the OB-1 aura size correction (`4148e19`).
Parked: OB-25 (rocket `LookRotation` warnings, needs a design pass). Deferred to their own branches:
the OmniBonk rename and removing LiteNetLib.

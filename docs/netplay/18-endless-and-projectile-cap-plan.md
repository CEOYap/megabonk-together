# Endless Run and No Projectile Cap — research and plan

**Status: PLAN, nothing implemented.** Written 2026-10-20 from two sources:

- **Our own decompile** of build 21750826. That is authoritative.
- **A single-player reference mod** (MelonLoader, not BepInEx), decompiled for research only. It is
  not named here, per the repo's rules. Nothing below is copied from it. It is used as a map of
  which game methods matter, and every claim taken from it was re-checked against `dump.cs` or a
  decompiled body, or is marked **UNVERIFIED**.

The goal: both features become **lobby settings** that the host sets for the whole session.

- **Endless Run.** After the tier's last stage, travel on through a shuffled selection of Forest,
  Desert, Graveyard and Crypt stages, with a boss room after every three stages.
- **No Projectile Cap.** Remove the game's per-attack projectile ceiling.

The OB-46 notes ("Notes for a future endless-run mode" in `08-observed-bugs.md`) are the starting
point; this supersedes them where they differ.

---

## 1. No Projectile Cap

### What the game does (decompiled)

`WeaponAttack` (layout from `dump.cs`):

| Field | Offset | |
|---|---|---|
| `maxNumProjectilesWithoutInterval` | 0x60 | int, private |
| `attackQuantity` | 0x68 | |
| `attackQuantityCurrent` | 0x6C | |
| `burstInterval` | 0x70 | |

- **`SetAttack`** (`0x1804505F0`) and **`StartAttackNoCoroutine`** (`0x180450C30`) read
  `WeaponUtility.GetAttackQuantity` and `GetBurstInterval` for the attack.
- **`FixedUpdateAttack`** (`0x180450150`) fires them:
  - **With an interval:** one projectile each time the timer passes it, until the quantity is met.
  - **With no interval:** every remaining projectile in one frame, **but the loop breaks at a
    per-attack ceiling and then calls `StopAttackNoCoroutine`**. Projectiles past the ceiling are
    dropped, not delayed.
- **`.ctor`** (`0x1804515D0`) initialises the ceiling to **80**. Ghidra labels the field
  `attackQuantity`, one slot off; by offset it is `maxNumProjectilesWithoutInterval`. **LIKELY**:
  name by slot shift, value and use consistent.

**So the cap bites only when a weapon's Quantity exceeds 80 and its burst interval is 0.** That is
a late-game, high-Quantity build. Below that, the setting changes nothing.

### How to remove it

Set `maxNumProjectilesWithoutInterval` to `int.MaxValue` on every `WeaponAttack`, and keep the
original so it can be restored.

- **Timing.** Do it in a postfix on `SetAttack`, and again before `SpawnProjectile` and after
  `ProjectileDone`, since the game may reset the field.
- **Restoring.** Store originals by native pointer; restore on session end, and when the setting is
  off.

### Netplay considerations

- **Who needs the setting.** The owner's machine fires its own weapons. The host also simulates
  every teammate's weapons (OB-37/38). Both must agree, or the host's simulated volley differs from
  the owner's. So: **a lobby setting**, applied on every peer, never a per-player config.
- **Bandwidth.** Projectiles are announced per spawn (`AbstractSpawnedProjectile`, reliable). 200
  projectiles in one frame means 200 reliable sends from one attack. Measure with `LogBandwidth`
  before shipping. It may need a batched "volley" message; that would be a new union tag, which
  needs its own reason.
- **Performance.** More live projectiles, more hits, more `EnemyDamaged` aggregation (OB-34). Test
  in a late-game swarm.

**Effort: small.** One patch class, one setting, one bandwidth measurement.

---

## 2. Endless Run

### What the game does (decompiled, ours)

- **`InteractableBossSpawner.CanSpawnPortal`** (`0x1804C5540`) =
  `!MapController.IsLastStage() && MapController.index < runConfig.mapTierIndex` (OB-46). That
  check is what ends a tier.
- **Stage loads.** `MapController.LoadNextStage` (`0x180421DF0`) increments `index`.
  `LoadFinalStage` (`0x180421C00`) loads the map's final-boss stage. `IsLastStage` is at
  `0x180421A10`.
- **Maps.** `EMap`: Forest = 1, Desert = 2, Hell = 4, Graveyard = 8. Graveyard is one map whose
  stages include the crypt levels and the Ghost King room.

### How the reference mod does it (UNVERIFIED against our build unless marked)

**The route.** Twelve rooms:

- **Regular (9):** Forest 1–3, Desert 1–3, Crypt 1, Graveyard, Crypt 2.
- **Boss (3):** Ent (Forest final), Pharaoh (Desert final), Ghost King (Graveyard).
- **Rule:** each pool is shuffled (Fisher–Yates) and refilled when empty, never repeating the
  current room first. After every third regular room, the next room comes from the boss pool. This
  is exactly the requested design.

**The jump.** Just before the game would load the next stage:

1. Overwrite `runConfig.mapData` and `stageData`, and `MapController.currentMap`, `currentStage`
   and `index` (set to `stage - 1`), with `isFinalBossStage = false`.
2. Call the game's own `LoadNextStage`, or `LoadFinalStage` for the Ent and Pharaoh rooms.
3. Roll everything back if any step throws.

**Which exits lead to the next room:**

- `InteractablePortal.Interact` on a map's last normal stage;
- a `CanSpawnPortal` postfix forcing a portal on every Forest/Desert stage that is not the map's
  last;
- `LoadNextStage` and `LoadFinalStage` prefixes;
- the Crypt exit (`InteractableCryptLeave.Interact`), the Graveyard key door
  (`InteractableCrypt.Interact`) and the Ghost King exit (`InteractableGhostBossLeave.Interact`).

**What it keeps across a jump:**

- **The player's inventory.** Prefix-false on `PlayerInventory.Cleanup` and
  `ChallengesTracker.OnInventoryInitialized`, and the inventory handed back through
  `MapController.GetPlayerInventory`.
- **Run statistics.** It skips `RunStats.Reset`, `RunStats.OnNewRun`,
  `RunUnlockables.OnNewRunStarted` and `ChallengesTracker.OnNewRunStarted`.
- **`MyTime.runTimer`.**
- **Its own bookkeeping.** A 45 s watchdog that stops Endless if the next map never becomes
  playable.

**Optional scaling.** Enemy HP, damage and speed scale through postfixes on `EnemyStats.GetHp`,
`GetDamage` and `GetSpeed`, using an exponential curve on played time that steepens after a
threshold. The difficulty tier selects the curve.

**Graveyard.** It has its own room handling: which part of the Graveyard map generates, the boss
room's arena initialisation (`GraveyardBossRoom.Update`), and the spawn portal.

### What changes in multiplayer

Our stage changes already work by **every peer pressing the same portal**. The host broadcasts
`InteractableUsed { IsPortal }`, and each peer calls `portal.Interact()` itself, which runs the
game's own stage load (`SynchronizationService`, the `used.IsPortal` branch). Endless keeps that
shape and adds one rule: **the host picks the next room and every peer loads that room.**

1. **Lobby setting.** The host's lobby panel gets Endless (and No Projectile Cap) toggles. A new
   message carries them to every peer before the run starts.
2. **Route on the host only.** The host owns the shuffle and the three-then-boss counter. Clients
   never roll the route; a client-side `Random` would diverge on the first refill.
3. **Next room in the portal message's place.** When the host's exit fires, the host sends
   `EndlessNextRoom { Sequence, Destination }` first, then the existing portal message. Each
   client:
   - sets the same `runConfig` and `MapController` fields;
   - presses its portal as today.

   A client that gets the portal without a room for that sequence waits; it never falls back to the
   game's own next stage.
4. **Every exit is host-decided.** The Crypt, key-door and Ghost King exits must go through the same
   message.
   - **Today:** only `IsPortal` and `IsFinalPortal` are synced paths.
   - **Needed:** a client using the Crypt exit asks the host, the same as OB-6's shrine request.
5. **Inventory and stats.** Keeping the inventory across a jump touches the mod's own inventory
   sync (`PlayerManagerService` inventory store, `NetPlayer` remote inventories, OB-43 item
   copies). That needs its own audit; it is the riskiest part.
6. **Run end.** The run ends only by death. The existing death and revive flow (OB-20, OB-49/53)
   and the end screen apply as they are.
7. **Scaling.** If enemy scaling is wanted, it must follow `GameBalanceService`. That service keys
   on stage index 0/1/2 (OB-46 notes) and the team-average Difficulty (OB-26). Only the host spawns
   enemies, so scaling is host-side; clients receive HP through the spawn and rescale messages.

### Wire changes (append-only, CLAUDE.md)

New union tags after 79:

- **`LobbySettings`:** `EndlessEnabled`, `NoProjectileCap`, and room to grow. Reliable, sent by
  the host on change and on join.
- **`EndlessNextRoom`:** `Sequence`, `Destination` (int), `CompletedMaps`. ReliableOrdered, so it
  arrives before the portal message.

**A version gate is needed.** A peer on an older mod version handshakes fine, but cannot apply
these. Endless must be refused, or the toggle greyed out, unless every peer reports a version that
has them.

### Phases

| Phase | Scope | Risk |
|---|---|---|
| 1 | Lobby-settings message and UI; **No Projectile Cap** behind it | Low |
| 2 | Endless on **Forest and Desert only**: 6 regular rooms plus Ent and Pharaoh, host-chosen route, portal exits only | Medium: the inventory and stat carry-over |
| 3 | Add **Graveyard, Crypt 1/2 and Ghost King**: their exits, room generation and boss arena | High: Graveyard sync is the least-tested part of the mod |
| 4 | Optional enemy scaling, folded into `GameBalanceService` | Medium |

Each phase is its own build and playtest. Phase 2 confirms in play when:

- three peers travel several rooms together;
- the boss room arrives on every third;
- inventories survive each jump;
- digests stay in sync throughout.

### Open questions (decompile before building)

- **Stage-load inputs.** Does `MapController.LoadNextStage` read anything besides `runConfig` and
  the static map fields? Decompile `0x180421DF0` and `0x180421C00`.
- **Inventory cleanup.** Where does `PlayerInventory.Cleanup` run on a stage load? Decompile its
  callers, and the mod's own `ResetForNextLevel` set, to know what a jump must keep.
- **Graveyard layout.** How does the Graveyard map pick between crypt and graveyard layouts? The
  reference mod keys room generation off the current room; find the game method it steers.
- **Lobby code.** Does the lobby panel (`Scripts/Modal/LobbyPanel.cs`) have a place for host-only
  toggles, or does it need a new row?

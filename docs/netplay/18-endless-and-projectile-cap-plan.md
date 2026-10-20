# Endless Run and No Projectile Cap — research and plan

**Status:** Phases 1 and 2 built 2026-10-20: lobby settings, No Projectile Cap, and Endless on Forest and Desert. **UNVERIFIED, not run in-game.** Phases 3 and 4 are still a plan. Written 2026-10-20 from two sources:

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

### Phase 1, as built (2026-10-20)

- **Wire.** `LobbySettings` (union tag **80**) carries `NoProjectileCap` plus `EndlessEnabled`.
  `EndlessEnabled` is reserved, so Phase 2 adds no field to a message peers already speak. The
  client route republishes it through `EventManager`.
- **`LobbySettingsService`.** The host snapshots its `[Lobby]` config at run start and sends it from
  `SynchronizationService.OnRunStarted`, just before `RunStarted` on the same ReliableOrdered
  channel. A client applies the host's values and never its own. Everything returns to vanilla on
  `SynchronizationService.Reset`, which also restores every patched attack.
- **`NoProjectileCapPatches`** (`Patches/WeaponAttack.cs`). A `SetAttack` postfix sets the cap to
  `int.MaxValue` while the session is started and the setting is on, keeping the original. Without
  the setting it restores the original.
- **UI.** A `No Proj. Cap: ON/OFF` toggle on the lobby's Options page, next to Saves and Shared XP.
  It writes `[Lobby] NoProjectileCap`. Only the host's value counts.
- **Logs.** The host prints `[lobby-settings] Host settings for this session: ...`. Each client
  prints `[lobby-settings] Applying the host's settings: ...`.

**Mixed versions.** A peer on an older build does not know tag 80. It drops the message and keeps
the cap, so its volleys differ from the host's. The plan's version gate is not built yet; until
then, everyone in a session should run the same build.

**Confirm in play:**

- **Logs.** The host turns it on, and both bots log `Applying the host's settings: no projectile
  cap ON`.
- **Volleys.** A weapon with Quantity above 80 and no burst interval fires every projectile on
  every peer.
- **Bandwidth.** The `[bw]` projectile lines stay within budget in a late-game swarm.
- **Off again.** Turning it off brings the cap back next run, with singleplayer unaffected.

### Phase 2, as built (2026-10-20)

**What the decompile settled** (the open questions below are answered for Forest and Desert):

- **`MapController.LoadNextStage`** (`0x180421DF0`), unless `reseting`, does four things in order:
  1. stores `GameManager.GetPlayerInventory()` into `MapController.inventory`;
  2. increments `index`;
  3. sets `currentStage = currentMap.stages[index]`;
  4. starts the scene load.

  **So the inventory already carries across any stage load.** No inventory patches are needed when
  the run stays within these maps.
- **`LoadFinalStage`** (`0x180421C00`) does the same, but sets `isFinalBossStage` where the other
  increments `index`.
- **`StartNewMap`** (`0x1804222F0`) cleans up the old inventory and then calls `LoadNextStage` for
  stage 1.
- **`InteractablePortalFinal.Interact`** (`0x1804CCA20`) sets `done` and starts `DoFinishGame`,
  which ends the run.

**Built:**

- **Wire.** `EndlessNextRoom { Sequence, Room }`, union tag **81**, host to clients,
  ReliableOrdered. `LobbySettings.EndlessEnabled` is now live, from `[Lobby] EndlessRun`, with an
  `Endless: ON/OFF` toggle on the lobby's Options page.
- **`Helpers/EndlessRooms`.** The room ids are wire values: Forest 1–3 = 0–2, Desert 1–3 = 3–5,
  Graveyard rooms 6–8 reserved, Ent 9, Pharaoh 10, Ghost King 11 reserved. The table also holds
  the map and stage of each room, and a Fisher–Yates refill that never repeats the current room
  first.
- **`EndlessRunService`.** `BeginRun` runs on every peer after `StartNewMap`: on the host from
  `OnRunStarted`, after `RunStarted` is sent; on a client at the end of `OnReceivedRunStarted`. It
  needs a Forest or Desert start, and logs and stays off otherwise.
  - **The host** rolls the route with `System.Random`. Unity's generator is seeded per run and
    shared with map generation, so the host must not consume it. It sends each room one stage
    change ahead: room 1 at run start, room *n + 1* while applying room *n*.
  - **The route:** three regular rooms, then Ent or Pharaoh, from separately shuffled pools.
- **Stage changes.** `LoadNextStage` and `LoadFinalStage` prefixes hand every stage change to the
  service. For each room, the service:
  1. sets `runConfig.mapData`/`stageData`, `currentMap`, `currentStage` and
     `isFinalBossStage = false`;
  2. sets `index` to the room's stage, minus 1 for a regular room;
  3. lets the matching load run, or calls the other one itself when the game asked for the wrong
     kind.

  `StartNewMap`'s own first load is excluded.
- **Exits.**
  - **Regular rooms.** `CanSpawnPortal` is forced true on every regular room.
  - **Boss rooms.** `InteractablePortalFinal.Interact` loads the next stage instead of
    `DoFinishGame`, and `SynchronizationService` treats that portal as `PortalOpened`, not
    `FinalPortalOpened`, on both its send and receive paths.
- **Failure handling.** A client that reaches a stage change without the host's room, or a room
  without stage data, logs `[endless] ...` and turns Endless off for the run, rather than loading a
  stage the host did not choose.
- **Logs.** `[endless] Endless Run on; starting room X`, then `[endless] Stage change N: Room` on
  every peer.

**Not covered by Phase 2:**

- **Graveyard and Crypt.** They are reserved, not in the route.
- **Enemy scaling.** Difficulty still grows only with time, as in vanilla.
- **The version gate.**
- **The final-boss flow** (OB-7/8/19) is reused as it is on each boss room. That it holds up being
  entered several times in one run is **UNVERIFIED**.

**Confirm in play:**

- **Rooms agree.** Host plus two bots, Endless on, starting on Forest stage 1. Every peer logs the
  same `Stage change N: Room` sequence, and a boss room is every fourth change.
- **State survives.** Inventories and levels carry through each change, and digests stay in sync.
- **Boss exits.** The portal after Ent or Pharaoh leads on rather than ending the run.

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

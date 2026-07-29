# Investigation Targets

Concrete game types and members to inspect, each tied to an open question in the code. Ordered
by how much they block.

Method and tooling: [`00-decompilation-guide.md`](00-decompilation-guide.md).

Record answers **in this file**, with the game version — findings expire on every game update.

| Tag | Meaning |
|---|---|
| **BLOCKING** | A code decision cannot be made correctly without this |
| **IMPORTANT** | Would resolve a known TODO or a suspected bug |
| **NICE** | Would enable a feature or remove a hack |

---

<a name="basesummoner"></a>
## 1. `BaseSummoner` — BLOCKING

**Blocks:** whether the credit-timer balance patch can ever be enabled.
**Code:** `src/plugin/Patches/Summoner/BaseSummoner.cs` (entire file commented out, upstream
note `//TODO: re enable again when no more FPS drops`);
`src/plugin/Services/GameBalanceService.GetCreditsTimerMultiplier()`

### Questions

1. **What is `giveCreditsTimer`?** A fixed interval reset after each grant, or a countdown
   decremented per tick, or an accumulator?
2. **Where is it written?** Does `Tick()` reassign it from a constant each call, or carry it
   across calls?
3. **How often does `Tick()` run?** Per frame, per `FixedUpdate`, or on its own cadence?
4. **How many `BaseSummoner` instances exist concurrently?** The patch cost scales with this.
5. **What are the `BaseSummoner` subclasses?** `[HarmonyPatch(typeof(BaseSummoner))]` may or
   may not catch derived overrides. `ChallengeSummoner` and `SummonerController` are already
   patched separately.

### Why it matters

The commented-out patch is a `Tick` **prefix**:

```csharp
__instance.giveCreditsTimer *= gameBalanceService.GetCreditsTimerMultiplier();
```

`GetCreditsTimerMultiplier()` returns 1.01–1.05 × 1.00–1.07 — **always greater than 1**. If
`Tick()` does not reassign `giveCreditsTimer` from a constant before the prefix observes it,
this compounds geometrically: 1.05^N. At 50 ticks/second the summoner stops issuing credits
within seconds and enemy spawning collapses.

If instead `Tick()` sets it fresh each call, the patch is a per-tick scale and roughly does
what it says.

`Sea-Bass-cmd/optimized-netplay` re-enables this **verbatim** in `8e02879`, fixing nothing
that caused the original disable. Two supporting signals that it was not thought through:
`GameBalanceService.Initialize()` still logs `"Credits Timer Multiplier (Disabled)"`, and
`GetCreditsTimerMultiplier()` → `PlayersCount` → `GetAllPlayersAlive()` allocates a `Player[]`
plus two LINQ iterators **per summoner per tick**.

### Method

UnityExplorer is enough. Find a `BaseSummoner` in the scene, watch `giveCreditsTimer` for
30 seconds with the patch disabled. A sawtooth means interval-reset (patch is roughly safe);
monotonic drift means the patch will compound.

Then `grep -n -A 60 "^public class BaseSummoner" dump.cs` for the field list and the `Tick`
RVA, and read `Tick` in Ghidra to confirm.

### Consequence

- **Sawtooth / reassigned each tick** → the patch is viable. Still move it to a postfix that
  scales the *reset* value, and cache `GetCreditsTimerMultiplier()` (see
  [`../netplay/04-performance-and-gc.md`](../netplay/04-performance-and-gc.md#3-gamebalanceservice--recomputed-per-call))
  before enabling — the FPS reason for the original disable is unaddressed either way.
- **Carried across ticks** → the patch is broken as written. Apply the multiplier once at
  spawn, or patch whatever assigns the interval.

**Finding:** _not yet investigated_

---

<a name="chargeshrine"></a>
## 2. `ChargeShrine` — IMPORTANT

**Blocks:** whether the `Start` postfix in [P1-2](../netplay/01-critical-fixes.md#p1-2) is
needed or dead weight.
**Code:** `src/plugin/Patches/ChargeShrine.cs`

### Questions

1. **Does `ChargeShrine.Start()` write `isGolden`?** If it does not, the postfix
   `Sea-Bass-cmd` adds is pointless and the receive-side assignment is sufficient.
2. **When does `Start()` run relative to `Instantiate` returning?** Unity runs `Start` on the
   first frame after activation; the spawned-object handler may run before or after.
3. **Is `isGolden` read once at `Start` or every frame?** Determines whether a late write
   still takes effect.
4. **What else does `isGolden` gate?** Reward table, visual, charge time — each may need its
   own sync.
5. **`OnTriggerEnter` / `OnTriggerExit` signature.** The mod calls these directly
   (`SynchronizationService.cs:2809`, `:2915`) with `Plugin.CAN_SEND_MESSAGES = false` around
   them. Confirm they are parameterless and have no other side effects.

### Method

`grep -n -A 80 "^public class ChargeShrine" dump.cs`, then read `Start` in Ghidra. UnityExplorer
can confirm the timing: watch `isGolden` on a client from spawn.

**Finding:** _not yet investigated_

---

<a name="poolmanager"></a>
## 3. `PoolManager` / `Pickup` / `PickupManager` — IMPORTANT

**Blocks:** any future per-object network state design; explains a class of ownership bugs.
**Code:** `src/plugin/Patches/PoolManager.cs`, `Patches/Pickup.cs`, `Patches/PickupManager.cs`

### Questions

1. **Which object classes are pooled?** Enemies, pickups, projectiles — confirm each.
2. **On despawn, is the object `Destroy`ed or deactivated and requeued?**
3. **Is there a reset/clear hook** on pool return or pool fetch that a mod could patch?
4. **`PickupManager.DespawnPickup(Pickup)`** — does it destroy or requeue? Called at
   `SynchronizationService.cs:1770` and `:1877`.
5. **Do `WeaponBase` and its `Attack` (`PoolManager.cs`, `__result`) share a GameObject?**
6. **Are `DamageContainer` instances pooled or freshly allocated?**

### Why it matters

Two things depend on this.

**(a) The `NetEntity` design is wrong if objects are pooled.** `Sea-Bass-cmd`'s `NetEntity`
cleans up only in `OnDestroy`. Pooled objects are never destroyed, so stale `NetId`/`OwnerId`
survives recycling — a recycled pickup carries the previous owner. If Q2 confirms pooling, any
future replacement for `DynamicData` must clear state on **pool return**, not on destroy. See
[`../netplay/03-cherry-pick-guide.md`](../netplay/03-cherry-pick-guide.md#netentity).

**(b) Q5 decides whether GameObject-level keying is safe at all.** `DynamicData.For(component)`
keys the component. Anything keyed on the GameObject collapses all components on it into one
record. `PoolManager.cs` reads `weaponBase`'s `ownerId` and writes `__result`'s; if they share
a GameObject those are the same record. `WeaponUtility.GetDamageContainer_Postfix` has an
"already assigned?" guard that breaks the same way.

### Method

UnityExplorer is decisive here. Watch the scene hierarchy during a pickup cycle — if the
GameObject persists inactive after pickup, it is pooled. Then select a weapon GameObject and
list its components for Q5.

**Finding:** _not yet investigated_

---

<a name="damagecontainer"></a>
## 4. `DamageContainer` — IMPORTANT

**Blocks:** the TODO at `src/plugin/Patches/WeaponUtility.cs:51` —
`//TODO: track DamageContainer so we dont have to do this check`
**Code:** `Patches/WeaponUtility.cs`, `SynchronizationService.cs:1537`, `:1608`, `:3898`

### Questions

1. **Is `DamageContainer` a `Component`, a plain managed class, or a struct?** Determines
   which per-object-state mechanism can attach to it at all.
2. **Constructor signature** — the mod builds them at `SynchronizationService.cs:1537`
   (`new DamageContainer(damaged.DamageProcCoefficient, damaged.DamageSource)`) and `:1608`
   (`new DamageContainer(0.0f, died.DamageSource)`). Confirm both overloads and any fields
   left unset.
3. **Is there a spare field** — an unused int/enum — that could carry an owner ID directly,
   removing the need for side-table tracking?
4. **Lifetime.** Created per hit and discarded, or pooled?
5. **Does `DamageSource` have a stable identity** usable for attribution?

### Why it matters

Ownership attribution ("who dealt this damage, so gold flies to the right player") currently
rides on a side table keyed by the `DamageContainer` instance. Both `DynamicData` and any
`ConditionalWeakTable`-based replacement key on **managed reference identity**, and
Il2CppInterop can produce more than one managed wrapper for the same native object. If Q3
finds a usable spare field, the whole side table goes away and the attribution becomes exact.

**Finding:** _not yet investigated_

---

## 5. `GameManager.IsFinalSwarm()` and the enemy cap — IMPORTANT

**Blocks:** confident tuning of `GameBalanceService.GetMaxEnemiesSpawnable()`.
**Code:** `src/plugin/Services/GameBalanceService.cs:38`

### Questions

1. **What reads the value returned by `GetMaxEnemiesSpawnable()`?** Which game field is being
   patched, and is it a hard cap or a spawn-budget input?
2. **Is the cap enforced per spawner or globally?**
3. **What does `IsFinalSwarm()` actually test,** and how long does the final swarm last?
4. **What is the vanilla cap** in each phase? `NETPLAY_CHANGES.md` says 400 is "the original
   cap" — confirm.

### Why it matters

`Sea-Bass-cmd` changes the final-swarm return from `400` to `baseCap + 200` (700 or 800). We
rejected it, but the underlying numbers are unverified guesses on our side too — the existing
500/600 caps are documented as *"untested, you have been warned"*. Knowing what the number
actually controls is a prerequisite for tuning it rather than guessing.

**Finding:** _not yet investigated_

---

## 6. `Enemy` — IMPORTANT

**Blocks:** confidence in the target-assignment fix and the delta thresholds.
**Code:** `Patches/Enemies/Enemy.cs`, `Services/EnemyManagerService.cs`,
`Scripts/Enemies/TargetSwitcher.cs`

### Questions

1. **`Enemy.target`** — type (`Rigidbody`?), and what reads it. Confirms whether
   [P0-4](../netplay/01-critical-fixes.md#p0-4)'s restored assignment is the only thing that
   redirects an enemy.
2. **`Enemy.InitEnemy`** — full signature and when it runs relative to pool fetch.
3. **Does anything else write `target`** between `InitEnemy` and `TargetSwitcher.Update`?
   This determines the real duration of the host-aggro bias when the assignment is missing.
4. **`enemyFlag` / `enemyData.enemyName`** — the full `EEnemyFlag` and `EEnemy` sets, for the
   `InitializeSwitcher` switch (`EnemyManagerService.cs:218`, carrying
   `//TODO: the applied values should be stored in GameBalanceService`).
5. **`basePowerupDropChance` and `minStayAtDistance`** — the client branch zeroes both
   (`Enemy.cs`). Confirm these are the only client-side suppressions needed.

**Finding:** _not yet investigated_

---

## 7. Steam integration surface — IMPORTANT (blocks the migration)

**Blocks:** [`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md) Phase 2.
**Code:** `Patches/SteamAchievementsManager.cs`, `Patches/SteamStatsManager.cs`,
`Patches/LeaderBoards.cs`, `Patches/SaveManager.cs`

### Questions

1. **Where does the game call `SteamAPI.Init()`,** and where does it pump
   `SteamAPI.RunCallbacks()`? Frequency and call site.
2. **Which Steamworks.NET version** does `Il2Cppcom.rlabrecque.steamworks.net.dll` correspond
   to?
3. **Does the game already use `ISteamNetworkingSockets` or `SteamMatchmaking` for anything?**
   If so, virtual-port and lobby collisions are possible.
4. **Do `SteamAchievementsManager` / `SteamStatsManager` / `Leaderboards` route through the
   IL2CPP wrapper exclusively?** If we add a second managed wrapper, could a call path bypass
   our suppression patches?

### Why it matters

Q4 is the one that can hurt players. The mod deliberately blocks Steam writes during netplay —
`NETPLAY_CHANGES.md` is explicit that uploading a netplay score would get people banned.
Introducing a second managed Steamworks wrapper is exactly the kind of change that could route
around a suppression patch. **Verify before shipping.**

Q1 determines whether calling `SteamAPI.RunCallbacks()` ourselves (as Multibonk does every
frame from `LobbyManager.Update()`) double-fires the game's own callbacks.

**Finding:** _not yet investigated_

---

## 8. `NetPlayer` lifetime and the dangling transform — NICE

**Blocks:** removing the hack at `Patches/Unity/UnityComponent.cs:27`
(`//TODO: i'm pretty sure its a netplayer dangling reference but how do i even debug this...`)

### Questions

1. **Which `Component.transform` call is hitting a destroyed object?** The rate-limited log
   from [P2-1](../netplay/01-critical-fixes.md#p2-1) plus a stack trace should identify it.
2. **What retains a `NetPlayer` reference past `Destroy`?** Prime suspects:
   `TargetSwitcher.currentTarget` — a `(Transform, Rigidbody)` tuple cached with no
   invalidation on player despawn — and `PlayerManagerService.cs:466`
   (`//TODO: cleanup inventories at some point`).
3. **Does `Enemy.target` survive its `NetPlayer` being destroyed?** Same class of problem.

This one is best attacked from our own code with logging rather than by decompiling — the bug
is almost certainly in the mod, not the game. Land P2-1 first and let the log tell you.

**Finding:** _not yet investigated_

---

## 9. `LaserBeamAttack` and constant attacks — NICE

**Blocks:** `src/plugin/Patches/ConstantAttacks/LaserBeamAttack.cs:8` —
`//TODO: Synchronize laser beam attacks properly`

### Questions

1. **How is the beam represented?** Persistent object, per-frame raycast, or a particle
   system?
2. **What is the minimum state needed to reproduce it on a client** — origin, direction,
   duration, owner?
3. **How does it apply damage?** Per-frame ticks or on-enter — determines whether it can ride
   the existing damage path.
4. **Same questions for `AegisAttack` and `ProjectileDragonBreath`,** which are also in
   `ConstantAttacks/`.

**Finding:** _not yet investigated_

---

## 10. `DataManager.GetEnemyDataByName` — NICE

**Blocks:** `src/plugin/Extensions/DataManager.cs:7` —
`//TODO: remove this extension, there is already one in the base game`

### Question

Find the base-game equivalent in `dump.cs`
(`grep -n -A 40 "^public class DataManager" dump.cs`), confirm the semantics match — in
particular the missing-key behaviour, since our extension logs a warning and returns `null` —
and delete the extension.

`Sea-Bass-cmd` deletes this file in `0c7e313` without identifying the replacement. Do the
lookup first.

**Finding:** _not yet investigated_

---

## 11. Interpolators and IL2CPP abstract classes — NICE

**Blocks:** `Scripts/Snapshot/EnemyInterpolator.cs:7` and `PlayerInterpolator.cs:7` —
`//TODO: Find a way to make abstract class work with IL2CPP because its not working for some reason ¯\_(ツ)_/¯`

### Question

This is an Il2CppInterop `ClassInjector` limitation, not a game question. Check whether the
BepInEx IL2CPP version in use supports injecting abstract types or types with a base class,
and whether `RegisterTypeInIl2Cpp` needs the base registered first. Current duplication between
the two interpolators is the cost.

Reference: Il2CppInterop `ClassInjector` docs and the BepInEx 6 changelog for the pinned
version (`BepInEx.Unity.IL2CPP 6.0.0-be.*`, `src/plugin/MegabonkTogether.Plugin.csproj:39`).

**Finding:** _not yet investigated_

---

## Quick reference — every open TODO in the codebase

For context when picking up any of the above.

| File:line | TODO | Related target |
|---|---|---|
| `Extensions/DataManager.cs:7` | remove, base game has one | [#10](#10-datamanagergetenemydatabyname--nice) |
| `Scripts/Snapshot/EnemyInterpolator.cs:7` | abstract class + IL2CPP | [#11](#11-interpolators-and-il2cpp-abstract-classes--nice) |
| `Scripts/Snapshot/PlayerInterpolator.cs:7` | same | [#11](#11-interpolators-and-il2cpp-abstract-classes--nice) |
| `Services/FinalBossOrbManagerService.cs:30` | simplify | — |
| `Services/UdpClientService.cs:186` | should use a connection key | [P1-3](../netplay/01-critical-fixes.md#p1-3) |
| `Services/UdpClientService.cs:201` | should validate the request key | [P1-3](../netplay/01-critical-fixes.md#p1-3) |
| `Services/UdpClientService.cs:1042` | move `OnLobbyUpdate` to `SynchronizationService` | — |
| `Services/SpawnedObjectManagerService.cs:41` | concurrency? | [P0-3](../netplay/01-critical-fixes.md#p0-3) |
| `Services/EnemyManagerService.cs:40` | concurrency? | [P0-3](../netplay/01-critical-fixes.md#p0-3) |
| `Services/EnemyManagerService.cs:217` | values belong in `GameBalanceService` | [#6](#6-enemy--important) |
| `Services/WebsocketClientService.cs:268` | `Task.Run` might be useless now | deleted by the Steam migration |
| `Services/PickupManagerService.cs:25` | concurrency? | [P0-3](../netplay/01-critical-fixes.md#p0-3) |
| `Services/PlayerManagerService.cs:466` | cleanup inventories | [#8](#8-netplayer-lifetime-and-the-dangling-transform--nice) |
| `Services/SynchronizationService.cs:742` | auto-cancel spawn routine after X seconds | — |
| `Services/SynchronizationService.cs:933` | Desert branch untested | — |
| `Services/SynchronizationService.cs:971` | Graveyard branch untested | — |
| `Services/SynchronizationService.cs:1521` | "can be unreliable" — already is; stale, delete | [02-delivery](../netplay/02-delivery-method-reference.md) |
| `Services/SynchronizationService.cs:1612` | track `dmgContainer`? | [#4](#4-damagecontainer--important) |
| `Services/SynchronizationService.cs:2283` | ShadyGuy/Microwave special-cased | — |
| `Services/SynchronizationService.cs:3145` | pylon and lamp should share logic | [P2-3](../netplay/01-critical-fixes.md#p2-3) |
| `Patches/Summoner/BaseSummoner.cs:3` | re-enable when no more FPS drops | [#1](#1-basesummoner--blocking) |
| `Patches/ConstantAttacks/LaserBeamAttack.cs:8` | synchronize properly | [#9](#9-laserbeamattack-and-constant-attacks--nice) |
| `Patches/WeaponUtility.cs:51` | track `DamageContainer` | [#4](#4-damagecontainer--important) |
| `Patches/LevelUpScreen.cs:10` | refactor with `ChestWindowUiPatch` | — |
| `Patches/Unity/UnityComponent.cs:27` | dangling netplayer reference | [#8](#8-netplayer-lifetime-and-the-dangling-transform--nice) |
| `Patches/ChestWindowUI.cs:37` | refactor with `LevelUpScreenPatch` | — |
| `Patches/Enemies/Enemy.cs:51` | could be simplified | [P0-4](../netplay/01-critical-fixes.md#p0-4) |
| `Server/Services/WebSocketHandler.cs:523` | `RunStatistics` unused | deleted by the Steam migration |

---

## Patched game types

113 patch files reference these types. Useful as an index of the game surface the mod already
depends on — a game update touching any of them is a compatibility risk.

<details>
<summary>Full list</summary>

`AegisAttack`, `BaseSummoner`, `BossLamp`, `BossOrb`, `BossOrbBleed`, `BossOrbShooty`,
`BossPylon`, `ChallengeSummoner`, `CharacterMenu`, `ChargeShrine`, `ChestOpening`,
`ChestWindowUi`, `CinematicBars`, `CombatAura`, `Component`, `DesertStorm`,
`DetectInteractables`, `EffectManager`, `EncounterUi`, `EncounterWindows`, `Enemy`,
`EnemyManager`, `EnemyMovementRb`, `EnemyProjectileMud`, `EnemySpecialAttackCactusProjectile`,
`EnemySpecialAttackPrefabSingle`, `EnemySpecialAttackTargetLaser`, `EnemyStats`,
`FinalFightController`, `FullMap`, `GameManager`, `GenerateTileObjects`, `GraveyardBossRoom`,
`InteractableCharacterFight`, `InteractableCoffin`, `InteractableDesertGrave`,
`InteractableMicrowave`, `InteractableShadyGuy`, `InteractableSkeletonKingStatue`,
`InteractableTumbleWeed`, `ItemGhost`, `ItemInventory`, `ItemSoulHarvester`, `LaserBeamAttack`,
`Leaderboards`, `LevelupScreen`, `LocalizedString`, `MainMenu`, `MapController`,
`MapGenerator`, `Maze`, `MazeHeightGenerator`, `MoneyUtility`, `MyInputManager`, `MyPlayer`,
`MyTime`, `OpenChest`, `PassiveAbilityBullseye`, `PauseUi`, `Pickup`, `PickupManager`,
`PickupOrb`, `PlayerCamera`, `PlayerHealth`, `PlayerInventory`, `PlayerMovement`,
`PlayerRenderer`, `PlayerStatsNew`, `PlayerXp`, `PoolManager`, `ProjectileAxe`,
`ProjectileBanana`, `ProjectileBase`, `ProjectileBlackHole`, `ProjectileCringeSword`,
`ProjectileDexecutioner`, `ProjectileDragonsBreath`, `ProjectileHeroSword`,
`ProjectileLightningBolt`, `ProjectileMelee`, `ProjectileRocket`, `ProjectileScythe`,
`ProjectileShotgun`, `RandomObjectPlacer`, `Rarity`, `ReturnWeaponWui`, `Rigidbody`, `Rocket`,
`RsgController`, `SaveManager`, `SkinSelection`, `SpawnInteractables`, `SpawnPlayerPortal`,
`SpawnPositions`, `SpecialAttackController`, `SteamAchievementsManager`, `SteamStatsManager`,
`StealWeaponWui`, `SummonerController`, `TargetOfInterestPrefab`, `TomeInventory`,
`TomeUtility`, `TrackStats`, `Transform`, `UnityEngine.Object`, `WeaponAttack`,
`WeaponInventory`, `WeaponUtility`, `WindowManager`

Plus compiler-generated coroutine state machines: `RsgController._GenerateMap_d__41`,
`_DoAttack_d__5`, `_DoAttack_d__7`, `_DoAttack_d__8`, `_GenerateMap_d__15`, `_GenerateMap_d__39`.
These names are **compiler-generated and unstable across game builds** — a recompile can
renumber them. They are the most likely thing to break on a game update; check them first when
the mod stops loading.

</details>

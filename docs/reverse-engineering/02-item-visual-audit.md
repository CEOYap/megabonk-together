# Item and weapon visuals confined to one machine — audit

Build 21750826. Written 2026-10-23 from decompiled bodies in `megabonk-re/decompiled/` and from
reading the mod's source. VAs come from `megabonk-re/build-21750826/dump.cs` (VA, not RVA). Ghidra's
field names were 8 bytes off on `ItemProjectile`, `ItemProjectileOrb`, `Rocket` and `EffectManager`;
the offsets below are dump.cs's.

**Status:** family 1 (`ItemProjectile`) is fixed by tag 85 `ItemProjectilesSpawned` (protocol 5), not
yet run in game. Families 2 to 4 and the non-visual cases are open. Bug log entry:
[OB-56](../netplay/08-observed-bugs.md). Legend: CONFIRMED means decompiled; anything else is marked
UNVERIFIED.

---

## Why it happens

The mod runs each item on exactly one machine:

| Who runs it | Mod code | Items |
|---|---|---|
| Host | `HitItemOwnership.cs` (clients skip `ItemInventory.PostDamage`, OB-37); `EventListenerOwnership.RunOnHost` (kill and hit listeners, OB-39) | on-hit procs, on-kill items, some passives |
| Owner | `OwnerOnlyItems.cs` (OB-43), `RemoteItemCopies.cs`, `ItemGhost.cs` | timed items, reactions to the owner being hit |

Anything an item spawns exists only on that machine. Consequences:

- Host-run visuals are invisible to every client, **including the item's owner**: a client never sees
  its own on-hit procs (Slutty Cannon rockets, gloves, Spicy Meatball and so on).
- Owner-run visuals are invisible to everyone else.
- Gameplay is unaffected: damage is applied once, on the machine that runs the item.

---

## Affected items

Pools are fields of `PoolManager.Instance` (static `+0x18`), taken with `ObjectPool<GameObject>.Get`.
EM means `EffectManager`.

### Host-run

| Item / passive | Method (VA) | Visual and how it is made | Replicated |
|---|---|---|---|
| Soul Harvester | `OnEnemyDied` 0x180465B70; `SpawnProjectile` 0x180465F10 | "Red ghost": `angrySoulPool` 0x208 → `ItemProjectile.Set` at the enemy, `numProjectiles` per kill | **Yes, tag 85** |
| Slutty Cannon | `ProcOnHitEffects` 0x1804646F0 | `rocketPool` 0x1D0 → `Rocket.Set(pos = MyPlayer + up, weaponBase = null, useGenericPool = true)`; explosion from `explosionPool` 0x1D8 in `Rocket.ProjectileDone` | No |
| Bonker | `ProcOnHitEffects` 0x18043B230 | `bonkPool` 0x1E0 | No |
| Ice Cube | `ProcOnHitEffects` 0x18045DBE0 | `freezeFxPool` 0x1C0 | No |
| Dragonfire | `ProcOnHitEffects` 0x180454C90 | `firefieldPool` 0x1C8 → `Firefield.Set` (0x18041E8E0) under the enemy. **Damaging object, not a visual** | No |
| Grandma's Tonic | `LateFixedUpdate` 0x18045BF40 | `grandmaTonicPool` 0x198 | No |
| Quin's Mask | `ProcOnHitEffects` 0x1804631A0 | `quinMaskPool` 0x248 | No |
| Spicy Meatball | `ProcOnHitEffects` 0x1804671E0 | `spicyMeatballPool` 0x1B0 | No |
| Snek | `ProcOnHitEffects` 0x180464CF0 | `snekPool` 0x250 | No |
| Gloves: Blood / Cursed / Lightning / Poison / Power | 0x180458670 / 0x1804591A0 / 0x180459990 / 0x18045A0E0 / 0x18045A980 | Instantiate the EM prefab once into the item's `fx` (`EffectPlayer`), move it, `EffectPlayer.Play` (0x180517CA0). Prefabs: `gloveBlood` 0x190, `gloveCurse` 0x198, `gloveLightning` 0x180, `glovePoison` 0x188, `glovePower` 0x1A0 | No |
| Lightning Orb | `ProcOnHitEffects` 0x1804600C0 | `WeaponUtility.LightningStrike` → `lightningStrikePool` 0xF0, and `chainLightningPool` 0x1B8 via `ChainLightning` 0x1804340F0 | Yes, tag 41 (see the lead below) |
| Borgor | `OnEnemyDied` 0x18043BCE0 | `borgorPool` 0x218 → `Borgar.Set`. **A pickup, not a visual:** `Borgar.OnCollisionEnter` (0x18048AC10) heals `MyPlayer.Instance` | No |
| Zooma (passive) | `OnEnemyDamage` 0x1804800F0 | `EM.ZapEffect(pos)` 0x1805025D0: instantiate once, then Play | No |
| Plague (passive) | `OnEnemyDied` 0x18047B730 | `poisonPool` 0x128 | No |
| Bullseye (passive) | `OnEnemyDamaged` 0x180474EA0 | `explosionPool` 0x1D8 | No |

### Owner-run

| Item | Method (VA) | Visual and how it is made | Replicated |
|---|---|---|---|
| Energy Core | `Tick` 0x180456600 → `FireOrb` 0x180456280 | `orbPool` 0x1F8 → `ItemProjectile.Set` on an `ItemProjectileOrb` that orbits `MyPlayer.Instance`, then fires | **Yes, tag 85** |
| Ghost | `OnInteracted` 0x180457DA0 / `SpawnGhost` 0x180457EE0 | `EM.SpawnGhostProjectile` 0x180501370 → `ghostPool` 0x200 → `ItemProjectile.Set` at MyPlayer. If the pool is empty it calls `AddDamage` on a random live ghost instead | **Yes, tag 85** |
| Bob (Dead) | `Tick` 0x18043A5D0 / `SpawnGhost` 0x18043A4D0 | Same `SpawnGhostProjectile` / `ghostPool` path | **Yes, tag 85** |
| Cursed Doll | `Tick` 0x18043FC70 | `cursedHitPool` 0x1F0 on each cursed enemy | No |
| Cactus | `OnTakeDamage` 0x18043C750 | `cactusPool` 0x230, plus `EM.EnemyHitEffect` | No |
| Electric Plug | `OnPlayerHit` 0x180455BB0 | `EM.ElectricalPlugEffect` 0x1804FBC80 (EM `electricPlugFx` 0xB8, parented to MyPlayer), plus `EnemyHitEffect` | No |
| Mirror | `ReflectDamage` 0x180460E10 | `EM.SpawnMirrorFx(pos, dir, size)` 0x180501720, plus `EnemyHitEffect` and `PopupText` | No |
| Holy Book | `Tick` 0x18045D250 | `EM.EnemyHitEffect` per enemy hit. `HolyBookEffect.OnUse` is local-holder only (OB-48) | No |
| Toxic Barrel | `Activate` 0x180468080 | Static `A_OnUse` → `ToxicBarrelEffect.OnUse` (0x180498A20) → `EffectPlayer.Play`, attached to the player | No |
| Campfire | `CreateCamp` 0x18043D380 / `RemoveCamp` 0x18043D770 | Instantiate EM `campfire` 0xE0, `Campfire.StartFire(pos)` 0x180350D00, later `EndFire` | No |
| Bob's Lantern | `Explode` 0x18043A990 | Instantiate EM `lanternExplosion` 0x1B0 once, then move it and SetActive on each explosion | No |
| Golden Shield | `OnPlayerTakeDamage` 0x18045B720 | `MoneyUtility.SpawnMoney`: gold pickups on the owner's machine. What clients do with these is **UNVERIFIED** | Unknown |

The remaining ~55 items create no objects. They are stat-only, apply debuffs only (debuff state is not
replicated, which is a separate problem), or have empty `Init` / `Tick`.

### Weapons

`WeaponInventory.Tick` runs on the host only. Every `WeaponAttack.SuccessfullySpawnedProjectile` is
broadcast as `SpawnedProjectile` (tags 7, 33–38, 50, 51, 60, 61), except LightningStaff. Constant
attacks (Aura, Aegis, Laser, Dragon Breath, Chunkers) are drawn on every machine. Gaps:

- **LightningStaff:** `ProjectileLightningBolt.TryInit` calls `LightningStrike` (xref), so the strike
  is replicated by tag 41. Any other bolt visual is **UNVERIFIED**.
- **Weapon-rocket explosion:** clients skip `Rocket.ProjectileDone` (0x1804F3FA0) for received
  rockets (`RocketPatches`), and that method spawns the explosion from `explosionPool` 0x1D8. Clients
  likely never see it. **UNVERIFIED.**
- **Per-hit and projectile-done effects:** missing on clients, because `ProjectileBase.HitEnemy` and
  `ProjectileDone` are prefix-false there. **UNVERIFIED:** Ghidra truncated `ProjectileBase.HitEnemy`
  (0x18042BDB0).
- `EffectManager.SpawnRockets` (0x180501AF0) has no direct call site; likely unused.

---

## Chokepoints

### 1. `ItemProjectile` — fixed (tag 85), confirmed in play 2026-10-24 for Soul Harvester and Energy Core

- **Creation:** the only callers of `Set` (0x1804D1CC0) are `ItemEnergyCore.FireOrb`,
  `ItemSoulHarvester.OnEnemyDied`, `ItemSoulHarvester.SpawnProjectile` and
  `EM.SpawnGhostProjectile`. `ItemProjectileOrb` is the only subclass.
- **`Set(pos, damage, procCoefficient, damageSource, pool, projectileIndex, totalProjectiles, duration, range)`:**
  - stores `damage` 0x2C, `damageSource` 0x40, `projectilePool` 0x98, `projectileIndex` 0x48,
    `projectilesCount` 0x4C, `range` 0x50, `procCoefficient` 0x88;
  - `finalProjectileSpeed` 0x34 = `projectileSpeed` × `GetStat(11)`;
  - `upTime` 0x38 = constant / `GetStat(11)`;
  - `spawnedAtTime` 0x30 = `MyTime.time`; `expirationTime` 0x3C = `GetStat(10)` × duration + time;
  - `startDirection` 0x54 and `lastDirection` 0x60 = normalize(up + `Random.insideUnitSphere` × c);
  - then `CheckSpawnCollision`, then virtual `Init`.
  - Speed and lifetime come from **the calling machine's** stats, so a copy must be given the sender's
    computed values. Stat 11 is LIKELY ProjectileSpeed, stat 10 LIKELY Duration.
- **Flight:** `FixedUpdate` (0x1804D14D0) calls `Step` unless paused, then times out to
  `ProjectileDone`. `GetMovementDirection` (0x1804D15D0) homes on `targetEnemy` 0x70, blending from
  `startDirection` over `upTime`. `FindTarget` (0x1804D1140) picks a random enemy within
  `range × c`, or steers toward `MyPlayer.Instance` plus an offset. Homing is random and local, so
  copies diverge cosmetically. `StepAttackMovement` (0x1804D1F30) overlap-checks and sphere-casts,
  calling virtual `CheckCollision`, then moves by `dir × finalProjectileSpeed`.
- **Damage:** only in `CheckCollision` (0x1804D0C90, virtual slot 9). Ground layer, unless
  `ignoreGroundCollision` 0x20 → `ProjectileDone`. Enemy layer → `WeaponUtility.GetDamageContainer` →
  `Enemy.DamageFromPlayerOther` → `ProjectileDone`. **`HitEnemy` (0x1804D1950) is an inlined duplicate
  that `CheckCollision` does not call, so a `HitEnemy` patch would never fire.** `ItemProjectileOrb`
  does not override `CheckCollision`.
- **`ProjectileDone` (0x1804D1B50):** `EM.EnemyHitEffect`, SetActive(false), release to the pool.
  Visual only.
- **Orb:** `Init` (0x180444130) sets `orbitTarget` 0xD0 to `MyPlayer.Instance.transform`. `Step`
  (0x1804447D0) hovers until a time from the index, then `FireOrb` (0x180443D80) fixes
  `movementDirection` 0xF0 toward `EnemyTargeting.GetEnemy`. Overrides `ProjectileDone`, calling base.

**The fix as built** (`Patches/Projectiles/ItemProjectileVisuals.cs`,
`Services/ItemProjectileVisualService.cs`):
- A `Set` postfix queues the computed values and the owner. The kind comes from `damageSource`
  (`SoulHarvester`, `EnergyCore`, `Ghost`/`BobDead`), because these stock-compiled MonoBehaviours
  expose no IL2CPP `Pointer` to compare pools.
- One batch per frame: host to all, or client to host, which draws and relays.
- The receiver marks the instance before `Set`, calls it with zero damage, then overwrites the
  computed fields. An orb copy orbits the owner's NetPlayer model.
- A `CheckCollision` prefix ends a marked copy on any enemy without damage; ground collisions run the
  original. The mark is cleared in `ProjectileDone` and on any real `Set`.

### 2. `Rocket` — Slutty Cannon, open

- `Set` (0x1804F4220) callers: Slutty Cannon, `EM.<DoSpawnRockets>` (0x180511670),
  `ProjectileRocket.TryInit`. Item rockets have `weaponBase == null`, `useGenericPool == true`.
- `CheckCollision` (0x1804F3300) calls `Rocket.HitEnemy` (0x1804F3CB0), which is **not** inlined. On
  the item path it uses `reuseDc` with `damageSource`, calls `Enemy.DamageFromPlayerWeapon`, then
  `ProjectileDone`.
- `ProjectileDone`: `explosionPool` 0x1D8 at pos + forward, release to `rocketPool` 0x1D0, invoke
  `A_ProjectileDone`.
- `RocketPatches` only freeze rockets carrying an `ownerId` DynamicData, so an item-rocket copy without
  one simulates normally.

**Proposed:** postfix on `Rocket.Set` when `weaponBase == null` sends owner, position, forward and
speed. The receiver takes from `rocketPool`, marks it, calls `Set(pos, 0, 0, null, true, source)`
without the `ownerId` data. A `Rocket.HitEnemy` prefix ends a copy without damage.

### 3. `EffectManager` and `EffectPlayer.Play` — open

Visual-only methods used by items:
- `ElectricalPlugEffect` 0x1804FBC80 (parented to MyPlayer)
- `SpawnMirrorFx(pos, dir, size)` 0x180501720
- `ZapEffect(pos)` 0x1805025D0
- `EnemyHitEffect(string)` 0x1804FBE40 → `PoolManager.GetProjectileHit` (0x18040A700); callers are
  Cactus, Electric Plug, Holy Book, Mirror, `PlayerHealth.Retaliate`, `ItemProjectile.ProjectileDone`.
  Visual-only is LIKELY, from a partial read.
- `SpawnGhostProjectile` 0x180501370 (covered by family 1)

`EffectPlayer.Play` (0x180517CA0) callers: the 5 gloves, `ElectricalPlugEffect`, `SpawnMirrorFx`,
`ZapEffect`, `OnDamage`, `ToxicBarrelEffect.OnUse`, `BossLamp`, `ProjectilePoisonFlask`. It covers many
items but needs context to filter out the non-item callers.

**Proposed:** a static "item visual scope" entered and left at the mod's existing decision points
(the `PostDamage` redirect, `RunOnHost`, the owner-only prefixes). Postfixes on the methods above send
only while a scope is open: position, direction, size or prefab key, owner. Receivers replay them;
these bodies only instantiate and play, so the replay is harmless. Covers Zooma, Mirror, Electric
Plug, Cactus and Holy Book sparks, the 5 gloves and Toxic Barrel.

### 4. Pooled one-shot effects — open

Bonker, Ice Cube, Grandma's Tonic, Quin's Mask, Spicy Meatball, Snek, Cursed Doll, Cactus, Plague,
Bullseye, and the Slutty Cannon explosion. The game calls `ObjectPool<GameObject>.Get` directly; that
generic method is shared with the enemy and XP pools. `PoolManager.OnTakeFromPool` (0x18040ABF0) only
does SetActive(true), has no code xrefs and runs before the position is set, so it is not usable.

**Proposed:** a scoped postfix on `ObjectPool<GameObject>.Get` that records the returned object while
an item scope is open, read at scope exit and sent as pool kind, position, rotation, scale and owner.
**Whether Harmony can patch that generic IL2CPP method is UNVERIFIED.** Fallback: about 10 small
per-item hooks.

### Pools (`PoolManager`, dump.cs)

| Pool | Offset | Pool | Offset |
|---|---|---|---|
| `lightningStrikePool` | 0xF0 | `rocketPool` | 0x1D0 |
| `poisonPool` | 0x128 | `explosionPool` | 0x1D8 |
| `grandmaTonicPool` | 0x198 | `bonkPool` | 0x1E0 |
| `spicyMeatballPool` | 0x1B0 | `cursedHitPool` | 0x1F0 |
| `chainLightningPool` | 0x1B8 | `orbPool` | 0x1F8 |
| `freezeFxPool` | 0x1C0 | `ghostPool` | 0x200 |
| `firefieldPool` | 0x1C8 | `angrySoulPool` | 0x208 |
| `borgorPool` | 0x218 | `cactusPool` | 0x230 |
| `quinMaskPool` | 0x248 | `snekPool` | 0x250 |

---

## Shared rules for any further replication

- **Who sends:** the machine that ran the item. Host-run visuals go to all clients, including the
  owner; owner-run visuals on a client go to the host, which draws and relays with
  `SendToAllClientsExcept(sender)`.
- **Owner id:** `trackerService.GetCurrentPlayerId() ?? local`. On the host, the `PositionRequest`
  redirect already resolves MyPlayer-relative positions to the teammate.
- **Wire format:** new messages are appended tags (86 onward); any layout change bumps
  `Protocol.Version`. Delivery per `docs/netplay/02-delivery-method-reference.md`; batch per frame.
- **Copies must never reach `Enemy.Damage`:** on a client the damage forward (`Enemy.Damage_Postfix`
  → `OnEnemyDamaged`) sends any hit whose `damageSource` is an item name to the host, and does not
  check `CAN_SEND_MESSAGES`. A replayed hit would leak.

---

## Not visuals — each needs its own sync

- **Borgor:** a host-only burger pickup that heals `MyPlayer`; only the host can eat them.
- **Dragonfire:** a damaging firefield; a copy would need `Firefield.CheckDamage` suppressed.
- **Golden Shield:** money pickups on the owner's machine.
- **Campfire, Bob's Lantern:** instantiated once, not pooled; per-item hooks.

## Leads, UNVERIFIED (read from source, not logs)

- **Lightning Orb double hit:** item `damageSource` is `EItem.ToString()` (confirmed in the
  `ItemLightningOrb` 0x180460650, `ItemSoulHarvester` 0x1804661E0 and `ItemSluttyCannon` 0x1804649F0
  ctors). Lightning Orb passes `"LightningOrb"` into `WeaponUtility.LightningStrike` (0x180435DA0),
  which calls `Enemy.DamageFromPlayerWeapon`. So the client's replay of tag 41 may forward the host's
  hit back to the host, which applies it again.
- **Weapon-rocket explosions** were never seen on clients once OB-25's fix made `RocketPatches`
  reliably skip `Rocket.ProjectileDone` (2026-10-24 playtest). `ProjectileInterpolator` now draws one
  from `explosionPool` when it removes a received rocket; confirmed in play 2026-10-24.

## Same mechanisms as Rocket, Soul Harvester and Energy Core — audit 2026-10-24

Build 21750826. All 110 `Init` overrides on `ItemBase`, `PassiveAbility` and `WeaponPassive`
subclasses were listed from dump.cs; 62 are the shared empty stub 0x1803321E0. The other 48 were
decompiled and each static delegate they combine into was named from dump.cs's static-field offsets.

### 1. Soul Harvester: a listener that fires after the kill's attribution scope has closed

| Hooked event | Listeners | Verdict |
|---|---|---|
| `Enemy.A_EnemyReleasedFromPool` (0x20) | Soul Harvester, Cursed Doll, Bullseye | Soul Harvester **fixed** (OB-56). Cursed Doll's `OnEnemyDied` (0x18043FBE0) and Bullseye's `OnEnemyReleasedFromPool` (0x1804754E0) only remove the enemy from a set or dictionary: safe on any machine. |
| `Enemy.A_EnemyDied` (0x8) | Borgor, Demonic Blood, Demonic Soul, Plague, Blood Magic | In scope: `Enemy.Damage` (0x1804AC7B0) calls `EnemyDied(dc)` directly (xref). One exception, `DiedNextFrame` (0x1804ACFD0), reached only from `EnemyBehaviour.FixedUpdate` for animation 0x16 enemies that expire on their own: it `Invoke`s the death a frame later, so the host is credited. No player made that kill, so only the host's copies proc. Minor. |
| `Enemy.A_Damage` (0x30) | Demon Blade, Kevin, Joe's Dagger, Zooma, Bullseye | In scope (raised inside `Damage`). |

**Every one of these 48 listeners** was also hit by the stage-change cleanup bug (OB-56, fixed): a
teammate's copy on the host was unsubscribed from stage 2 on.

**New leads, UNVERIFIED in game — a teammate's copy reacting to the local player's own event.**
These passives hook player-side statics and have no patch:

- **Shadowstep** `OnEvade` (0x18047D210), on `PlayerHealth.A_Evaded`: executes the enemy
  (`isExecute`, `DamageFromPlayerOther`). A teammate's copy here fires on *our* evade. On the host that
  executes enemies on the host's evades; on a client the hit is dropped (`Enemy.Damage` is host-only and
  the source is not an item name). The owner's own Shadowstep on a client is dropped the same way.
- **Flex** `OnCheckStopDamage` (0x180477FA0), on `PlayerHealth.A_CheckStopDamage`: calls `UseFlex`,
  which can block the local player's damage, so a teammate's Flex may protect us.
- **Speed Demon** (`A_TakeDamage`), **Lock In** (`AegisAttack.A_Regen`/`A_Used`), **Dice**
  (`ProjectileDice.A_RollSix`): change only that copy's own stat modifiers. Harmless unless a teammate's
  copy feeds the local player's stats.

### 2. Rocket: a `DynamicData` tag on a wrapper nothing keeps alive

`Il2CppObjectPool.Get<T>` reuses a cached wrapper only while it is alive *and* already a `T`; otherwise
it builds a new one and replaces the cache entry. A tag survives only while something holds the exact
wrapper it was set on.

| Tag | Held by | Verdict |
|---|---|---|
| Enemy `netplayId`, `targetId` | `EnemyManagerService.spawnedEnemies` | Safe |
| Pickup `pickupId`, `ownerId` | `PickupManagerService.spawnedPickups` | Safe |
| Host projectile `netplayId`, `ownerId` | `ProjectileManagerService.spawnedProjectile` | Safe |
| Constant attack `ownerId` | `NetPlayer.constantAttacks` (same typed object) | Safe |
| Spawned-object `netplayId` on a `GameObject` | `SpawnedObjectManagerService.spawnedObjects` | Safe while registered |
| Rocket child `ownerId` | nothing | **Fixed** (OB-25) |
| **Client: received weapon projectile `netplayId`** (all non-rocket kinds) | nothing; the interpolator holds the `GameObject`, not the component | **Fixed (OB-58), not yet run in game.** Was exposed: `ProjectileBase.ProjectileDone` then runs on the client: it ends and pools the copy on its own clock while the interpolator keeps moving the pooled object. Same fix as rockets applies. |
| Shady Guy / Microwave `rarity`, tumbleweed component, `InteractableCharacterFight` child `GameObject` | nothing | Exposed; tumbleweed reads it only as a fallback. |

### 3. Rocket explosion: a visual drawn inside a method the client skips

- `ProjectileBase.ProjectileDone` (0x18042C800) only deactivates; `ProjectileBase.HitEnemy` only damages.
  Skipping them on clients loses no visual.
- **Mines** `Explode` (0x1804EE9A0), from `MyFixedUpdate` (0x1804EEF10): draws from `explosionPool`
  (0x1D8), damages, then calls `ProjectileBase.ProjectileDone`. Nothing patches mines, so a client's
  received mine explodes on its own enemy check. With its tag held, `ProjectileDone` is then refused and
  the mine stays, so it can **explode again** each check until the host's `ProjectileDone` arrives.
  **Fixed (OB-58), confirmed in a hands-off run:** a received mine explodes once on the client
  through its own enemy check, then is hidden. Drawing on every removal was wrong: most mines time
  out through `ProjectileBase.CheckTimeout` without exploding.
- **`HitEnemy` overrides** (Bananarang 0x1804E2D80, Bluetooth 0x1804E62A0, Poison Flask 0x1804EFDC0,
  Whirlwind 0x18050D7E0) are not covered by the `ProjectileBase.HitEnemy` patch, so they run on
  clients. Damage is still blocked by the `Enemy.Damage` prefix; host credit is still right through
  `WeaponHitAttribution` (`GetDamageContainer`). Poison Flask's `ExplodeFlask` (0x1804EF5D0) also calls
  `Enemy.AddDebuff` and `WeaponAttack.ProjectileHit` on the client. Its debuff ticks are blocked, but
  what `ProjectileHit` does there is unchecked.

### 4. Energy Core: an owner-run item spawning `ItemProjectile`

All four `ItemProjectile.Set` callers (Soul Harvester x2, Energy Core, `EffectManager.SpawnGhostProjectile`
for Ghost and Bob) go through tag 85. Nothing else creates one. The only other item spawner of a shared
projectile type is **Slutty Cannon** (`Rocket.Set` with `weaponBase == null`), still family 2, open;
the host spawns it at the teammate (OB-43 position redirect in `HitItemOwnership`).
`EffectManager.SpawnRockets` (0x180501AF0) has no callers, only metadata references.

## Weapon projectiles with rocket- or mine-like problems — audit 2026-10-24

Build 21750826. Every `ProjectileBase` subclass (22) was listed from dump.cs and all 114 non-stub
overrides decompiled; each body was scanned for effect spawns (`EffectManager.*`, pool `Get`,
`Instantiate`, `EffectPlayer.Play`), damage, and `ProjectileDone`, then matched against what the mod
lets a client run on a received copy. The two OB-58 shapes were: a visual drawn inside a method the
client skips (`Rocket.ProjectileDone`), and one the client triggers on its own clock and can repeat
(`ProjectileMines.Explode`). A third came with it: setup only `Set`/`TryInit` does, which a client
never runs on a received copy.

**Key chokepoint, CONFIRMED by xref:** `WeaponAttack.ProjectileHit` (0x1804504D0) draws the weapon's
hit effect (`EffectManager.EnemyHitEffect`). Its 18 callers include `ProjectileBase.HitEnemy` and
`HitOther`, and the hit methods of Bananarang, Bluetooth, Cringe and Hero Sword, Dice, Exploding,
Melee, Mines, Poison Flask, Scythe, Shotgun, Sniper and Whirlwind. A client's replayed
`Enemy.Damage` (0x1804AC7B0) draws damage numbers only (`EffectManager.NewDamageNumbers`), never a
hit effect.

### 1. Hit effects never drawn on clients (the rocket shape)

| Weapon class | Effect path | Blocked on clients by |
|---|---|---|
| Every projectile using the base hit (Arrow, Basic, Axe, Homing, ...) | `ProjectileBase.HitEnemy` → `ProjectileHit` | `ProjectileBasePatches.HitEnemy_Prefix` (false on clients) |
| Black Hole | `MyFixedUpdate` → `CheckDamage` (0x1804E4570) → `EnemyHitEffect` | `ProjectileBlackHolePatches.MyFixedUpdate` (false on clients) |
| Cringe Sword, Hero Sword | `MyFixedUpdate` → `CheckColliderCustom` → `ProjectileHit` | their `MyFixedUpdate` patches (false on clients) |
| Dexecutioner | `CheckZone` | `ProjectileDexecutionerPatches.CheckZone` (false on clients) |
| Bananarang | `MyFixedUpdate` → `HitEnemy` → `ProjectileHit`; `LateUpdate` → `TryPopDamage` → `EnemyHitEffect` | `ProjectileBananaPatches.MyFixedUpdate` (false on clients); the pop list stays empty |
| Axe | `LateUpdate` → `TryPopDamage` (0x1804E2080) → `EnemyHitEffect` | runs, but pops `hitEnemies`, which only the blocked `HitEnemy` fills |

So a client sees a received weapon's damage numbers but not its hit sparks. Wall hits
(`ProjectileBase.HitOther`) are not patched and still draw. UNVERIFIED in game: no counter yet.

### 2. Effects a client triggers on its own clock (the mine shape)

- **Poison Flask, closest to the mine bug.** `HitEnemy` (0x1804EFDC0) and `HitOther` call
  `ExplodeFlask` (0x1804EF5D0). It detaches and plays the flask's `effect` (`EffectPlayer`, real offset
  0x80), poisons enemies in `explosionRadius` (`Enemy.AddDebuff`, ticks blocked on clients), calls
  `ProjectileHit`, then `ProjectileDone`, which a client refuses for a received copy. Nothing else
  deactivates the flask, so it can hit again and explode again until the host's `ProjectileDone`
  arrives, and its explosions follow the client's collisions, not the host's.
- **Bluetooth.** `StepMovement` (not patched) calls `HitTarget` (0x1804E6320): `ProjectileHit`, then a
  refused `ProjectileDone`, so it can repeat.
- **One-shot per local collision, so they diverge but do not repeat:** Whirlwind `HitEnemy`, Shotgun
  `CheckZone`, Sniper `Hitscan`, Scythe and Melee `CheckZone`/`SpawnEffect` (their patches only redirect
  positions).

### 3. Setup only `TryInit` does

A client never runs `Set`/`TryInit` on a received copy, except Firefield (the spawn calls
`TryInit(0)`) and Black Hole (`StartScaleSize` is in its message). Attack size feeds visuals in Arrow
(trail width), Poison Flask, Scythe and Sniper (`localScale`), and Katana, Melee and the swords (zone
size). Axe and Shotgun also start particles or sounds there. With size upgrades, clients see default
sizes.

### Fix options, not done

1. **Hit effects:** done 2026-10-25 (OB-60, tag 88 `EnemyHitEffects`, protocol 8). The host sends
   every weapon `EnemyHitEffect` call it draws (15 callers, see OB-60), clients replay exactly those
   and refuse their own, except their own Aegis and laser. Counts matched hands-off.
2. **Poison Flask and Bluetooth:** done 2026-10-25 (OB-59, tag 87 `FlaskExploded`, protocol 7).
   Clients skip the local trigger on received copies and play the host's flask explosions; counts
   matched hands-off.
3. **Size:** apply `GetAttackSizeMultiplier` on the client (it reads that machine's stats, so it is
   approximate), or send the host's size with the spawn (protocol change).

Each can be checked hands-off with host/client counters, as `[explosions]` did for OB-58.

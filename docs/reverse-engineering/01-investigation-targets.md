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

## Current dump

`megabonk-re/build-21750826/` — Steam appid 3405340, buildid **21750826**, Unity 2023.2.22f1.
Line numbers below refer to that `dump.cs`.

**What a dump can and cannot settle.** `dump.cs` lists every type, field, signature and RVA, but
**no method bodies** — those compiled to native code. So it answers *"does this exist, what type
is it, what is the shape"* definitively, and answers *"what does it do"* not at all. Findings
below are tagged accordingly:

| Tag | Basis |
|---|---|
| **CONFIRMED** | Read directly from `dump.cs` — declaration, type, or signature |
| **STRONG** | Inferred from structure (e.g. a `Reuse` method implies pooling). Not proof. |
| **NEEDS GHIDRA** | Requires a method body. RVA/VA given where known. |
| **NEEDS RUNTIME** | Requires watching live values — UnityExplorer or in-game logging |

A field appearing only once in `dump.cs` means only that it is *declared* once. It does **not**
mean nothing uses it — reads and writes live in native code the dump cannot see.

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

### Finding — buildid 21750826, partial

**Q5 — ANSWERED. The patch does catch every subclass.** `dump.cs:372026`:

```csharp
public abstract class BaseSummoner        // not a MonoBehaviour
{
    protected float credits;              // 0x10
    private   float giveCreditsTimer;     // 0x20
    private   float spendCreditsTimer;    // 0x24
    public const int maxEnemiesPerSecond = 500;
    public const int maxEnemiesPerCycle   = 200;

    public          void Tick()      { }  // RVA 0x46D990  VA 0x18046D990  <- NOT virtual
    protected virtual void TickExtra() { } // Slot 4 <- the subclass extension point
}
```

`Tick()` is **non-virtual and declared only on the base**, so subclasses cannot override it —
they extend through `TickExtra()`. `[HarmonyPatch(typeof(BaseSummoner))]` on `Tick` therefore
catches all five subclasses (CONFIRMED): `BossSummoner` (`:372304`), `ChallengeSummoner`
(`:372337`), `SpecialSkeletonSummoner` (`:372380`), `StageSummoner` (`:372419`), `SwarmSummoner`
(`:372468`). No per-subclass patching needed.

**Q4 — partial.** `SummonerController` holds `private List<BaseSummoner> summoners`
(`dump.cs:372136`), so several run concurrently; the live count is NEEDS RUNTIME. Note
`SummonerController` is not a MonoBehaviour either, so something else drives `Tick()` — finding
that caller is the remaining half of Q3.

**Q1–Q3 — NEEDS GHIDRA.** No bodies in the dump. Read `Tick` at **VA 0x18046D990**. Two details
that sharpen the question:

- `giveCreditsTimer` is **private**, and has a sibling `spendCreditsTimer`. The commented-out
  patch reaches a private field through the interop proxy.
- The credit surface is larger than the patch assumes: `AddCredits()`, `GetCreditsPerSecond()`
  (`0x46CCE0`), `GetMultiplier()` (`0x46CD20`), `protected virtual UseMultiplier()`, and
  `CanEarnCredits()` (`0x46C4F0`). **`GetMultiplier()`/`UseMultiplier()` mean the game already
  has its own multiplier concept** — scaling `giveCreditsTimer` from outside may be fighting it
  rather than composing with it. Read `GetMultiplier` before re-enabling anything.

**Consequence unchanged:** the compounding risk is still unresolved, so the patch stays disabled.
But the Q5 concern is closed — whatever is done, one patch site covers all summoners.

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

### Finding — buildid 21750826, partial

`dump.cs:332940` — `public class ChargeShrine : BaseInteractable`. Relevant members:

```csharp
private float chargeTime;              // 0x58
private float currentChargeTime;       // 0x5C
private float chargeProgress;          // 0x60
private float goldChance;              // 0x104
private bool  <isGolden>k__BackingField; // 0x108   <- auto-property
public  Material goldMaterial;         // 0x110
private bool  charging;                // 0x12D

public static Action       A_ChargeShrineSpawned;  // 0x0
public static Action<bool> A_Charged;              // 0x8
public static ChargeShrine lastRewardShrine;       // 0x10
```

**`isGolden` is an auto-property** (CONFIRMED), so every write goes through a setter — a
Harmony patch can intercept it, which the current receive-side assignment does not exploit.

**`goldChance` (0x104) sitting next to it is the load-bearing detail** (STRONG): a per-shrine
roll chance strongly implies `isGolden` is decided **locally at spawn from an RNG draw**. If so,
every client rolls its own value and the host's assignment is racing a local roll — which is
precisely the desync the P1-2 postfix is trying to paper over. Reading `Start` in Ghidra to
confirm the roll happens there would settle Q1 and Q2 together.

**Two statics worth flagging that the original questions did not anticipate:**

- `A_Charged` is a `static Action<bool>` and `A_ChargeShrineSpawned` a `static Action`. Static
  game events are the delegate-leak hazard from the **il2cpp** skill — if the mod subscribes
  without restoring on session end, the handler survives into singleplayer.
- `public static ChargeShrine lastRewardShrine` is **static mutable cross-run state**. It is not
  cleared by anything the mod controls, and a stale reference here is a candidate for the
  dangling-transform bug in [#8](#8-netplayer-lifetime-and-the-dangling-transform--nice).

**Q3, Q4, Q5 — NEEDS GHIDRA.** The method list did not surface `OnTriggerEnter`/`OnTriggerExit`
in the region read; confirm their signatures before relying on the direct calls at
`SynchronizationService.cs:2809` and `:2915`.

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

### Finding — buildid 21750826, largely answered

**Q1 — ANSWERED (CONFIRMED). Effectively everything the mod synchronises is pooled.**
`PoolManager : MonoBehaviour` (`dump.cs:363290`) holds **40+** `ObjectPool<GameObject>` fields
plus five pool dictionaries:

| Pool | Covers |
|---|---|
| `enemyPool`, `enemySpawnFxPool` | **enemies** |
| `xpPool`, `goldPool`, `silverPool`, `powerupPool`, `orbPool` | **pickups** |
| `projectilePools` — `Dictionary<EWeapon, ObjectPool<GameObject>>` | **projectiles, per weapon** |
| `weaponAttackPools` — `Dictionary<EWeapon, ObjectPool<GameObject>>` | **weapon attacks, per weapon** |
| `projectileDonePools`, `projectileHitPools` — `Dictionary<string, …>` | projectile lifecycle fx |
| `enemyAttacksPools`, `enemyAttacksFxPools` | enemy special attacks |
| `rocketPool` (`const rocketPoolSize = 200`), `tumbleweedPool`, `ghostPool`, `firefieldPool`, `chainLightningPool`, `cactusPool`, … | the rest |

**Q2 — ANSWERED (CONFIRMED by type). Deactivated and requeued, never destroyed.** These are
`UnityEngine.Pool.ObjectPool<GameObject>`; `Release` returns the instance to the pool. This is
the same type `src/plugin/Helpers/PoolHelper.cs` already reconstructs reflectively via its
7-argument constructor — so the mod's own code corroborates the finding.

**Q3 — ANSWERED (CONFIRMED). Yes, there are hooks.** That 7-arg `ObjectPool<T>` constructor is
`(createFunc, actionOnGet, actionOnRelease, actionOnDestroy, collectionCheck, defaultCapacity,
maxSize)`. `actionOnGet` and `actionOnRelease` are exactly the reset points a mod needs, and
`PoolHelper` already demonstrates they are reachable.

**Q4 — NEEDS GHIDRA**, but the prior is strong: `PickupManager.DespawnPickup(Pickup)` at
**VA 0x1804DDB60**. Given `Pickup : MonoBehaviour` (`:334856`) and the presence of pickup pools,
requeue is far more likely than destroy.

**Q6 — ANSWERED.** See [#4](#4-damagecontainer--important): `DamageContainer.Reuse()` indicates
pooling there too.

> ### The `NetEntity` design is confirmed broken
>
> Concern **(a)** in "Why it matters" is no longer hypothetical. `Sea-Bass-cmd`'s `NetEntity`
> cleans up in `OnDestroy`. **Pooled objects are not destroyed between uses**, so `OnDestroy`
> fires only at scene teardown — meaning stale `NetId`/`OwnerId` survives every recycle. A
> recycled enemy or pickup carries the previous occupant's owner.
>
> Any replacement for `DynamicData` must clear state in **`actionOnRelease`** (or a patched
> despawn path), never in `OnDestroy`. Reject the cherry-pick as written — see
> [`../netplay/03-cherry-pick-guide.md`](../netplay/03-cherry-pick-guide.md#netentity).

**Q5 — still open (NEEDS RUNTIME).** The dump cannot show which components share a prefab's
GameObject. Two ways in, neither needing UnityExplorer: export the weapon prefabs with
AssetRipper and read the component list, or patch `PoolManager`'s `__result` and log
`GetComponents<Component>()` once. Until Q5 is settled, **assume GameObject-level keying is
unsafe** and keep keying on the component.

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

### Finding — buildid 21750826, mostly answered

`dump.cs:374151`, namespace `Assets.Scripts.Actors`:

```csharp
public class DamageContainer              // plain managed class
{
    public const string unknownDamageSource = "Unkown";   // game's typo, not ours
    public Vector3       direction;             // 0x10
    public float         damage;                // 0x1C
    public bool          crit;                  // 0x20
    public bool          isExecute;             // 0x21
    public float         knockback;             // 0x24
    public Enemy         enemy;                 // 0x28
    public EDamageEffect damageEffect;          // 0x30
    public EElement      element;               // 0x34
    public float         procCoefficient;       // 0x38
    public string        damageSource;          // 0x40
    public int           damageBlockedByArmor;  // 0x48
    public DcFlags       flags;                 // 0x4C
    public bool          canProcJoe;            // 0x50

    public void  .ctor(float procCoefficient, string damageSource) { }  // 0x4A6570
    public void  Reuse(float procCoefficient, string damageSource) { }  // 0x4A64D0
    public void  Copy(DamageContainer dcOther)                     { }  // 0x4A6370
    public Color GetColor()                                        { }  // 0x4A6410
}
```

**Q1 — ANSWERED (CONFIRMED).** A plain managed class. Not a `Component`, not a struct. So
nothing can be `AddComponent`ed to it, and a side table is the only attachment mechanism
available — unless Q3 yields a spare field.

**Q2 — ANSWERED (CONFIRMED).** Exactly **one** constructor,
`.ctor(float procCoefficient, string damageSource)`. Both mod call sites
(`SynchronizationService.cs:1537`, `:1608`) match it; there is no second overload to worry
about. Everything except those two fields is left at default by the ctor — notably `enemy`,
`direction`, `damage`, `flags`.

**Q4 — ANSWERED (STRONG): they are pooled.** `Reuse(float, string)` takes the *exact signature
of the constructor*. That is the shape of an object pool re-initialising a recycled instance,
and `Copy(DamageContainer)` reinforces it.

> **This makes the `WeaponUtility.cs:51` TODO a correctness bug, not a cleanup.** If containers
> are recycled, a side table keyed on managed reference identity will hand a later hit the
> **previous** occupant's owner — gold and kill credit go to the wrong player, intermittently,
> more often under load when the pool churns. The existing "already assigned?" guard in
> `WeaponUtility.GetDamageContainer_Postfix` makes this *worse*, not better: it sees a stale
> entry as a valid one and skips reassignment.
>
> Confirm by reading `Reuse` at **VA 0x1804A64D0** — if it resets fields rather than allocating,
> pooling is proven. Any fix must clear tracking state on **`Reuse`**, not on destruction.

**Q3 — CANDIDATES ONLY, NOT ANSWERED.** `damageBlockedByArmor` (int, 0x48) and `canProcJoe`
(bool, 0x50) each appear exactly once in `dump.cs` — their own declaration. **That is not
evidence they are unused**: reads and writes live in native code the dump cannot see. Both are
plausible carriers for an owner id, and both need Ghidra before anything is written to them.
Hijacking a field the game actually reads would corrupt damage handling.

`flags` is definitively **in use** — `DcFlags` is referenced 14 times and carries a full set:
`None`, `BypassEvade=1`, `BossDamage=2`, `BypassAegis=4`, `FinalBossDamage=8`, `IgnoreArmor=16`,
`BypassAll=5`. Do not repurpose it.

**Q5 — partial.** `damageSource` is a `string`, defaulting to the `"Unkown"` constant. A string
is a poor identity key: no guarantee of uniqueness per attacker, and every read marshals across
the IL2CPP boundary. Not a viable attribution key on its own.

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

### Finding — buildid 21750826, partial

Two vanilla caps surfaced, and **they are not the 400 the docs assume**. On `BaseSummoner`
(`dump.cs:372026`), CONFIRMED:

```csharp
public const int maxEnemiesPerSecond = 500;
public const int maxEnemiesPerCycle  = 200;
```

These are `const` on the summoner, so they are **per-summoner spawn-rate limits**, not a global
live-enemy cap — and `SummonerController` runs several summoners concurrently
(`private List<BaseSummoner> summoners`, `:372136`). That distinction matters: a rate limit and
a population cap tune differently, and `GetMaxEnemiesSpawnable()` may be feeding neither of
these.

`NETPLAY_CHANGES.md` calls 400 "the original cap". **No constant equal to 400 was found on
`BaseSummoner`.** Either the 400 lives elsewhere, or the claim is inherited folklore. Treat it
as unverified until located — and note the existing 500/600 mod caps happen to sit on either
side of the real `maxEnemiesPerSecond = 500`, which may be coincidence or may mean someone was
tuning against the wrong number.

**Q1, Q2, Q3 — NEEDS GHIDRA.** `SpendCredits(bool useWeights)` (`0x46D740`) is where spawning
is actually gated; read it alongside `CanEarnCredits()` (`0x46C4F0`). `IsFinalSwarm()` was not
located on `BaseSummoner` — search `GameManager` for it.

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

### Finding — buildid 21750826, partial

**Q1 — ANSWERED (CONFIRMED).** `public Rigidbody target { get; set; }` — an auto-property
backed by `private Rigidbody <target>k__BackingField` at 0x90 (`dump.cs:374504`). The type is
`Rigidbody`, as suspected. Because it is a property rather than a bare field, the setter is
patchable: a Harmony patch on `set_target` would reveal **every** writer, which answers Q3
directly and is cheaper than reading `InitEnemy` in Ghidra.

**Q5 — fields CONFIRMED to exist:** `private float minStayAtDistance` (0x170) and
`private float basePowerupDropChance` (0x17C). Whether zeroing only these two is sufficient is
NEEDS GHIDRA — the dump cannot show what else reads them.

**Q4 — partial.** `private EEnemyFlag enemyFlag` (0xBC) CONFIRMED. The full `EEnemyFlag` and
`EEnemy` member lists are in the dump and can be transcribed into `GameBalanceService` whenever
the `EnemyManagerService.cs:218` TODO is picked up:

```bash
grep -n -A 40 "enum EEnemyFlag" megabonk-re/build-21750826/dump.cs
grep -n -A 200 "enum EEnemy " megabonk-re/build-21750826/dump.cs
```

**Q2, Q3 — NEEDS GHIDRA / RUNTIME.** Prefer the `set_target` patch above over static analysis.

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

### Finding — buildid 21750826, partial — and Q4 is already a "no"

**Q2 — ANSWERED (CONFIRMED).** Pin the SDK by its interface version strings
(`dump.cs:616835`+), which are more reliable than any package version:

| Constant | Value |
|---|---|
| `STEAMNETWORKINGSOCKETS_INTERFACE_VERSION` | `SteamNetworkingSockets012` |
| `STEAMMATCHMAKING_INTERFACE_VERSION` | `SteamMatchMaking009` |
| `STEAMUSER_INTERFACE_VERSION` | `SteamUser023` |
| `STEAMUTILS_INTERFACE_VERSION` | `SteamUtils010` |

The wrapper is `DummyDll/com.rlabrecque.steamworks.net.dll`. **Match these exact strings when
picking a Steamworks.NET package** — a mismatched interface version fails at runtime, not at
compile time.

> ### Q4 — ANSWERED, and the answer is the bad one: **no, calls do not route exclusively
> through one wrapper.**
>
> There is a **second, obfuscated Steam binding** in the build (`dump.cs:407309`):
>
> ```csharp
> // Namespace:  (none)
> internal class idKIkXgevCvEuIVJPHmMVwnnMSNi
> {
>     public const string YKdRPcKPxQDjLJMwMYLaaaQxtuTT = "steam_api";
>     public const string swItdCSNgSXHZkAEYwtFhNFIfvas = "steam_api64";
>     public const string vyTWANMMjadHrFSNROhCENswsoSvA = "SteamController006";
>     public const string ncQcAbChtRPntupfomHOgrrfAcEab = "SteamUtils009";
> }
> ```
>
> Namespace-less, obfuscated member names, P/Invoking **`steam_api64` directly**, and carrying
> **`SteamUtils009` — a different interface version** from the main wrapper's `SteamUtils010`.
> Two independent Steam access paths coexist in this binary.
>
> `SteamController006` suggests this is a bundled third-party input library (the game uses
> Rewired, which ships Steam Controller support), so it is probably not a stats/achievement
> writer. **But the premise behind Q4 — "one managed wrapper, so patching it is sufficient" —
> is false as of this build.** Confirm what this class actually calls before the migration adds
> a *third* path.
>
> This also qualifies a claim in
> [`00-decompilation-guide.md`](00-decompilation-guide.md#cautions): the game is **partially
> obfuscated**. Game code under `Assets.Scripts.*` has readable names; at least one bundled
> library does not.

**A better suppression mechanism than patching (CONFIRMED).** `SteamAchievementsManager`
(`:361385`) is a **static class** exposing:

```csharp
public static bool ENABLED;                                  // 0x0  <- built-in kill switch
public static void Init()                                    { }    // VA 0x1803EB8C0
public static void TryUnlockAchievement(string achievementKey) { }  // VA 0x1803EBDE0
public static void CheckAchievements()                       { }    // VA 0x1803EB740
```

Setting `SteamAchievementsManager.ENABLED = false` for the duration of a netplay session is
strictly more robust than patching each entry point — it cannot be bypassed by an internal call
path the mod did not think to patch. Verify in Ghidra that `ENABLED` is actually consulted on
the write path (**VA 0x1803EBDE0**) before relying on it.

`SteamStatsManager` (`:361487`) is likewise static, with `public static bool areStatsReady`,
`Init()`, and `RequestStats()` (VA 0x1803EF510).

`Leaderboards` (`:361343`, namespace `Assets.Scripts.Steam`) exposes the ban-risk call directly:
**`public static void UploadScore(int score)` — VA 0x1803E2820.**

**`SteamRichPresenceManager` (`:361420`) is a fourth Steam surface the mod does not patch.** Not
a ban risk, but it will broadcast netplay state to friends as if it were a normal run. Worth a
decision rather than an oversight.

**Q1 — NEEDS GHIDRA.** `SteamAPI.Init()` / `SteamAPI.RunCallbacks()` exist on the wrapper's
`public static class SteamAPI` (`:620882`), but the dump cannot show the game's call sites.
Read `SteamAchievementsManager.Init` (VA 0x1803EB8C0) and `SteamStatsManager.Init`
(VA 0x1803EE690) to find who drives them, and locate the `RunCallbacks` pump before adding a
second one — Multibonk's per-frame pump would double-fire.

**Q3 — NOT ANSWERED; do not read the greps as a yes.** `ISteamNetworkingSockets` (47 hits) and
`SteamMatchmaking` (141 hits) both appear, but **that is the wrapper's complete API surface,
which is present whether or not the game calls any of it.** Presence proves availability, not
use. Determining actual use requires call-site analysis in Ghidra. Until then, assume
virtual-port and lobby collisions are *possible* and pick non-default values.

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

### Finding — buildid 21750826, RESOLVED

**Delete the file. There is nothing to replace.**

Two facts settle it:

1. **The base-game method exists** (CONFIRMED): `public EnemyData GetEnemyData(EEnemy eEnemy)`
   at `dump.cs:336829`, on `DataManager : MonoBehaviour` (`:336765`). It reads the same
   `private Dictionary<EEnemy, EnemyData> enemyData` (0xA8) that the extension reaches into
   directly.
2. **The extension has zero call sites.** The only two references to `GetEnemyDataByName` in
   `src/` are its own declaration and its own log message:

   ```bash
   grep -rn "GetEnemyDataByName" src/    # 2 hits, both inside the extension itself
   ```

The TODO framed this as "find the replacement, then swap" — but nothing calls it, so the swap
is unnecessary. `src/plugin/Extensions/DataManagerExtensions.cs` is dead code.

This also revises the doc's criticism of `Sea-Bass-cmd`: their deletion in `0c7e313` was
**correct**, and not identifying a replacement was fine, because none was needed. The one open
question the extension raised — whether the base method's missing-key behaviour matches the
extension's log-and-return-`null` — is moot with no callers. Should a future caller need
`GetEnemyData`, verify its missing-key behaviour in Ghidra **then**.

**Action:** delete `src/plugin/Extensions/DataManager.cs` and remove the row from the TODO
table below.

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

### Finding — BepInEx 6.0.0-be.785 / Il2CppInterop 1.5.3, answered

Verified against the **exact** BepInEx build installed in the game: source commit `6abdba4`,
matching `LogOutput.log`'s "Built from commit `6abdba47eeebe08552282e7a58ef0f4a9ab60b62`".
That source pins **Il2CppInterop 1.5.3**
(`Runtimes/Unity/BepInEx.Unity.IL2CPP/BepInEx.Unity.IL2CPP.csproj:18-21`).

**Inheritance on injected types IS supported.** BepInEx uses it itself, in
`Runtimes/Unity/BepInEx.Unity.IL2CPP/Utils/Collections/Il2CppManagedEnumerator.cs`:

```csharp
public class Il2CppManagedEnumerator : Object          // derives from Il2CppSystem.Object
{
    static Il2CppManagedEnumerator()                    // self-registers on first use
    {
        ClassInjector.RegisterTypeInIl2Cpp<Il2CppManagedEnumerator>(new RegisterTypeOptions
        {
            Interfaces = new[] { typeof(Il2CppIEnumerator) }
        });
    }

    public Il2CppManagedEnumerator(IntPtr ptr) : base(ptr) { }   // IL2CPP-visible ctor

    public Il2CppManagedEnumerator(IEnumerator enumerator)
        : base(ClassInjector.DerivedConstructorPointer<Il2CppManagedEnumerator>())
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        ClassInjector.DerivedConstructorBody(this);      // <- required, and easy to omit
    }
}
```

Three mechanisms the interpolators are not currently using (all CONFIRMED present in 1.5.3):

| API | Purpose |
|---|---|
| `RegisterTypeInIl2Cpp<T>(new RegisterTypeOptions { … })` | options overload — `Interfaces`, and more |
| `ClassInjector.DerivedConstructorPointer<T>()` | passed to `base(...)` so the native object is allocated for the derived type |
| `ClassInjector.DerivedConstructorBody(this)` | **must** be called at the end of the managed ctor |

A registration placed in a **static constructor** also removes the need for a matching line in
`Plugin.Load()` — the type registers itself the first time it is touched, which eliminates the
"forgot to register, component silently never ticks" failure mode described in the **il2cpp**
skill.

**What this does not prove.** The example derives from `Il2CppSystem.Object` — an *Il2Cpp* base,
not a managed **abstract** base shared by two injected `MonoBehaviour`s, which is what the
interpolators actually want. Il2CppInterop requires a base that is itself either an Il2Cpp type
or an already-injected type; a plain managed abstract class is neither, which is the most likely
cause of the original "not working for some reason". Confirming that requires Il2CppInterop
1.5.3's own `ClassInjector` source (a NuGet package, not vendored in the BepInEx tree).

**Recommendation — prefer composition, and stop blocking on this.** Rather than chase abstract
base support, move the shared interpolation logic into a plain non-`MonoBehaviour` helper that
each interpolator owns and delegates to. Nothing needs injecting, no `DerivedConstructor*`
dance, and the duplication goes away:

```csharp
// not a MonoBehaviour, never registered, no IL2CPP involvement
internal sealed class SnapshotBuffer<T> { /* shared buffer + lerp */ }

public class EnemyInterpolator : MonoBehaviour        // still registered, as today
{
    private readonly SnapshotBuffer<EnemyModel> buffer = new();
}
```

If inheritance is still wanted afterwards, the derived-constructor pattern above is the route —
but the TODO is a code-organisation problem, not an IL2CPP blocker, and composition solves it
today. **Downgrade from an open question to a refactor.**

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

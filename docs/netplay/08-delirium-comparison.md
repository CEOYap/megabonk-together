# `DeliriumPulse/MegaBonk.Multiplayer` — what is worth taking

A second, independent multiplayer mod for the same game, from a different lineage — not a fork of
`Fcornaire/megabonk-together`, so it is a genuine second opinion on the same problems rather than a
variant of our own answers. ([`00-fork-comparison.md`](00-fork-comparison.md) covers the forks that
*are* related.)

Read at commit `969c679`. Nothing here has been built or run; claims about its behaviour are read
off its source.

## Scale and scope, first — because it decides how to read everything else

| | this repo | DeliriumPulse |
|---|---|---|
| C# lines | ~32,100 | ~9,700 |
| Enemy replication | full (snapshots, delta, interpolation, retargeting) | **none** |
| XP / gold / items | shared XP, shared gold, item and weapon sync | **none** |
| Encounters, pause, rewards | the whole barrier ([`07`](07-shared-experience-audit.md)) | **none** |
| Matchmaking / relay | websocket rendezvous + UDP relay server | direct IP, config file |
| Projectiles, pickups, chests | replicated with owner tracking | chest **transform** only |
| Player replication | networked transform + inventory + animation | transform + input + appearance |
| Transport | LiteNetLib, Steamworks planned | `ITransport` + LiteNetLib, Steam **stub** |

**It is not a more advanced implementation, and most of it is not applicable.** What it has is a
different *architecture* for the part it does cover, and four specific techniques that are directly
useful to us. Taking the architecture wholesale is not on the table; taking the techniques is.

---

## The architectural difference in one line

**We send the world. They seed it.**

This repo is replication-first: the host owns the world and broadcasts what happened —
`SpawnedObject`, enemy snapshots, projectile spawns, interactable use. DeliriumPulse is
determinism-first: force every peer's RNG to the same seed at the same points, and the world
generates itself identically with nothing on the wire.

Their `Patch_MapGenSeed` overrides `MapGenerator.GenerateMap`'s seed from a shared `coop_seed` and
calls `UnityEngine.Random.InitState`; `Patch_ProceduralTileJobSeed` reaches into the Burst job's
private `random` field and overwrites it. No map data is transmitted at all.

**The trade is honest and worth stating both ways.** Determinism removes whole classes of bug we
have spent this branch fixing — "object not found in SpawnedObjectManagerService", ownership
tracking, spawn ordering. It replaces them with one much sharper failure: if any peer makes a
different *number* of RNG calls, in a different order, every subsequent draw diverges and there is
no correction mechanism. Their design has no enemies, no items and no shared economy precisely
because those are where call-order divergence would be unavoidable. Ours pays bandwidth and bug
surface to be robust against exactly that.

So: **do not migrate to determinism.** Do consider it for the narrow places where we currently
synchronise a *decision* that both peers could have computed — see item 3 below.

---

## Worth taking

### 1. The prefix/postfix scope stack — and a correction to [P1-11](01-critical-fixes.md#p1-11)

Their `UnityRandomScope` seeds RNG in a Harmony **prefix** and restores it in the **postfix**. The
same shape as our ~48 netplayer-position push/pop pairs, and they solved the part we got wrong:

```csharp
// prefix
internal static void Enter(MethodBase? method, object? instance, …)
{
    var stack = _scopeStack ??= new Stack<ScopeState>();
    if (<decided not to act>)
    {
        stack.Push(new ScopeState { Seeded = false, RestoreState = false });   // ← still pushes
        return;
    }
    …
    stack.Push(new ScopeState { Seeded = true, PreviousState = previous, … });
}

// postfix
internal static void Exit()
{
    var state = stack.Pop();          // ← no condition. Pops what the prefix pushed.
    if (!state.Seeded) return;
    …
}
```

Two rules we do not follow and should:

- **The prefix pushes a record even when it decides to do nothing**, so the stack stays balanced
  whatever the prefix chose.
- **The postfix carries no condition at all.** It pops.

Our P1-11 defect is exactly the inverse: the postfix re-derives its condition (`targetId.HasValue`,
`GetNetPlayerByWeapon`, `HasNetplaySessionStarted()`) and skips the pop when the answer changed
mid-call. The frame-stamped purge landed on this branch bounds the damage; **this is the design
that removes the cause**, and it needs no wire change.

**And a correction to what P1-11 says.** That entry states `try/finally` is "not available" for a
prefix/postfix pair. That is wrong: Harmony (HarmonyX, which BepInEx 6 ships) has
**`[HarmonyFinalizer]`**, which runs after the original *even when it throws* — the exception-safe
completion of exactly this pattern. Neither codebase uses it. A `Finalizer` that pops the scope is
what makes the balanced stack correct under exceptions too, and it is the right shape for our
push/pop pairs when we redo them.

### 2. `ITransport` + a factory — and one IL2CPP trap they already hit

Relevant to the Steamworks migration ([`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md)):

```csharp
public interface ITransport
{
    bool IsServer { get; }
    int ConnectedCount { get; }
    event Action<ulong> PeerConnected;
    event Action<ulong> PeerDisconnected;
    event Action<ulong, ArraySegment<byte>, bool> DataReceived;
    void StartHost(int port, string key);
    void StartClient(string hostAddress, int port, string key, ulong hostSteamId);
    void Poll();
    void SendToAll(byte[] data, bool reliable);
    void SendTo(ulong peerId, byte[] data, bool reliable);
}
```

Two things to note beyond the shape itself:

- **A `ulong` peer id in the interface**, which is a LiteNetLib peer id today and a `CSteamID`
  later. Our Phase 1 problem is the opposite — two id spaces that `SendToAllClientsExcept` has to
  reconcile (the `RelayEnvelope.ToFilters` item). One id type in the transport contract is the
  cleaner starting point.
- **`// IMPORTANT: IL2CPP cannot marshal interface returns; use concrete property.`** — from
  `LiteNetTransportRunner`. They hit it and worked around it by exposing the concrete transport
  rather than the interface from an IL2CPP-visible surface. Worth knowing *before* we design the
  same abstraction, because it constrains where the interface can appear (injected MonoBehaviours
  and anything the runtime marshals) versus where it is fine (plain managed services, which is most
  of ours).

Their Steam transport is a stub — every method empty. There is nothing to learn from it about
Steamworks itself.

### 3. Their RNG allowlist is a free map of where randomness enters Megabonk

Independent of whether we adopt determinism, `Patch_DumpAndForceJobRNGs` enumerates the game
methods that consume randomness, by full type name. Ones that matter to us directly:

| Type | Methods |
|---|---|
| `Inventory__Items__Pickups.Rarity` | `GetItemRarity`, `GetEncounterOfferRarity`, `GetShadyGuyRarity` |
| `Upgrades.EncounterUtility` | `GetRandomStatOffers`, `GetRandomStatsBalanceShrine`, `GetBalanceShrineOffers` |
| `Weapons.EnemyTargeting` | `GetClosestEnemy`, `GetEnemiesInRadius`, `GetRandomEnemy`, `GetSmartEnemy` |
| `GoldAndMoney.MoneyUtility` | `SpawnSilver`, `SpawnSilverNoTimerImpact` |
| `Weapons.Attacks.WeaponAttack` | `SpawnProjectile` |
| `Weapons.Projectiles.ProjectileBase` | `CheckSpawnCollision`, `HitOther`, `StepMovement` |

This is a **hypothesis list, not verified** — they may be patching methods that never draw. But it
is a much better starting point than grepping, and two entries are immediately interesting:

- **`Rarity` / `EncounterUtility`** are where a chest's and a shrine's *offers* come from. Those are
  decisions we currently do not synchronise at all — every peer rolls its own. Seeding just those
  would make offers identical without a single message, which is a narrow, testable use of
  determinism in exactly the area with the worst bug history ([#76, #81, #93](07-shared-experience-audit.md)).
- **`EnemyTargeting.GetRandomEnemy`** is the shape of our own `ReTargetEnemies`, which uses
  `Random.Range` and is the reason clients must not retarget locally
  ([P1-8](01-critical-fixes.md#p1-8)). If that draw were deterministic, the constraint changes.

### 4. Reflection-based patch targeting — a real trade-off, not a clear win

They target patches by string:

```csharp
var t = AccessTools.TypeByName("MapGenerator");
var m = AccessTools.Method(t, "GenerateMap", new[] { AccessTools.TypeByName("MapData"), … });
```

and take `object __instance`, reading fields through reflection. We compile against the interop
assemblies and use typed patches.

- **In their favour:** a game update that renames nothing but shifts assemblies does not break the
  build, and there is no dependency on a local `MegabonkPath` — which is the trap `CLAUDE.md` opens
  with (a stale `stripped-libs` fallback that fails silently).
- **Against:** no compile-time check at all. A renamed method becomes a runtime `LogError` and a
  silently missing patch — the exact "patch that does nothing" failure the `harmony` skill warns
  about, and their code is full of the defensive `if (m == null) LogError(...)` this forces.

**Not a recommendation to switch.** It is worth borrowing for the narrow case where we patch
something the interop assemblies do not expose cleanly, and worth knowing as the fallback if a game
update ever breaks our typed patches wholesale.

---

## Not worth taking

- **`PlayerPrefs` as the seed channel.** `Patch_MapGenSeed` reads `coop_seed` from `PlayerPrefs` —
  process-global mutable state that persists to disk and outlives the session. A stale key from a
  crashed run silently seeds the next one. We already have exactly this bug class (P0-5, SE-2).
- **`BinaryWriter`/`BinaryReader` hand-rolled framing.** Their `NetMsgUtil` writes a one-byte type
  and then raw fields. Our MemoryPack union contract is stricter, versioned by tag, and already
  documented; going back to hand framing loses the append-only tag discipline that
  `CLAUDE.md` is built around.
- **20 Hz fixed transform streaming with no distance throttling.** We already do better
  ([`04-performance-and-gc.md`](04-performance-and-gc.md)).
- **Chest sync keyed by RNG call index.** `Patch_InteractableChestSync` identifies a chest by the
  *ordinal of the RNG call that created it* and syncs only rotation and scale. It is ingenious and
  it is exactly as fragile as it sounds — one extra or missing draw on either side and the indices
  address different chests. Our netplay-id mapping is the right answer.

---

## Suggested order, if any of this is picked up

1. **The scope-stack discipline + `[HarmonyFinalizer]`** for the push/pop pairs (item 1). No wire
   change, removes the *cause* of P1-11 rather than bounding it, and corrects a wrong statement in
   our own docs. Do it when P1-11 is revisited, not before — the frame purge holds until then.
2. **The `ITransport` shape and the IL2CPP interface-return caveat** (item 2) — as input to
   Steamworks Phase 1, which is already scheduled.
3. **Seeded reward offers** (item 3), *only* after the shared-experience fix order in
   [`07`](07-shared-experience-audit.md) reaches step 4. It is a narrow, high-value use of
   determinism, but it changes what each player sees in a chest, and dropping it into the middle of
   the barrier work would make that playtest unreadable.

Nothing here is urgent. The two items with real leverage are the scope stack and the RNG map.

# Related Implementations — what exists, what to take, what to reject

Every other multiplayer implementation for Megabonk, in one place: the two forks related to ours,
and the three independent mods that are not. What each is, what it does well, and what — if anything —
belongs here.

Merged from the former `00-fork-comparison.md` and `08-delirium-comparison.md` and re-audited against
the repo. **Corrections that audit produced are listed at the end** — four claims in the old files
were stale or wrong, including one that pointed at a defect since eliminated.

Nothing about the other projects has been built or run; claims about their behaviour are read off
their source. Claims about *this* repo are checked against the working tree.

> **On §5.** One of these is closed-source and binary-only, with no licence granting reuse. It is
> referred to as **Mod S** and is not named, quoted or reproduced anywhere in this repository —
> only facts about its protocol and its Steamworks API surface are recorded, which is the same
> standard applied to every other project here: read for technique, port no code.

---

## The landscape

| | this fork | Fcornaire (upstream) | Sea-Bass | Multibonk | DeliriumPulse | Mod S |
|---|---|---|---|---|---|---|
| Relationship | — | parent | fork of upstream | unrelated | unrelated | unrelated, closed-source |
| Loader | BepInEx IL2CPP | BepInEx IL2CPP | BepInEx IL2CPP | MelonLoader | BepInEx IL2CPP | BepInEx IL2CPP |
| Transport | LiteNetLib + NAT punch + relay | same | same | `ISteamNetworkingSockets` | `ITransport` → LiteNetLib; Steam **stub** | **`ISteamNetworkingSockets`, shipped** |
| Discovery | websocket rendezvous | same | same | Steam lobbies | direct IP, config file | Steam lobbies |
| C# lines | ~34,000 | ~32,000 | ~32,000 | ~1,250 netcode | ~9,700 | ~2,900 methods |
| Enemy replication | full | full | full | none | **none** | full |
| XP / gold / items | full | full | full | none | **none** | XP, items |
| Encounters / rewards | full barrier | full barrier | full barrier | none | **none** | pause sync |
| Player replication | yes | yes | yes | yes | yes | yes, + mid-run join |

Three of these are complete netplay implementations and two are not. Read the small ones for
technique, never for scope.

---

## 1. `Fcornaire/megabonk-together` — upstream, the trunk

The parent project and the only other complete netplay implementation.

- 113 Harmony patch files; `SynchronizationService.cs` alone is **4,820 lines**
- Synced: enemies, projectiles, pickups, chests, shrines/pylons/lamps, damage attribution, XP, gold,
  inventory (weapons/tomes/items/hats), encounters, tumbleweeds, storms, swarms, final boss orbs,
  map generation

**Relationship:** this fork is **90 commits ahead** of the shared merge base `50b30a4`. Two of those
are Fcornaire's and predate our own work:

- `bd9518c` "more code optimizations" — genuinely good. `string[]` → `HashSet<string>` for
  `AllowedDamageSource` (O(n) → O(1) per damage event), and
  `Select().ToList().ToDictionary()` → pre-sized `Dictionary` + `foreach` in
  `GetAllEnemiesDeltaAndUpdate` (per-tick, per-enemy hot path). Also adds `DistanceThrottler`, which
  this repo still uses.
- `041881b` "added some logs to identify a chest open issue" — diagnostic logging around the
  chest-open path, a known-fragile area.

Also in the merge base: `24f5004` *"update packet delivery methods to improve performance and prevent
desync issues"*. Upstream deliberately tuned the reliability map. **Treat it as intentional; do not
overwrite it wholesale.**

**Verdict:** the trunk. Stay aligned, upstream general fixes back.

---

## 2. `Sea-Bass-cmd/optimized-netplay` — the optimization fork

12 commits off `50b30a4`, 43 files, +565/−489. Head `8628e71`.

Advertised as netcode/GC/performance work. In practice: a script-driven replacement of MonoMod
`DynamicData` with a custom `NetEntity` MonoBehaviour across ~40 files, a blanket downgrade of 17
event RPCs to `Unreliable`, and a handful of real logic fixes buried in the same commits.

**What it gets right**

| Contribution | Value |
|---|---|
| Shrine/pylon/lamp charging: check-before-write ordering | High — a real claim-clobbering bug. **We have since fixed this area independently** (P2-3 dedup, plus the charge-start replay fix in `7c8cdb2`), so this is now historical rather than actionable |
| Charging stop paths: `KeyNotFoundException` guards | High — a live crash. Also since fixed here |
| Deduplicating the triplicated charging logic | Medium — resolves upstream's own TODO; done here as P2-3 |
| Host relay for XP / gold / encounter-close | Medium — closes a real propagation hole, but echoes to the sender |
| Legendary (golden) shrine flag sync | Medium — real gap, but breaks the wire format with no version gate |
| Null guard on `GetNetPlayerByNetplayId` | Medium — prevents a live NRE |
| `LogWarning` on the dangling-transform hack | Low — useful; needs rate limiting, which this repo added |

**What it gets wrong**

| Problem | Impact |
|---|---|
| 17 non-idempotent event RPCs → `Unreliable` | **Critical** — permanent desync on any loss; reverts upstream `24f5004` |
| Host relay echoes `GoldChanged` to its own sender | **Critical** — gold duplication exploit (`ChangeGold` is a delta) |
| `NetEntity` keyed on GameObject, cleaned only in `OnDestroy` | High — broken under pooling (**confirmed**: `DespawnPickup` calls `ObjectPool.Release`, never `Destroy`); stale ownership on recycled objects |
| `Object.Destroy(netEnt)` before `DespawnPickup` | High — deferred clear + `AddComponent`/`Destroy` churn per pickup cycle |
| Final swarm enemy cap 400 → 700/800 | High — the game *lowers* the cap to 400 then 300 during the final swarm; vanilla baseline is 550 |
| `BaseSummoner` patch re-enabled verbatim | High — disabled for measured FPS reasons; compounds credit income to ~2–3× |
| Claimed `Interlocked` concurrency fix never applied | Medium — only the `//TODO` markers were deleted |
| `__instance.target` assignment silently dropped in `Enemy.cs` | Medium — 2–6 s host-aggro bias on every spawn |
| `Specific.IsGoldenShrine` with no version gate | Medium — silent MemoryPack wire break |

**Verdict: selective hand-porting only.** Every commit with something worth having also carries
something that must not ship. See [`03-cherry-pick-guide.md`](03-cherry-pick-guide.md).

Two mechanical blockers if you try anyway:

- `45ce3f5` adds an 11.5 MB `dump.cs` that `8628e71` removes. Cherry-picking `45ce3f5` alone
  permanently adds 11.5 MB to history.
- 5 files conflict with `bd9518c`/`041881b`: `Patches/Enemies/Enemy.cs`,
  `Patches/Unity/UnityComponent.cs`, `Plugin.cs`, `Services/EnemyManagerService.cs`,
  `Services/SynchronizationService.cs`. Fcornaire's versions are better; do not let a cherry-pick
  overwrite them.

---

## 3. `Vanlichtinstein1945/Multibonk` — the Steamworks reference

A separate, much smaller mod. Self-described as unstable. MelonLoader, so **the code is not
portable** — read it for API surface only.

1,252 lines of networking total. Synced: player position, rotation, animation bits. That is the
entire netcode.

**What to take — its API choices, which are correct:** `SteamNetworkingSockets.CreateListenSocketP2P`
/ `ConnectP2P`, `SteamNetConnectionStatusChangedCallback_t` for connection lifecycle,
`SteamMatchmaking` lobbies for discovery, `GameLobbyJoinRequested_t` for friends-list joins,
`SteamFriends.SetRichPresence` for presence.

**What not to take —** its architecture (everything `static` on one class), and specifically its
send path:

```csharp
SteamNetworkingSockets.SendMessageToConnection(conn, ptr, (uint)len, 0, out long _);
//                                                                   ^ nSendFlags = 0
//                                              = k_nSteamNetworkingSend_Unreliable
```

All three send helpers are `SendUnreliable`, `BroadcastUnreliable`, `SendToHostUnreliable`. That is
**correct for Multibonk** — position snapshots are continuous state where the next packet supersedes
a lost one. It is **not** a precedent for one-shot event RPCs, and it is the same shape of mistake as
the Sea-Bass downgrade. Multibonk gets away with it because it never sends a non-idempotent event.

It also never calls `SteamNetworkingUtils.InitRelayNetworkAccess()`, which costs a multi-second stall
on the first P2P connection while the SDR ticket is fetched. **Do call it.**

**Verdict: reference, not source.**

---

## 4. `DeliriumPulse/MegaBonk.Multiplayer` — the determinism reference

Read at commit `969c679`. A different lineage, so a genuine second opinion rather than a variant of
our own answers. ~9,700 lines; no enemies, no XP/gold/items, no encounter barrier; chest **transform**
only; direct-IP config-file discovery.

**It is not a more advanced implementation, and most of it is not applicable.** What it has is a
different architecture for the part it does cover, and four techniques worth taking.

### 4.1 The prefix/postfix scope stack — and a correction to [P1-11](01-critical-fixes.md#p1-11)

Their `UnityRandomScope` seeds RNG in a Harmony **prefix** and restores it in the **postfix** — the
same shape as our ~48 netplayer-position push/pop pairs, and they solved the part we got wrong:

```csharp
// prefix
if (<decided not to act>)
{
    stack.Push(new ScopeState { Seeded = false, RestoreState = false });   // ← still pushes
    return;
}
stack.Push(new ScopeState { Seeded = true, PreviousState = previous, … });

// postfix
var state = stack.Pop();          // ← no condition. Pops what the prefix pushed.
if (!state.Seeded) return;
```

Two rules we do not follow and should:

- **The prefix pushes a record even when it decides to do nothing**, so the stack stays balanced
  whatever the prefix chose.
- **The postfix carries no condition at all.** It pops.

Our P1-11 defect is the inverse: the postfix re-derives its condition and skips the pop when the
answer changed mid-call.

> **This is not theoretical — a live instance was found and fixed in this repo.**
> `RocketPatches.MyFixedUpdate_Prefix` called `AddGetNetplayerPositionRequest` on **every** peer
> while the postfix popped **only on a host**, stranding one request per remote rocket per frame on
> clients. Fixed in `91d4673` by making both sides host-only. Nothing had reported it; it was found
> while reading the file for an unrelated bug. Assume there are others.

**And a correction to what P1-11 says.** That entry states `try/finally` is "not available" for a
prefix/postfix pair. That is **wrong**: HarmonyX (which BepInEx 6 ships) has **`[HarmonyFinalizer]`**,
which runs after the original *even when it throws*. **Still unused in this repo — 0 occurrences.**
A finalizer that pops the scope is what makes the balanced stack correct under exceptions, and it is
the right shape when the push/pop pairs are redone.

### 4.2 `ITransport` + a factory — and one IL2CPP trap they already hit

Relevant to [`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md):

```csharp
public interface ITransport
{
    bool IsServer { get; }
    event Action<ulong> PeerConnected;
    event Action<ulong, ArraySegment<byte>, bool> DataReceived;
    void StartHost(int port, string key);
    void StartClient(string hostAddress, int port, string key, ulong hostSteamId);
    void Poll();
    void SendToAll(byte[] data, bool reliable);
    void SendTo(ulong peerId, byte[] data, bool reliable);
}
```

- **One `ulong` peer id in the contract** — a LiteNetLib peer id today, a `CSteamID` later. Our Phase
  1 problem is the opposite: two id spaces that `SendToAllClientsExcept` must reconcile (the
  `RelayEnvelope.ToFilters` item). One id type is the cleaner starting point.
- **`// IMPORTANT: IL2CPP cannot marshal interface returns; use concrete property.`** — from their
  `LiteNetTransportRunner`. Worth knowing *before* designing the same abstraction: it constrains
  where the interface may appear (injected MonoBehaviours and anything the runtime marshals) versus
  where it is fine (plain managed services, which is most of ours).

Their Steam transport is a stub — every method empty. Nothing to learn from it about Steamworks.

### 4.3 Their RNG allowlist is a free map of where randomness enters Megabonk

Independent of adopting determinism, `Patch_DumpAndForceJobRNGs` enumerates the game methods that
consume randomness, by full type name:

| Type | Methods |
|---|---|
| `Inventory__Items__Pickups.Rarity` | `GetItemRarity`, `GetEncounterOfferRarity`, `GetShadyGuyRarity` |
| `Upgrades.EncounterUtility` | `GetRandomStatOffers`, `GetRandomStatsBalanceShrine`, `GetBalanceShrineOffers` |
| `Weapons.EnemyTargeting` | `GetClosestEnemy`, `GetEnemiesInRadius`, `GetRandomEnemy`, `GetSmartEnemy` |
| `GoldAndMoney.MoneyUtility` | `SpawnSilver`, `SpawnSilverNoTimerImpact` |
| `Weapons.Attacks.WeaponAttack` | `SpawnProjectile` |
| `Weapons.Projectiles.ProjectileBase` | `CheckSpawnCollision`, `HitOther`, `StepMovement` |

A **hypothesis list, not verified** — they may patch methods that never draw. Two entries matter:

- **`Rarity` / `EncounterUtility`** are where a chest's and a shrine's *offers* come from — decisions
  we do not synchronise at all today; every peer rolls its own. Seeding just those would make offers
  identical with no message, in exactly the area with the worst bug history
  ([#76, #81, #93](07-shared-experience-audit.md)).
- **`EnemyTargeting.GetRandomEnemy`** is the shape of our `ReTargetEnemies`, which uses `Random.Range`
  and is why clients must not retarget locally ([P1-8](01-critical-fixes.md#p1-8)). If that draw were
  deterministic, the constraint changes.

### 4.4 Reflection-based patch targeting — a trade-off, not a win

They target by string (`AccessTools.TypeByName`, `object __instance`, fields via reflection); we
compile against interop assemblies with typed patches.

- **For:** a game update that shifts assemblies without renaming does not break the build, and there
  is no dependency on a local `MegabonkPath` — the trap `CLAUDE.md` opens with.
- **Against:** no compile-time check at all. A renamed method becomes a runtime `LogError` and a
  silently missing patch — the "patch that does nothing" failure the `harmony` skill warns about.

**Not a recommendation to switch.** Worth borrowing where interop does not expose something cleanly,
and worth knowing as the fallback if an update breaks typed patches wholesale.

---

## 5. Mod S — the shipping Steamworks implementation

A closed-source P2P mod for this game, BepInEx 6 IL2CPP + Steamworks.NET, distributed as a
binary with a Discord for support. **Deliberately not named here**, and its code is not quoted
or reproduced: there is no licence file and no licence field in its manifest, which means all
rights reserved. Findings below are from assembly metadata and protocol shape only.

**Why it matters more than the other three: it is the only project that has already shipped the
architecture [`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md)
proposes.** Steam P2P sockets, Steam lobbies for discovery, no rendezvous server, no NAT-punch
path, no relay of our own. It is proof the target works on this game and this loader, which is
worth more than any individual technique in it.

459 types, ~2,900 methods, 65 network messages across `Client` / `Server` / `Shared`.

### What is worth knowing

- **Poll groups.** `CreatePollGroup` / `SetConnectionPollGroup` / `ReceiveMessagesOnPollGroup`
  — the host drains every peer in one call. Folded into the migration plan's gotcha 6.
- **Relay *and* authentication readiness are both gated.** They poll
  `GetRelayNetworkStatus` and `GetAuthenticationStatus` before connecting, not just call
  `InitRelayNetworkAccess`. Folded into gotchas 2 and 6a.
- **Auth session tickets** (`GetAuthSessionTicket` / `CancelAuthTicket`) — identity proved
  rather than asserted, which is the structural answer to internet-play fault 4.
- **Their reliability split independently matches our policy**: 55 of 65 messages reliable, and
  the 9 unreliable ones are exactly the superseded-continuous-state set. See
  [`../steamworks/01-api-mapping.md`](../steamworks/01-api-mapping.md) for the table, and for
  the one place they disagree with our recommendation (`NoNagle` on events plus explicit batch
  messages, rather than letting Nagle coalesce).
- **Explicit batching** — `ReliableBatchMessage`, `EnemyStateBatchMessage`,
  `EnemyRetargetBatchMessage`, `SpawnedObjectBatchMessage`. Our `LobbyUpdates` is one mega-message
  at 65–90% of host egress; theirs is batched per category. The clearest available lead for the
  bandwidth work.
- **Clock synchronisation** — `TimeSyncRequest`/`TimeSyncResponse` + a keep-alive, feeding a
  network-time system. **We have none.** We sidestep needing one by stamping interpolation
  snapshots on *receipt*, which is correct but lets arrival jitter into the interpolation
  timeline. Not a bug; a real improvement if interpolation quality is ever revisited.
- **A dedicated `BossPortalUnlockedMessage`**, host-broadcast and guarded by an id set on both
  sides. Independent corroboration of the double-`OnBossDefeated` defect found and fixed in this
  repo — and confirmation that the fix shape is a guard plus host authority, which is what this
  repo's own charging code already does.

### What their handshake does and does not tell us

Their join sequence is eight messages (`ClientHello` → `ServerHello` → `HostWelcome` →
`ClientIntroduce` → `ClientPrefabsReady` → `PlayerReadyForSpawn` → `ServerReadyForSpawnSync` →
`AllPlayersReadyForSpawn`) against our single `ClientInGameReady`.

**It contains no retry, timeout or resend logic at all** — checked directly. So it is *not*
evidence that adding retries fixes our lobby-ready defects, and I am recording that because it
is the opposite of what I expected to find.

What it does suggest is structural: readiness there is a **protocol phase** with its own
messages, whereas ours is a mutable `IsReady` flag living on the replicated `Player` record —
which is exactly why `ResetForNextLevel` and `OnLobbyUpdate` can clobber it (defects B and C in
[`12-session-handover.md`](12-session-handover.md)). The lesson is about where readiness lives,
not about retrying.

### Verdict

**Reference, and the most relevant one we have — but read for architecture, not for code.**
Binary-only and all rights reserved: port nothing, quote nothing, and keep it anonymous in this
repo. Everything it contributed above is a fact about a protocol or an API, not an
implementation.

---

## The architectural axis: we send the world, they seed it

This repo and upstream are **replication-first**: the host owns the world and broadcasts what
happened. DeliriumPulse is **determinism-first**: force every peer's RNG to the same seed at the same
points and the world generates itself, with nothing on the wire. `Patch_MapGenSeed` overrides
`MapGenerator.GenerateMap`'s seed from a shared `coop_seed`; `Patch_ProceduralTileJobSeed` overwrites
the Burst job's private `random` field. No map data is transmitted at all.

**The trade is honest both ways.** Determinism removes whole classes of bug we have spent this branch
fixing — "object not found in SpawnedObjectManagerService", ownership tracking, spawn ordering. It
replaces them with one much sharper failure: if any peer makes a different *number* of RNG calls, in
a different order, every subsequent draw diverges with no correction mechanism. Their design has no
enemies, items or shared economy precisely because that is where call-order divergence would be
unavoidable.

> **A concrete cost of our side of that trade, found this session.** Because clients *instantiate and
> then reposition* while the host builds in place, every client-spawned object ran its `Awake` at the
> prefab's authored coordinates — `Awake` runs synchronously inside `Instantiate`. Anything caching a
> transform there cached prefab coordinates, identically for every clone. It surfaced as an
> invisible charge-shrine model and was a defect in the spawn path affecting every object a client
> spawns. Fixed in `1e62b31`; full account in
> [`12-session-handover.md`](12-session-handover.md).
>
> **A determinism-first design cannot have this bug**, because nothing is instantiated-then-moved —
> objects are generated in place on both peers. That is the clearest single illustration of what each
> architecture buys and costs, and neither of the source documents contained it.

**So: do not migrate to determinism.** Do consider it for the narrow places where we synchronise a
*decision* both peers could have computed — §4.3.

---

## Decision matrix

| Source | Action |
|---|---|
| `Fcornaire/megabonk-together` | Track. Rebase on it. Upstream general fixes back. |
| `Sea-Bass-cmd/optimized-netplay` | Hand-port only. Its charging fixes are now redundant — we fixed that area ourselves. Reject the rest. |
| `Vanlichtinstein1945/Multibonk` | Read for Steamworks API patterns. Port no code. |
| Mod S (closed-source) | The only shipped example of our target architecture. Read the protocol shape; port nothing, quote nothing, keep it unnamed. |
| `DeliriumPulse/MegaBonk.Multiplayer` | Read for the scope-stack discipline and the RNG map. Port no code. |

## What to take, ranked

1. **The scope-stack discipline + `[HarmonyFinalizer]`** (§4.1). No wire change, removes the *cause*
   of P1-11 rather than bounding it, corrects a wrong statement in our own docs, and there is now a
   confirmed live instance of the defect it prevents. Do it when P1-11 is revisited — the
   frame-stamped purge holds until then.
2. **The `ITransport` shape and the IL2CPP interface-return caveat** (§4.2) — input to Steamworks
   Phase 1, already scheduled.
3. **Multibonk's Steamworks API choices**, including `InitRelayNetworkAccess()` (§3) — same phase.
4. **Seeded reward offers** (§4.3), *only* after the shared-experience fix order in
   [`07`](07-shared-experience-audit.md) reaches step 4. Narrow, high-value, but it changes what each
   player sees in a chest and would make the barrier playtest unreadable if landed mid-stream.

## What to reject outright

- **Blanket `Unreliable` downgrades** (Sea-Bass, and Multibonk's shape misread as precedent).
- **`PlayerPrefs` as a seed channel** (DeliriumPulse) — process-global mutable state persisted to
  disk that outlives the session. A stale key from a crashed run silently seeds the next one. We
  already have this bug class (P0-5, SE-2).
- **Hand-rolled `BinaryWriter` framing** (DeliriumPulse) — loses the append-only MemoryPack tag
  discipline `CLAUDE.md` is built around.
- **20 Hz fixed transform streaming with no distance throttling** (DeliriumPulse) — we already do
  better ([`04-performance-and-gc.md`](04-performance-and-gc.md)).
- **Chest sync keyed by RNG call index** (DeliriumPulse) — identifies a chest by the ordinal of the
  RNG call that created it. Ingenious and exactly as fragile as it sounds. Our netplay-id mapping is
  the right answer.
- **`NetEntity` keyed on GameObject with `OnDestroy` cleanup** (Sea-Bass) — broken under pooling.

---

## Corrections this re-audit produced

The old files carried four claims that no longer hold. Kept visible rather than silently edited,
because someone will propose them again.

| Old claim | Corrected |
|---|---|
| "we are **2 commits ahead** of `50b30a4`" | **90 commits ahead.** The figure was written before any of this fork's own work existed |
| "`SynchronizationService.cs` alone is **4,373 lines**"; repo "~32,100 lines" | **4,820** and **~34,000**. Directionally the same point — that file is still the centre of gravity — but the numbers were stale |
| "Decompilation … surfaced a new High-severity defect (**P1-5**) that no fork had identified" | **Overturned in-game.** Every proposed mechanism for P1-5 was eliminated; the `DamageContainer` path was write-only dead code and was deleted. `01-critical-fixes.md` now records the symptom as unattributed and unreproduced. The decompilation work was still worth it — the enemy-cap and `giveCreditsTimer` findings stand — but it did not find this |
| "**Add a protocol version gate**" ranked #2 in "where the real work is" | **Attempted and disproved in-game.** The LiteNetLib connect-key gate cannot fire: `ConnectionRequestEvent` never runs on the NAT-punch path because both peers `Connect` at each other and LiteNetLib reconciles the cross-connect internally. Reverted; deferred to Steamworks Phase 3. It remains true that a mismatched build silently corrupts a session — the *fix* was wrong, not the problem |

One item that **still stands** and is worth restating: `[HarmonyFinalizer]` is documented in
`01-critical-fixes.md` as the correction to P1-11's "no `try/finally` available" claim, and it is
still used **zero** times in this repo.

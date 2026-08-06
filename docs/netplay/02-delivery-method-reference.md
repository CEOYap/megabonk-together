# Delivery Method Reference

The policy for choosing a `DeliveryMethod`, the complete current map of what this mod puts on
the wire, and the reasoning that should govern any future change.

**Read this before touching any `DeliveryMethod` argument**, and before translating any send
site to a new transport API.

**Stamped `main @ 7810b1b`** (PR #4 merged). Regenerated from source, not edited from the
previous revision — the previous revision was stamped `041881b`, roughly ninety commits stale,
and its map covered one file of the three that send. If you are reading this more than a few
dozen commits after that SHA, re-derive it (see [Regenerating this map](#regenerating-this-map))
rather than trusting it.

---

## The rule

> **Reliability is a correctness property, not a performance knob.**
>
> A message may be `Unreliable` **only if a later message supersedes it.** If losing the
> message leaves two machines permanently disagreeing, it must be reliable.

Everything else follows from that one test.

## Classifying a message

Ask, in order:

1. **Is there a next packet that makes this one irrelevant?**
   Positions, rotations, HP totals, absolute XP values — yes. These are *continuous state*.
   The next tick overwrites whatever was lost. → `Unreliable`.

   The trap in this codebase is that "there is a next packet" has to be true *unconditionally*,
   not "true while the entity keeps changing". A delta stream that only re-sends changed
   entities stops superseding the moment an entity goes still, which is how the enemy stream
   could strand a client on a stale final state forever. See
   [Why the enemy stream is allowed to be unreliable](#why-the-enemy-stream-is-allowed-to-be-unreliable).

2. **Is it a one-shot state transition?**
   "This enemy died", "this chest was opened", "this weapon was added", "this player started
   charging". There is no next packet. → **Reliable**.

3. **Is it idempotent, and is it re-sent?**
   Idempotent *and* periodically re-sent → `Unreliable` is defensible. Idempotent but sent
   once → still reliable, because "applied zero times" is a divergent state.

4. **Does order matter relative to a sibling message?**
   Paired events (start/stop, spawn/despawn, open/close) must not reorder. → `ReliableOrdered`.
   Independent events with no ordering constraint can use `ReliableUnordered`, which avoids
   head-of-line blocking.

5. **Can the payload exceed MTU?**
   LiteNetLib fragments **reliable** channels. It does **not** fragment unreliable ones —
   anything over ~1400 bytes on an unreliable channel fails to send. If a message carries a
   list, a string, or an inventory snapshot, it must be reliable *or chunked below the cap*.
   `WeaponAdded` (carries `List<StatModifier> upgradeOffer`), `TomeAdded` and `ChestOpened` take
   the reliable answer; the four periodic streams take the chunking answer, which is what
   `SendStreamUpdate` exists for.

## Channel semantics

| `LiteNetLib.DeliveryMethod` | Delivered | Ordered | Fragments | Use for |
|---|---|---|---|---|
| `ReliableOrdered` | guaranteed | yes | yes | paired/sequential events; the safe default |
| `ReliableUnordered` | guaranteed | no | yes | independent one-shot events; avoids head-of-line blocking |
| `ReliableSequenced` | **only the last** | yes, drops stale | yes | periodic state where only the newest matters |
| `Sequenced` | best effort | drops stale | no | high-rate state where staleness is worse than loss |
| `Unreliable` | best effort | no | **no** | high-rate continuous state, superseded every tick |

**`ReliableSequenced` is not "reliable".** It guarantees only the *most recent* packet on that
channel; intermediate packets may be dropped by design. It is the wrong channel for a one-shot,
and this codebase has one send that reaches it — see
[The implicit `SendToHost` default](#the-implicit-sendtohost-default).

---

## The five send paths

Everything this mod sends goes through one of five paths. **Only two of them take a
`DeliveryMethod` from the caller**, which is the single most important fact the previous
revision of this document omitted: reading a call site is not enough to know what channel a
message rides.

| Path | Signature | Delivery |
|---|---|---|
| Host broadcast | `SendToAllClients<T>(T, DeliveryMethod)` | **caller's**, relay leg included |
| Host broadcast, pre-serialized | `SendToAllClients(byte[], DeliveryMethod, string label)` | **caller's**, relay leg included |
| Client → host | `SendToHost<T>(T, DeliveryMethod? = null)` | caller's on the direct leg; **hardcoded `ReliableOrdered` on the relay leg**; `ReliableSequenced` when the caller passes nothing |
| Host → one client | `SendToClient<T>(NetPeer, T, uint)` | **hardcoded `ReliableOrdered`**, no parameter |
| Host rebroadcast | `SendToAllClientsExcept<T>(int, uint, T)` | **hardcoded `ReliableOrdered`**, no parameter |

All five live in [`UdpClientService.cs`](../../src/plugin/Services/UdpClientService.cs).

Two size behaviours are applied inside the helpers, not at the call sites:

- `SendToHost` **promotes to `ReliableOrdered`** when the serialized message is
  `>= MAX_PACKET_SIZE_BYTES` (1000). So a client-side `Unreliable` request is not a guarantee of
  an unreliable send.
- `SendStreamUpdate` (the four periodic streams) **chunks** an oversized tick into several
  sub-MTU `Unreliable` datagrams, and only promotes a chunk that still overflows.

### The implicit `SendToHost` default

`SendToHost`'s `DeliveryMethod?` parameter defaults to `null`, which resolves to
**`ReliableSequenced`**. Exactly one call site relies on it:

```
UdpClientService.cs:266   SendToHost(introduced);      // Introduced, union tag 30
```

`Introduced` is a one-shot handshake message, and rule 2 says a one-shot must be reliable.
`ReliableSequenced` only guarantees the *last* packet on its channel. This is currently harmless
only because `Introduced` is the sole occupant of that channel, so it is always the last packet
on it — an accident of there being no second caller, not a property anyone chose.

**Any second use of the implicit default puts both messages at risk.** If you add a
`SendToHost` call, pass the delivery method explicitly. Every other one of the 27 `SendToHost`
sites already does.

---

## Stream 1 — the four periodic host broadcasts

**These are over 90% of host egress by measurement, and were entirely absent from the previous
revision of this document.** They live in `UdpClientService.cs`, driven off accumulators in
[`Scripts/NetworkHandler.cs`](../../src/plugin/Scripts/NetworkHandler.cs), and all four go
through `SendStreamUpdate`.

| Sender | Message (tag) | Rate | Channel | Gate |
|---|---|---|---|---|
| `SendPlayersStateUpdate` | `PlayersStateUpdate` (**68**) | 60 Hz | `Unreliable`, chunked | host, in-game |
| `SendLobbyUpdate` | `LobbyUpdates` (0) — players | 5 Hz + forced on readiness change | `Unreliable`, chunked | host, in-game |
| `SendEnemiesUpdate` | `LobbyUpdates` (0) — enemies + boss orbs | 40 Hz | `Unreliable`, chunked | host, in-game |
| `SendProjectilesUpdate` | `ProjectilesUpdate` (39) | 20 Hz | `Unreliable`, chunked | host, in-game |
| `SendTumbleWeedsUpdate` | `TumbleWeedsUpdate` (46) | 20 Hz | `Unreliable`, chunked | host, in-game, Desert only |

Tick rates are `LOBBY_UPDATE_TICK_RATE` 60, `ENEMY_UPDATE_TICK_RATE` 40,
`PROJECTILE_UPDATE_TICK_RATE` 20, `TUMBLEWEED_UPDATE_TICK_RATE` 20 in `NetworkHandler`. The
60 Hz lobby accumulator drives `UdpClientService.Update()`, which sends the player *state*
every tick and the full player *record* every `FULL_PLAYER_RECORD_EVERY_N_TICKS` (12) ticks —
hence 5 Hz for the record.

**`LobbyUpdates` (tag 0) is two unrelated streams sharing one type.** `SendLobbyUpdate` carries
`Players`; `SendEnemiesUpdate` carries `Enemies` + `BossOrbs`. They scale with completely
different things and are three orders of magnitude apart in volume. The bandwidth counters label
them by hand — `"LobbyUpdates(players)"` and `"LobbyUpdates(enemies)"` — precisely because
`GetType().Name` merged them, and that merge sent three sessions of optimisation work at the
wrong stream. Do not undo the hand labels, and do not reason about "the LobbyUpdates stream" as
one thing.

### Chunking, and why `Unreliable` survives a large tick

`SendStreamUpdate` serializes the whole tick once. If it fits under
`MAX_PACKET_SIZE_BYTES` (1000) it goes out as one `Unreliable` datagram — the common case, one
serialize, one send. If it does not, the item count per chunk is derived from the tick's average
bytes-per-item against a budget of `1000 - CHUNK_ENVELOPE_HEADROOM_BYTES` (100), and the tick
goes out as several `Unreliable` datagrams.

The 100-byte headroom is because every chunk repeats the message envelope — the union tag and
the empty-collection headers for the fields that stream does not set. Budgeting at the cap would
land the last chunk just over it.

**A chunk that still overflows promotes to `ReliableOrdered`.** That is reachable when the
average underestimated a chunk, or when one single item exceeds the whole budget (a player with
a large inventory). Promotion is the only way such a message arrives at all, so the fallback
makes the worst case no worse than before chunking rather than a silent dropped send.

This is the correct shape for these streams: the tick does not need to arrive in one message, it
needs to arrive. Before chunking, an oversized tick was promoted wholesale, which paid acks and
retransmits to redeliver entity positions that were already stale on arrival, and head-of-line
blocked every later update behind one stalled fragment.

**Reading the counters:** a split tick records once per chunk, so sends/s rises and B/send falls
while KB/s stays flat. That is the mechanism working, not a regression.

**Measured, two players, direct P2P @ 65 ms rtt (2026-08-06):** no stream's B/send reached the
1000 B cap (max 725 and 733, against 2100 and 4761 before chunking), so nothing was promoted.
Projectiles reached 3.9 chunks/tick and enemies 2.5, so splitting does fire under load.
`PlayersStateUpdate` measured 98 B/send against a predicted 98. Host egress peak fell
321.1 → 154.9 → 98.6 KB/s across three sessions.

**Not measured:** the promotion fallback has never been observed firing, and neither has any
tick at more than two players. Six-player sizes in the code comments are *derived from the
serializer*, not captured. Two players is the maximum available to this project.

### Why the enemy stream is allowed to be unreliable

The enemy stream is a delta, which by itself fails test 1: an enemy that stops changing produces
no further packets, so nothing supersedes the last one. If that packet is lost the client is
wrong about that enemy for the rest of the run.

It is legitimate anyway because `EnemyManagerService` **bounds staleness instead of guaranteeing
delivery**. Changed enemies are sent stalest-first up to `MAX_ENEMIES_PER_TICK`
(`MAX_ENEMY_TICK_BYTES` 3600 / `ENEMY_MODEL_WIRE_BYTES` 15 = 240), and spare budget refreshes
enemies not sent within `ENEMY_REFRESH_PERIOD_TICKS` (40 ticks ≈ 1 s at 40 Hz). Nothing can stay
wrong indefinitely: worst case it is wrong for about a refresh period.

That is the pattern to copy if you ever want another unreliable delta stream. The alternative —
tracking the last *acknowledged* state — needs acks, which means a per-client baseline and a
per-client serialize every tick instead of one broadcast.

**Unverified:** the per-tick cap has never actually bound in play. The one session that ran it
peaked at ~130 enemies/tick against the 240 budget, so the deferral behaviour the budget exists
for is untested.

---

## Stream 2 — the client's periodic send

| Sender | Message (tag) | Rate | Channel |
|---|---|---|---|
| `UdpClientService.Update` (client branch) | `PlayerUpdate` (3) | 60 Hz | `Unreliable` — **but see below** |

Correct by test 1: position, rotation, HP and animator state, superseded every tick.

**On a relayed session this is not unreliable.** `SendToHost`'s relay branch hardcodes
`ReliableOrdered`, ignoring the caller's argument. So a client behind a failed hole-punch puts
its 60 Hz position stream on a reliable ordered channel — paying acks and retransmits for
positions that are stale on arrival, and head-of-line blocking everything behind any stalled
packet. See [Relay asymmetries](#relay-asymmetries).

---

## Stream 3 — event messages (`SynchronizationService`)

84 `DeliveryMethod` sites across 55 methods in
[`SynchronizationService.cs`](../../src/plugin/Services/SynchronizationService.cs).

Almost every one is the same shape: the host broadcasts, the client asks the host to. The client
half is `SendToHost(message, ReliableOrdered)` at **every** one of these sites — the interesting
variation is entirely on the host half.

### Host half — `Unreliable` (1 method)

| Method | Message (tag) | Why it is safe |
|---|---|---|
| `OnEnemyDamaged` | `EnemyDamaged` (20) | Superseded: `EnemyModel.Hp` rides the 40 Hz enemy delta stream, and `HP_TRESHOLD = 1` means an HP change marks the enemy as changed. The refresh sweep bounds the worst case at ~1 s regardless. |

`SynchronizationService.cs:1653` still carries `//TODO: Can be unreliable i think ?` on a site
that is already `Unreliable`. The TODO is stale; the answer is yes, for the reason above.

### Host half — `ReliableUnordered` (7 methods)

| Method | Message (tag) |
|---|---|
| `OnSpawnedEnemy` | `SpawnedEnemy` (4) |
| `OnSpawnedChest` | `SpawnedChest` (13) |
| `OnSpawnedProjectile` | `SpawnedProjectile` (7) and the ten concrete subtypes (33–38, 50, 51, 60, 61) |
| `SendSpawnedObject` | `SpawnedObject` (2) |
| `OnFinalBossOrbsSpawned` | `FinalBossOrbSpawned` (24) |
| `OnFinalBossOrbDestroyed` | `FinalBossOrbDestroyed` (25) |
| `OnSelectedCharacter` | `SelectedCharacter` (5) |

Spawn notifications. Each is independent of the others, so ordering is not needed — but delivery
is, because a missed spawn means the entity never exists on that client.

### Host half — `ReliableOrdered` (everything else, 47 methods)

<details>
<summary>Full list, with union tags</summary>

`ForceCloseEncounter` (66, 65), `HandleChargingStart` / `HandleChargingStop` (the shared helpers
behind shrine 17/18, pylon 22/23, lamp 56/57), `HandleWantToStartFollowingPickup` (12),
`OnChangeGold` (67), `OnChestOpened` (14), `OnEnemyDied` (6), `OnEnemyExploder` (19),
`OnHatChanged` (59), `OnInteractableUsed` (16), `OnItemAdded` (52), `OnItemRemoved` (53),
`OnLightningStrike` (41), `OnPickupApplied` (11), `OnPickupOrbSpawned` (9), `OnPickupSpawned`
(10), `OnPlayerDied` (28, 29), `OnProjectileDone` (8), `OnReceivedPlayerDied` (28, 29),
`OnReceivedStartingToChargingLamp` (56), `OnReceivedStartingToChargingPylon` (22),
`OnReceivedStartingToChargingShrine` (17), `OnReceivedStoppingChargingLamp` (57),
`OnReceivedStoppingChargingPylon` (23), `OnReceivedStoppingChargingShrine` (18), `OnRespawn`
(63), `OnRunStarted` (32), `OnSpawnedEnemySpecialAttack` (21), `OnSpawnedObjectInCrypt` (55),
`OnStormStarted` (43), `OnStormStopped` (44), `OnSwarmEvent` (26), `OnTimerStarted` (58),
`OnTomeAdded` (40), `OnTornadoesSpawned` (42), `OnTumbleWeedDespawned` (47), `OnTumbleWeedSpawned`
(45), `OnWeaponAdded` (15), `OnWeaponToggled` (54), `PlayerXpAddXp` (64), `RetargetAfterDisconnect`
(66, 29), `RewardFinished` (65, 66), `SendPickupFollowingPlayer` (12), `SpawnReviver` (62)

Client-only senders (no host broadcast half): `HandleGameEvent` (1),
`OnInteractableFightEnemySpawned` (48), `OnWantToStartFollowingPickup` (49).

</details>

This map originates in upstream commit `24f5004` *"fix: update packet delivery methods to improve
performance and prevent desync issues"*. It is deliberate tuning; treat it as the baseline.

---

## Stream 4 — the host rebroadcast path

When the host receives a client's event message, it republishes it locally through `EventManager`
**and** forwards it to the other clients with `SendToAllClientsExcept`. That helper takes no
delivery method: **every one of these 24 forwards is `ReliableOrdered`**, in both the direct and
the relay branch.

This path was absent from the previous revision of this document entirely.

<details>
<summary>The rebroadcast message types (UdpClientService receive switch, lines 851–1027)</summary>

`SelectedCharacter` (5), `AbstractSpawnedProjectile` (7 and subtypes 33–38, 50, 51, 60, 61),
`ProjectileDone` (8), `EnemyDied` (6), `PickupApplied` (11), `PickupFollowingPlayer` (12),
`ChestOpened` (14), `WeaponAdded` (15), `InteractableUsed` (16), `EnemyExploder` (19),
`EnemyDamaged` (20), `StartingChargingPylon` (22), `StoppingChargingPylon` (23),
`FinalBossOrbDestroyed` (25), `TomeAdded` (40), `ItemAdded` (52), `ItemRemoved` (53),
`WeaponToggled` (54), `StartingChargingLamp` (56), `StoppingChargingLamp` (57), `TimerStarted`
(58), `HatChanged` (59), `AddXp` (64), `GoldChanged` (67), `PlayerDisconnected` (31, via
`SendToAllClients` rather than the exclusion helper)

Received but **not** rebroadcast: `PlayerUpdate` (3), `StartingChargingShrine` (17),
`StoppingChargingShrine` (18), `PlayerDied` (28), `InteractableCharacterFightEnemySpawned` (48),
`WantToStartFollowingPickup` (49), `EncounterClosed` (65). Three more are commented out in the
switch: `SpawnedEnemySpecialAttack` (21), `FinalBossOrbSpawned` (24), `SpawnedObjectInCrypt` (55).

</details>

Two consequences worth stating plainly:

1. **`EnemyDamaged` (20) has two different channels depending on who originated it.** Host-origin
   is `Unreliable`; a client's, forwarded, is `ReliableOrdered`. That is not a considered choice,
   it is the exclusion helper having no parameter. It is the highest-rate message on this path
   and therefore the one where the cost is real.

2. **Pylon and lamp charging start/stop are sent twice to every non-origin peer.** The receive
   switch forwards them with `SendToAllClientsExcept`, and the same message also reaches
   `SynchronizationService.OnReceivedStartingToChargingLamp` / `…Pylon` via `EventManager`, whose
   host branch calls `SendToAllClients` — to everyone. Shrine (17/18) does not do this: it has no
   case in the exclusion switch, which is why it is the odd one out above. **UNVERIFIED in play**
   — this is read from the two call chains, not observed on the wire. It is idempotent on the
   receiving side, so the expected cost is bandwidth rather than a correctness bug, but nobody
   has confirmed which of the two arrives first or whether either handler is order-sensitive.

---

## Stream 5 — session lifecycle

| Sender | Message (tag) | Path | Channel |
|---|---|---|---|
| `PeerConnectedEvent` (client) | `Introduced` (30) | `SendToHost` | **implicit `ReliableSequenced`** — see above |
| host handshake reply | `Introduced` (30) | `SendToClient` | `ReliableOrdered` (hardcoded) |
| `Update` (host, game over) | `GameOver` (27) | `SendToAllClients` | `ReliableOrdered` |
| disconnect handling | `PlayerDisconnected` (31) | `SendToAllClients` | `ReliableOrdered` |
| `EncounterClosed` handler | `CloseEncounter` (66) | `SendToAllClients` | `ReliableOrdered` |
| relay bind | raw `"<id>\|RELAY_BIND"` string | `peer.Send` | `ReliableOrdered` |

The relay bind is not a `IGameNetworkMessage` and carries no union tag; it is a raw
`NetDataWriter` string on the socket to the rendezvous server.

---

## Relay asymmetries

When a direct connection cannot be established, traffic goes host ↔ `RendezVousServer` ↔ client,
wrapped in a `RelayEnvelope`. Two facts govern what the channel actually is end to end:

1. **The server forwards on the delivery method the hop arrived on.**
   `RendezVousServer.cs:274 / 288 / 305` all pass the incoming `deliveryMethod` straight through.
   So the relay leg preserves whatever the plugin chose for the host→server hop; there is no
   second downgrade.

2. **The plugin does not always choose the same thing for the relay leg as for the direct leg.**

| Helper | Direct leg | Relay leg | Same? |
|---|---|---|---|
| `SendToAllClients<T>` | caller's | caller's | yes |
| `SendToAllClients(byte[])` | caller's | caller's | yes |
| `SendToHost` | caller's (or promoted on size) | **`ReliableOrdered`, always** | **no** |
| `SendToClient` | `ReliableOrdered` | `ReliableOrdered` | yes |
| `SendToAllClientsExcept` | `ReliableOrdered` | `ReliableOrdered` | yes |

The single asymmetry is `SendToHost`, and its only high-rate caller is the client's 60 Hz
`PlayerUpdate`. **A relayed client sends its position stream reliably-ordered.** Nothing in the
code says this was intended.

Whether that is worth changing is a judgement call this document does not make — a reliable
60 Hz stream through a shared relay is expensive, but the relay path is already the degraded
path and its loss characteristics have never been measured. It is recorded here so the Phase 1
translation does not carry it across silently as though it were a decision.

**The exclusion filter itself has been traced and is correct.** `SendToAllClientsExcept` takes
`netPlayerId` (a LiteNetLib `NetPeer.Id`) and `sender` (a game connection id) — two different id
spaces — but each is used only against the map that speaks it, and the server filters on the same
connection-id space the relay branch writes. Full trace, plus two harmless defects found along the
way: [`01-critical-fixes.md`](01-critical-fixes.md#p1-1) under *Sender exclusion in relay mode*.

---

## Full union tag → channel index

Tags are **append-only**; 0–68 are allocated. This table says which channel each tag's traffic
rides, so a Phase 1 translation can be checked tag by tag.

| Tag | Type | Origin | Channel |
|---|---|---|---|
| 0 | `LobbyUpdates` | host periodic (two streams) | `Unreliable`, chunked |
| 1 | `ClientInGameReady` | client | `ReliableOrdered` |
| 2 | `SpawnedObject` | host | `ReliableUnordered` |
| 3 | `PlayerUpdate` | client periodic | `Unreliable` direct / `ReliableOrdered` relay |
| 4 | `SpawnedEnemy` | host | `ReliableUnordered` |
| 5 | `SelectedCharacter` | both | `ReliableUnordered` host / `ReliableOrdered` client + rebroadcast |
| 6 | `EnemyDied` | both | `ReliableOrdered` |
| 7, 33–38, 50, 51, 60, 61 | `SpawnedProjectile` + subtypes | both | `ReliableUnordered` host / `ReliableOrdered` client + rebroadcast |
| 8 | `ProjectileDone` | both | `ReliableOrdered` |
| 9 | `SpawnedPickupOrb` | host | `ReliableOrdered` |
| 10 | `SpawnedPickup` | host | `ReliableOrdered` |
| 11 | `PickupApplied` | both | `ReliableOrdered` |
| 12 | `PickupFollowingPlayer` | both | `ReliableOrdered` |
| 13 | `SpawnedChest` | host | `ReliableUnordered` |
| 14 | `ChestOpened` | both | `ReliableOrdered` |
| 15 | `WeaponAdded` | both | `ReliableOrdered` (MTU-crosser) |
| 16 | `InteractableUsed` | both | `ReliableOrdered` |
| 17, 18 | `StartingChargingShrine` / `StoppingChargingShrine` | both | `ReliableOrdered` |
| 19 | `EnemyExploder` | both | `ReliableOrdered` |
| 20 | `EnemyDamaged` | both | **`Unreliable` host-origin / `ReliableOrdered` rebroadcast** |
| 21 | `SpawnedEnemySpecialAttack` | host | `ReliableOrdered` |
| 22, 23 | `StartingChargingPylon` / `StoppingChargingPylon` | both | `ReliableOrdered`, **double-sent** |
| 24 | `FinalBossOrbSpawned` | host | `ReliableUnordered` |
| 25 | `FinalBossOrbDestroyed` | both | `ReliableUnordered` host / `ReliableOrdered` client + rebroadcast |
| 26 | `StartedSwarmEvent` | host | `ReliableOrdered` |
| 27 | `GameOver` | host | `ReliableOrdered` |
| 28 | `PlayerDied` | both | `ReliableOrdered` |
| 29 | `RetargetedEnemies` | host | `ReliableOrdered` |
| 30 | `Introduced` | both | **`ReliableSequenced` client** / `ReliableOrdered` host |
| 31 | `PlayerDisconnected` | host | `ReliableOrdered` |
| 32 | `RunStarted` | host | `ReliableOrdered` |
| 39 | `ProjectilesUpdate` | host periodic | `Unreliable`, chunked |
| 40 | `TomeAdded` | both | `ReliableOrdered` (MTU-crosser) |
| 41 | `LightningStrike` | host | `ReliableOrdered` |
| 42 | `TornadoesSpawned` | host | `ReliableOrdered` |
| 43, 44 | `StormStarted` / `StormStopped` | host | `ReliableOrdered` |
| 45, 47 | `TumbleWeedSpawned` / `TumbleWeedDespawned` | host | `ReliableOrdered` |
| 46 | `TumbleWeedsUpdate` | host periodic | `Unreliable`, chunked |
| 48 | `InteractableCharacterFightEnemySpawned` | client | `ReliableOrdered` |
| 49 | `WantToStartFollowingPickup` | client | `ReliableOrdered` |
| 52, 53 | `ItemAdded` / `ItemRemoved` | both | `ReliableOrdered` |
| 54 | `WeaponToggled` | both | `ReliableOrdered` |
| 55 | `SpawnedObjectInCrypt` | host | `ReliableOrdered` |
| 56, 57 | `StartingChargingLamp` / `StoppingChargingLamp` | both | `ReliableOrdered`, **double-sent** |
| 58 | `TimerStarted` | both | `ReliableOrdered` |
| 59 | `HatChanged` | both | `ReliableOrdered` |
| 62 | `SpawnedReviver` | host | `ReliableOrdered` |
| 63 | `PlayerRespawned` | host | `ReliableOrdered` |
| 64 | `AddXp` | both | `ReliableOrdered` |
| 65, 66 | `EncounterClosed` / `CloseEncounter` | both | `ReliableOrdered` — **superseded by 69/70; received only** |
| 67 | `GoldChanged` | both | `ReliableOrdered` |
| **68** | **`PlayersStateUpdate`** | **host periodic** | **`Unreliable`, chunked** |
| **69** | **`EncounterClosedStamped`** | **client** | **`ReliableOrdered`** |
| **70** | **`CloseEncounterStamped`** | **host** | **`ReliableOrdered`** |

"both" means the host broadcasts it and the client requests it; the two halves may differ, and
where they do the cell says so.

---

## The `Sea-Bass-cmd` downgrade — do not apply

`Sea-Bass-cmd/optimized-netplay` moves 17 of the `ReliableOrdered` methods above to
`Unreliable`. Each fails test 1 (nothing supersedes them) and several fail test 5 (MTU).

| Method | Consequence of one dropped packet |
|---|---|
| `OnEnemyDied` | Enemy never dies on peers. Permanent ghost, never despawns, accumulates over the run. |
| `OnChestOpened` | Chest state diverges; contents mismatch. |
| `OnWeaponAdded` | Player permanently missing a weapon on remote clients. Also an MTU-crosser. |
| `OnTomeAdded` | Same, for tomes. |
| `OnPickupSpawned` / `OnPickupOrbSpawned` | XP orb never appears on that client. |
| `OnPickupApplied` | XP never granted. Compounds with the shared-XP model into level drift. |
| `OnWantToStartFollowingPickup` / `SendPickupFollowingPlayer` | Orphaned pickup. |
| `OnInteractableUsed` | Interactable state diverges. |
| `OnProjectileDone` | Projectile never cleaned up on peers. Leaked objects. |
| `OnStartingToChargingShrine` | Charge never registers. |
| `OnStoppingChargingShrine` | **Shrine locked forever** — the charger list never empties. |
| `HandleChargingStart` / `HandleChargingStop` (pylon, lamp) | Same, for pylons and graveyard boss lamps. |
| `OnEnemyExploder` | Missed damage event. Tolerable. |
| `OnSpawnedEnemySpecialAttack` | Missed attack. Tolerable. |

Three aggravating factors:

1. **Highest risk exactly where the change claims benefit.** Packet loss scales with send rate.
   At 600 enemies the `OnEnemyDied` volume peaks, so ghost accumulation is worst in the scenario
   the change targets.
2. **`Unreliable` is also unordered.** A `stop` overtaking its `start` leaves a shrine stuck in
   the charging state with no recovery path — the same end state as P0-1, reached differently.
3. **No fragmentation.** See test 5.

There is no resend, no sequence number, and no reconciliation snapshot for any of these events.
Nothing recovers a lost one. The periodic streams are the sole exception, and only because
`EnemyManagerService`'s refresh sweep was built for exactly that reason.

---

## If you actually want the bandwidth

The previous revision listed five routes. Three have since landed; what is left is smaller and
more specific.

**Done:**

- Sub-MTU chunking of the four periodic streams (`0b0dc97`), so oversized ticks no longer promote
  to a reliable channel.
- Splitting the player stream into 60 Hz continuous state and a 5 Hz full record (`a79ea0c`) —
  combined player traffic 19.98 → 7.34 KB/s at two players, a 63% cut.
- Budgeted, staleness-ordered enemy stream (`1a02504`), which caps the per-tick payload.
- Position quantization: `Helpers/Quantizer.cs` and the `Quantized*` models are already used by
  every position on the wire.

**Remaining, roughly in order of payoff:**

1. **Widen the delta thresholds.** `EnemyManagerService.cs:45-47` uses `POSITION_TRESHOLD = 0.1f`,
   `YAW_TRESHOLD = 5.0f`, `HP_TRESHOLD = 1`. Raising the position threshold cuts the per-tick
   enemy payload directly, with a visible-smoothness tradeoff you can tune. Zero correctness risk.
2. **Relevance-order the enemy budget.** Under the per-tick cap it is better to degrade enemies
   far from every player than whichever sort ordering lands last. `DistanceThrottler` already
   computes distance bands, but only on the receiving peer's `Enemy.MyUpdate` — it has never
   gated the host's send. This is a strict refinement of the existing ordering.
3. **Give `SendToAllClientsExcept` a `DeliveryMethod` parameter.** It would let the forwarded
   `EnemyDamaged` (tag 20) match the host-origin channel instead of paying `ReliableOrdered` for
   the highest-rate message on that path. One signature change; every caller currently wants
   `ReliableOrdered` except that one.
4. **Move independent one-shots from `ReliableOrdered` to `ReliableUnordered`.** Removes
   head-of-line blocking without giving up delivery. Safe for anything with no paired sibling —
   `OnItemAdded`, `OnHatChanged`, the remaining spawn notifications. **Not** for charging
   start/stop.
5. **Batch small messages.** Not currently done. Comes free with the Steamworks migration (Nagle
   is on by default). See [`../steamworks/01-api-mapping.md`](../steamworks/01-api-mapping.md).

`BandwidthDiagnostics` already records per-message-type bytes, sends and rate; measure before and
after. When you add a counter, ask what it *cannot* distinguish before trusting what it says —
the merged `LobbyUpdates` bucket sent three sessions after the wrong stream.

---

## Adding a new message

1. Define it under `src/common/Messages/GameNetworkMessages/`. Quantized/primitive types only —
   no `UnityEngine` types; `src/common/` compiles into the server.
2. Add a `[MemoryPackUnion(N, ...)]` line with the **next free N** (71 at this stamp). Never
   reuse, renumber, or remove a tag: MemoryPack is positional and peers on different mod versions
   still handshake, so a changed tag corrupts sessions silently rather than failing loudly.
   Adding a field to an existing message is the same hazard.
3. **There is no protocol version gate.** The previous revision of this document told you to bump
   `Protocol.Version`. That type does not exist — it was proposed, implemented, disproved in-game
   and reverted. See [P1-3](01-critical-fixes.md#p1-3), which explains why the connect-key
   approach never fires, before proposing it again. A version bump does not make a wire change
   safe today.
4. Run it through tests 1–5 above and record the answer in a comment at the send site.
5. Choose the send path deliberately, and check what that path does with your delivery method —
   three of the five ignore it. See [The five send paths](#the-five-send-paths).
6. If it is a delta (like `GoldChanged.Amount`), relay it with `SendToAllClientsExcept`, never
   `SendToAllClients` — see fix [P1-1](01-critical-fixes.md#p1-1).
7. Measure the serialized size. Over ~1000 bytes it is reliable-only, or it has to be chunked.

---

## Regenerating this map

This document goes stale silently, which is how the previous revision survived ninety commits
past its stamp. Two scans reproduce most of it:

```bash
grep -rn "DeliveryMethod\." --include=*.cs src/plugin/
```

```bash
grep -rnE "\b(SendToAllClientsExcept|SendToAllClients|SendToHost|SendToClient)\s*[<(]" --include=*.cs src/plugin/
```

**Run the second one too.** The first alone is what produced the previous revision's blind spot:
all 24 `SendToAllClientsExcept` call sites and the implicit `SendToHost` default name no
`DeliveryMethod` at all, so a scan for `DeliveryMethod.` cannot see them — and the four periodic
broadcasts that account for over 90% of host egress reach the socket through a helper the first
scan attributes to `SendStreamUpdate` rather than to any of them.

Re-stamp the heading with the SHA you scanned.

---

## Testing reliability changes

Never on LAN. Local loopback and same-subnet play both run at effectively 0% loss, which is
precisely the condition under which every bug in this document is invisible. Two instances on one
PC is `rtt 0 ms`, which is not a LAN test either.

- **Windows:** [clumsy](https://jagt.github.io/clumsy/) — 3% drop, 50 ms lag, 1% duplicate.
- **Linux:** `tc qdisc add dev <iface> root netem loss 3% delay 50ms duplicate 1%`

Then check for: ghost enemies that never despawn, shrines that refuse to charge, inventory
mismatches between clients, and gold totals that disagree.

**Two players is the maximum available to this project**, so a final-swarm test at 4+ players is
not a reproduction step anyone here can execute. Do not write one into a checklist. Six-player
payload sizes are derived from the serializer instead; six-player *behaviour* is accepted risk.
See [`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md) Phase 0.

---
name: netcode
description: |
  MegabonkTogether's networking model — MemoryPack message contract, LiteNetLib delivery methods, host authority, tick rates, and quantization.
  Use when: adding or changing an IGameNetworkMessage, choosing a DeliveryMethod, investigating desync or dropped state, touching NetworkHandler tick rates, or changing anything in src/common/Messages.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Netcode

Peer-to-peer, **host-authoritative**, up to 6 players. Transport is LiteNetLib UDP; a
matchmaking/relay server (`src/server/`) does WebSocket matchmaking and NAT introduction, and
relays UDP when direct connection fails (IPv6, or failed IPv4 hole-punch).

Two topologies, same message set:

- **Direct P2P** — peers connect to each other after NAT introduction.
- **Relay** — traffic goes through `RendezVousServer` on the matchmaking host.

## The message contract

Every message is a `[MemoryPackable] partial class` implementing `IGameNetworkMessage`, living in
`src/common/Messages/GameNetworkMessages/`.

```csharp
[MemoryPackable]
public partial class ChestOpened : IGameNetworkMessage
{
    public uint ChestId { get; set; }
    public uint OwnerId { get; set; }
}
```

Polymorphic dispatch is a **hand-numbered union** on the interface in `GameNetworkMessage.cs`:

```csharp
[MemoryPackUnion(14, typeof(ChestOpened))]
```

### Adding a message — the checklist

1. Create the class in `src/common/Messages/GameNetworkMessages/`. Only quantized/primitive
   types — **no `UnityEngine` types in `src/common/`**.
2. Add a `[MemoryPackUnion(N, ...)]` line with the **next free N**. Never reuse or renumber an
   existing tag.
3. Send it from the appropriate service, with a deliberate `DeliveryMethod` (see below).
4. Handle it in `UdpClientService`'s receive switch, then republish via `EventManager` so
   gameplay code stays decoupled from transport.

**Union tags are the wire format.** Renumbering, reordering, or removing a tag breaks every
client on a different mod version. Tags are append-only. Version bumps do not make it safe —
peers on different versions still handshake.

## Delivery method — read the doc first

`docs/netplay/02-delivery-method-reference.md` is the authority: it holds the policy, the
current message→channel map, and the reasoning. **Read it before touching any `DeliveryMethod`
argument.** The governing rule:

> Reliability is a correctness property, not a performance knob. A message may be `Unreliable`
> only if a later message supersedes it.

| Method | Delivered | Ordered | Fragments >MTU | Use for |
|---|---|---|---|---|
| `ReliableOrdered` | yes | yes | yes | paired/sequential events; the safe default |
| `ReliableUnordered` | yes | no | yes | independent one-shots; avoids head-of-line blocking |
| `ReliableSequenced` | latest | yes | yes | periodic state where only newest matters |
| `Sequenced` | best effort | drops stale | **no** | high-rate state, staleness worse than loss |
| `Unreliable` | best effort | no | **no** | continuous state superseded every tick |

**The MTU trap:** LiteNetLib fragments reliable channels only. Anything carrying a list, a
string, or an inventory snapshot must be reliable regardless of how "continuous" it looks —
over ~1400 bytes on an unreliable channel simply fails to send. `WeaponAdded`, `TomeAdded`,
`ChestOpened` are all in this category.

## Host authority

The host owns simulation. `Plugin.cs` carries the authority flags as static gates:

```csharp
public static bool CAN_SPAWN_PICKUPS = false;
public static bool CAN_SPAWN_CHESTS  = false;
public static bool CAN_ENEMY_EXPLODE = false;
public bool CAN_DAMAGE_ENEMIES       = false;
```

Clients do not spawn authoritative entities; they receive spawn messages and instantiate. When
adding a new synchronized entity, decide explicitly which side owns it and gate the spawn path
on `IsHost`.

Send helpers on `IUdpClientService`:

```csharp
void SendToAllClients<T>(T data, DeliveryMethod deliveryMethod) where T : IGameNetworkMessage;
void SendToHost<T>(T data, DeliveryMethod? deliveryMethod = null) where T : IGameNetworkMessage;
void SendToClient<T>(NetPeer client, T data, uint netPlayerId) where T : IGameNetworkMessage;
void SendToAllClientsExcept<T>(int netPlayerId, uint sender, T data) where T : IGameNetworkMessage;
```

## Tick rates

`Scripts/NetworkHandler.Update()` drives sends off accumulators, not every frame. Current rates:

| Stream | Rate | Notes |
|---|---|---|
| Lobby / player update | 60 Hz | `SendToHost(playerUpdate, DeliveryMethod.Unreliable)` |
| Enemies | 40 Hz | host only, while game started |
| Projectiles | 20 Hz | host only |
| Tumbleweeds | 20 Hz | host only, Desert map only |

Pattern for adding a stream:

```csharp
private const float X_UPDATE_TICK_RATE = 20f;
private const float xUpdatetickInterval = 1f / X_UPDATE_TICK_RATE;
private float xUpdateAccumulator = 0f;
```

Raising a rate raises bandwidth *and* CPU on every peer. Historical FPS complaints in the
changelog (v4.1.0, v4.2.2, v5.x) trace back to send volume — treat rate increases as a
performance change, not a fidelity tweak.

## Bandwidth reduction

- **Quantization** — `Helpers/Quantizer.cs` packs world positions to `short` against world
  bounds and yaw to `ushort`. Models: `QuantizedVector2/3`, `QuantizedRotation`. Use these in
  messages, not raw floats.
- **Distance throttling** — `Helpers/DistanceThrottler.cs` skips updates and disables renderers
  for `Far` entities, throttles `Medium` to an interval. Host still sends (`isServer: true`)
  where correctness requires it.

## Common mistakes

1. **Reusing or renumbering a `MemoryPackUnion` tag** → silent cross-version corruption.
2. **`Unreliable` on a one-shot transition** (died / opened / added) → permanent desync.
3. **`Unreliable` on a message carrying a list or string** → over MTU, never sends, no error.
4. **A `UnityEngine` type in `src/common/`** → server build breaks.
5. **Client-side spawn of a host-owned entity** → duplicate entities.
6. **Raising a tick rate to "fix" jitter** → interpolate on the receiving side instead; see the
   `*Interpolator` scripts.
7. **Handling a message directly in the receive path** → publish through `EventManager`.

## Repo docs

- `docs/netplay/02-delivery-method-reference.md` — delivery policy + full message map
- `docs/netplay/01-critical-fixes.md` — known-bad areas, UNVERIFIED markers
- `docs/netplay/04-performance-and-gc.md` — allocation on the send/receive path
- `NETPLAY_CHANGES.md` — user-facing summary of behaviour changes

## Related skills

- **csharp** — service layout, EventManager
- **il2cpp** — marshalling cost when reading game state to build a message

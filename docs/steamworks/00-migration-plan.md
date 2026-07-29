# Steamworks.NET Migration Plan

Moving from LiteNetLib + NAT punchthrough + self-hosted rendezvous to
[`Steamworks.NET`](https://github.com/rlabrecque/Steamworks.NET) `ISteamNetworkingSockets`.

Reference implementation to read (not to copy):
[`Vanlichtinstein1945/Multibonk`](https://github.com/Vanlichtinstein1945/Multibonk).

---

## Why

### What gets deleted

| Component | Lines | Replaced by |
|---|---|---|
| `src/plugin/Services/WebsocketClientService.cs` | 382 | `SteamMatchmaking` lobbies |
| `src/server/` (entire ASP.NET Core rendezvous server) | ~1900 | Steam lobby list |
| NAT punchthrough (`EventBasedNatPunchListener`, `natPunchComplete`) | — | SDR (Steam Datagram Relay) |
| Relay fallback (`usesRelay`, `relayPeer`, `RelayEnvelope`, `gamePeersIntroducedByRelay`, `hasTriedForceRelay`) | — | SDR |
| Server hosting, nginx config, OpenTelemetry/Prometheus wiring | — | — |

Valve's relay backbone is better than a self-hosted one: global PoPs, DDoS protection, no
hosting cost, no operational burden. This is the single largest maintenance win available to
this project.

### What gets better

1. **Message handling becomes main-thread by construction.**
   `ISteamNetworkingSockets` is **poll-based** — you call `ReceiveMessagesOnConnection` from
   an `Update` loop. LiteNetLib delivers on a background receive thread. Pumping Steam from
   Unity's `Update` means every network message handler runs on the main thread, which
   structurally removes an entire race class:
   - the non-atomic ID allocation race ([P0-3](../netplay/01-critical-fixes.md#p0-3))
   - the `List<uint>` mutation inside `ConcurrentDictionary` in the charging paths
     ([P0-2](../netplay/01-critical-fixes.md#p0-2) note)
   - the `WebsocketClientService` disconnect race (that file is deleted)
   - the shared-buffer caveat on `GetAllPlayersAliveNonAlloc`
     ([P1-4](../netplay/01-critical-fixes.md#p1-4))

   Still implement P0-3 — it is small, and correctness should not depend on a scheduling
   accident.

2. **Steam invites, friends-list joins, and a lobby browser** replace code exchange.
   `GameLobbyJoinRequested_t` gives you "Join Game" from the Steam friends list for free.

3. **Nagle coalescing on by default.** Small messages are batched (~5 ms). This is the
   "packet batching" the Sea-Bass fork claimed but never implemented — it comes free.

4. **Connection lanes.** `ConfigureConnectionLanes` gives real priority and bandwidth-share
   between message classes. A legitimate route to bandwidth reduction **without** giving up
   reliability.

5. **Free diagnostics.** `GetConnectionRealTimeStatus` supplies ping, packet loss, send-queue
   depth, and estimated bandwidth per connection. Today only `GetLatency` exists.

6. **Encryption and authentication.** Every connection is authenticated by SteamID and
   encrypted. `UdpClientService.cs:186` currently connects with the literal string
   `"yourKey"` and `:201` accepts any connection request — both carry `//TODO` markers about
   validation. Those disappear.

7. **Protocol version gating gets easier.** Publish the version as lobby metadata via
   `SteamMatchmaking.SetLobbyData` and filter incompatible lobbies out of the browser before
   anyone connects.

### What it costs

- **Steam-only.** No non-Steam path afterwards.
- **Proton interaction.** Steamworks under Proton is generally fine — the Windows
  `steam_api64.dll` talks to the Proton-side Steam client — and is probably *more* reliable
  than the current NAT-punch path. But it must be tested; see `PROTON_SETUP.md` and commits
  `9384f93`, `ff42005`.
- **Two managed Steamworks wrappers in one process.** See [Gotcha 1](#gotcha-1).

### What it does **not** change

Every finding in [`../AUDIT_optimized-netplay.md`](../AUDIT_optimized-netplay.md) and
[`../netplay/01-critical-fixes.md`](../netplay/01-critical-fixes.md) survives the migration.
They live in game logic and delivery *semantics*, not in the transport.

In particular the gold duplication (P1-1) is unaffected — `ISteamNetworkingSockets` has no
sender-exclusion primitive either. You still write the exclusion yourself.

---

## Prerequisites

**Land these first.** Migrating a broken reliability map is how a bug survives two rewrites.

| Fix | Why first |
|---|---|
| [P0-1](../netplay/01-critical-fixes.md#p0-1), [P0-2](../netplay/01-critical-fixes.md#p0-2) | Charging fixes are transport-independent and carry over unchanged. Verify them on the known-good transport. |
| [P1-3](../netplay/01-critical-fixes.md#p1-3) | The version gate design changes (lobby metadata), but you want the concept and the `Protocol.Version` constant in place first. |
| [`../netplay/02-delivery-method-reference.md`](../netplay/02-delivery-method-reference.md) | The reliability map must be correct and documented before it is translated to a new API. |

---

## Architecture

### The seam

`IUdpClientService` (`src/plugin/Services/UdpClientService.cs:39-73`) is already close to a
transport interface. `SynchronizationService.cs` has **91** `udpClientService.` call sites,
all going through a handful of methods:

```csharp
void SendToAllClients<T>(T data, DeliveryMethod deliveryMethod) where T : IGameNetworkMessage;
void SendToAllClients(byte[] data, DeliveryMethod deliveryMethod);
void SendToHost<T>(T data, DeliveryMethod? deliveryMethod = null) where T : IGameNetworkMessage;
void SendToClient<T>(NetPeer client, T data, uint netPlayerId) where T : IGameNetworkMessage;
void SendToAllClientsExcept<T>(int netPlayerId, uint sender, T data) where T : IGameNetworkMessage;
```

The refactor is therefore mostly confined to `UdpClientService.cs` (1,810 lines) rather than
spreading through game logic. Two leaks to close first:

1. **`LiteNetLib.DeliveryMethod` appears in game-logic call sites.** Introduce a
   transport-neutral enum. See [`01-api-mapping.md`](01-api-mapping.md).
2. **`SendToClient` takes a `LiteNetLib.NetPeer`.** Replace with the existing `uint`
   connection ID.

### Target shape

```
INetTransport                 (transport-neutral; game logic depends only on this)
 ├── LiteNetTransport         (existing UdpClientService, adapted — keep during migration)
 └── SteamNetTransport        (new)

ILobbyService                 (discovery + join, transport-neutral)
 ├── RendezVousLobbyService   (existing WebsocketClientService — delete at the end)
 └── SteamLobbyService        (new; SteamMatchmaking)
```

Proposed interface:

```csharp
namespace MegabonkTogether.Net
{
    public enum NetDelivery
    {
        Unreliable,          // continuous state, superseded next tick
        ReliableUnordered,   // independent one-shot events
        ReliableOrdered      // paired/sequential events; the safe default
    }

    public interface INetTransport
    {
        bool Initialize();
        void Poll();                 // pump receive; call from Unity Update
        void Flush();                // force-send anything Nagle is holding

        bool IsHost { get; }
        uint LocalConnectionId { get; }

        void SendToHost<T>(T msg, NetDelivery delivery) where T : IGameNetworkMessage;
        void SendToAllClients<T>(T msg, NetDelivery delivery) where T : IGameNetworkMessage;
        void SendToAllClientsExcept<T>(uint excludedConnectionId, T msg, NetDelivery delivery) where T : IGameNetworkMessage;
        void SendToClient<T>(uint connectionId, T msg, NetDelivery delivery) where T : IGameNetworkMessage;

        int GetLatency(uint connectionId);
        NetConnectionStats GetStats(uint connectionId);

        event Action<uint> PeerConnected;
        event Action<uint, string> PeerDisconnected;

        void Disconnect(uint connectionId, string reason);
        void Shutdown();
    }
}
```

`NetDelivery` deliberately drops `Sequenced` / `ReliableSequenced` — nothing in the current
map uses them, and `ISteamNetworkingSockets` has no direct equivalent. If a future message
needs sequencing, implement it as a sequence number plus drop-if-older on `Unreliable`.

---

## Phases

Each phase is independently shippable and independently revertable.

### Phase 0 — Prerequisites
- Land P0-1, P0-2, P0-4, P0-3, P1-3.
- Add per-message-type byte counters and a latency/loss readout so the migration can be
  measured rather than asserted.
- **Exit criteria:** charging bugs fixed; a baseline bandwidth profile recorded at 2 / 4 / 6
  players.

### Phase 1 — Introduce the seam (no behaviour change)
- Add `INetTransport` and `NetDelivery`.
- Make `UdpClientService` implement it; map `NetDelivery` → `LiteNetLib.DeliveryMethod`.
- Replace all 91 `udpClientService.` call sites with `netTransport.`.
- Replace `SendToClient(NetPeer, ...)` with `SendToClient(uint connectionId, ...)`.
- **Exit criteria:** `LiteNetLib` types appear only inside `LiteNetTransport`. Behaviour and
  bandwidth identical to Phase 0.

This is the largest mechanical step and the one most likely to introduce a regression. Do it
alone, and playtest it as a no-op change before continuing.

### Phase 2 — Steam plumbing (no transport change yet)
- Reference `Steamworks.NET`. See [Gotcha 1](#gotcha-1) for which copy.
- Verify Steam is already initialised by the game; **do not** call `SteamAPI.Init()`.
- Call `SteamNetworkingUtils.InitRelayNetworkAccess()` at plugin startup.
- Add a debug command that prints `SteamUser.GetSteamID()` and SDR relay status.
- **Exit criteria:** the mod loads, Steam calls succeed, achievements/leaderboards still
  behave exactly as before (they are patched in `Patches/SteamAchievementsManager.cs`,
  `Patches/SteamStatsManager.cs`, `Patches/LeaderBoards.cs`).

This phase is where an IL2CPP/Steamworks conflict will surface. Do not proceed until the game
is demonstrably stable with the reference added.

### Phase 3 — `SteamLobbyService`
- Create/join lobbies via `SteamMatchmaking`.
- Publish `Protocol.Version`, host name, player count, and mode as lobby data.
- Handle `GameLobbyJoinRequested_t` (friends-list "Join Game") and `LobbyDataUpdate_t`.
- Filter the lobby list by protocol version — this is [P1-3](../netplay/01-critical-fixes.md#p1-3)
  in its final form.
- Set `SteamFriends.SetRichPresence` for lobby/in-game status.
- **Exit criteria:** players can find and join a lobby without the rendezvous server. The old
  transport still carries gameplay traffic.

### Phase 4 — `SteamNetTransport`
- `CreateListenSocketP2P` on the host; `ConnectP2P` on clients.
- Handle `SteamNetConnectionStatusChangedCallback_t` for the connection lifecycle, mapping to
  `PeerConnected` / `PeerDisconnected`.
- Implement the send methods per [`01-api-mapping.md`](01-api-mapping.md).
- Poll with `ReceiveMessagesOnConnection` from `Update`.
- Map `NetDelivery` → Steam send flags. **This is the step where the reliability map must not
  drift.**
- Put it behind a config flag so both transports can ship in one build during testing.
- **Exit criteria:** a full run completes on Steam sockets with the same behaviour as
  LiteNetLib, verified under 3% simulated packet loss.

### Phase 5 — Decommission
- Delete `WebsocketClientService.cs`, `src/server/`, the NAT-punch path, and the relay
  fallback.
- Remove the `LiteNetLib` package reference.
- Update `README.md`, `docs/Setup-Own-Server.md` (now obsolete), and `docs/PROTON_SETUP.md`.
- **Exit criteria:** no `LiteNetLib` symbols remain; the repo no longer ships a server.

Keep `LiteNetTransport` behind the config flag for at least one release before deleting it.

---

## Gotchas

<a name="gotcha-1"></a>
### 1. The game already ships Steamworks.NET, and already initialises Steam

Megabonk's IL2CPP assemblies include `Il2Cppcom.rlabrecque.steamworks.net.dll` — that is how
the achievement, stats, and leaderboard code you already patch works.

Consequences:

- **Never call `SteamAPI.Init()`.** Steam is already up. A second init is undefined behaviour
  at best.
- **You have two options for the managed wrapper,** and neither is free:

  **(a) Ship your own managed `Steamworks.NET.dll`** — what Multibonk does (its `.csproj`
  references `Megabonk/Mods/Steamworks.NET.dll`). It P/Invokes the same native
  `steam_api64.dll`, so it works. But you now have two managed callback registries over one
  native dispatch. Multibonk calls `SteamAPI.RunCallbacks()` every frame from
  `LobbyManager.Update()`; since the game pumps callbacks too, **verify this does not
  double-fire the game's own Steam callbacks** — particularly around achievements, which the
  mod is supposed to suppress during netplay.

  **(b) Use the game's IL2CPP assembly via interop.** No second registry, but you are pinned
  to whatever Steamworks.NET version shipped with the game, and IL2CPP-interop'd generic
  `Callback<T>` is awkward to work with.

  **(c) Direct P/Invoke to the flat C API** (`SteamAPI_ISteamNetworkingSockets_*`) with
  `SteamAPI_ManualDispatch_*` for callbacks. Most work, cleanest isolation, no registry
  conflict at all.

  Recommendation: start with **(a)** because it is the known-working path in this exact game,
  and instrument the achievement suppression patches to confirm nothing double-fires. Fall
  back to **(c)** if it does.

### 2. Call `InitRelayNetworkAccess()`

```csharp
SteamNetworkingUtils.InitRelayNetworkAccess();
```

Call it at plugin startup, well before the first `ConnectP2P`. It begins fetching the SDR
network configuration and authentication ticket. Multibonk does not call it, which costs a
multi-second stall on the first connection.

### 3. Unreliable messages do not fragment

`k_nSteamNetworkingSend_Reliable` fragments and reassembles up to 512 KB. Unreliable messages
above roughly 1200 bytes are fragmented *unreliably* — losing any fragment discards the whole
message. Anything carrying a list, a string, or an inventory snapshot must be reliable
regardless of its semantics. See
[`../netplay/02-delivery-method-reference.md`](../netplay/02-delivery-method-reference.md).

### 4. Reliable is always ordered

There is no `ReliableUnordered` on `ISteamNetworkingSockets`. Messages currently using it
(`OnSpawnedEnemy`, `OnSpawnedProjectile`, `SendSpawnedObject`, `OnSpawnedChest`,
`OnFinalBossOrbsSpawned`) become reliable-ordered, which is correct but adds head-of-line
blocking. If that shows up in profiling, connection **lanes** are the answer — put spawn
traffic on its own lane so it cannot block gameplay events.

### 5. Marshalling and allocation on the send path

Multibonk's send path pins a `byte[]` per message:

```csharp
var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
try { SteamNetworkingSockets.SendMessageToConnection(conn, handle.AddrOfPinnedObject(), (uint)len, 0, out long _); }
finally { handle.Free(); }
```

At our message rate that is a lot of pinning. Prefer a pre-allocated pinned buffer reused
across sends, or `SendMessages` (the batch variant) for the per-tick enemy delta.

### 6. Connection lifecycle is callback-driven, receive is poll-driven

`SteamNetConnectionStatusChangedCallback_t` fires from `RunCallbacks()`; messages arrive via
`ReceiveMessagesOnConnection`. Both must be pumped every frame. Missing the callback pump
means connections never establish; missing the receive poll means messages queue silently.

### 7. `k_nSteamNetworkingConnectionEnd_*` reasons

Steam gives structured disconnect reasons. Map them to user-facing messages — this is a
strict improvement over the current generic "Host has disconnected", and it is where the
protocol version rejection surfaces.

---

## Testing

1. **Same machine, two Steam accounts** — requires two PCs or a second account with Family
   Sharing. Steam P2P will not loop back to itself.
2. **Under packet loss.** SDR hides a lot; `clumsy` / `tc netem` at 3% still matters for the
   reliability map.
3. **Symmetric NAT** — the case the current relay fallback exists to handle. SDR should make
   it a non-event. Verify explicitly.
4. **Proton** — full run on Linux. This is where the two-wrapper question is most likely to
   bite.
5. **Achievement/leaderboard suppression still works.** The mod deliberately blocks Steam
   writes during netplay (`Patches/SteamAchievementsManager.cs`,
   `Patches/SteamStatsManager.cs`, `Patches/LeaderBoards.cs`). Adding a second Steamworks
   wrapper is exactly the kind of change that could route around those patches. Test it
   before shipping — a player getting banned from the leaderboard is the worst possible
   regression here.

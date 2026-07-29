# LiteNetLib → `ISteamNetworkingSockets` Mapping

Reference for Phase 4 of [`00-migration-plan.md`](00-migration-plan.md).

Every mapping here is from Steamworks SDK documentation and the `Steamworks.NET` binding
surface. **Nothing was compiled or run** — verify signatures against the `Steamworks.NET`
version you actually reference.

---

## Delivery flags

The one table that must not be got wrong.

| `LiteNetLib.DeliveryMethod` | Steam `nSendFlags` | Notes |
|---|---|---|
| `ReliableOrdered` | `k_nSteamNetworkingSend_Reliable` (8) | Steam reliable is **always** ordered |
| `ReliableUnordered` | `k_nSteamNetworkingSend_Reliable` (8) | No unordered variant. Adds head-of-line blocking; use lanes if it matters |
| `Unreliable` | `k_nSteamNetworkingSend_Unreliable` (0) | |
| `Sequenced` | — | No equivalent. Sequence number + drop-if-older on `Unreliable` |
| `ReliableSequenced` | — | No equivalent. As above, on `Reliable` |

Additional flags, combinable with `|`:

| Flag | Value | Effect |
|---|---|---|
| `k_nSteamNetworkingSend_NoNagle` | 1 | Send immediately, do not wait to coalesce |
| `k_nSteamNetworkingSend_NoDelay` | 4 | Drop rather than queue if the connection is not ready. Implies `NoNagle` |
| `k_nSteamNetworkingSend_UseCurrentThread` | 16 | Skip the internal thread hop |

Convenience combinations the SDK defines:

```
k_nSteamNetworkingSend_UnreliableNoNagle  = Unreliable | NoNagle   = 1
k_nSteamNetworkingSend_UnreliableNoDelay  = Unreliable | NoDelay | NoNagle = 5
k_nSteamNetworkingSend_ReliableNoNagle    = Reliable   | NoNagle   = 9
```

### Mapping helper

```csharp
private static int ToSteamFlags(NetDelivery delivery) => delivery switch
{
    // Position/state ticks: send immediately, do not let Nagle add latency.
    NetDelivery.Unreliable       => Constants.k_nSteamNetworkingSend_UnreliableNoNagle,

    // One-shot events: let Nagle coalesce. This is the free "packet batching".
    NetDelivery.ReliableUnordered => Constants.k_nSteamNetworkingSend_Reliable,
    NetDelivery.ReliableOrdered   => Constants.k_nSteamNetworkingSend_Reliable,

    _ => Constants.k_nSteamNetworkingSend_Reliable,   // fail safe, not fail fast
};
```

Note the deliberate asymmetry: `NoNagle` on the unreliable path (latency matters, and the
next tick supersedes it anyway), plain `Reliable` on the event paths (coalescing is the
point). Call `FlushMessagesOnConnection` at the end of each network tick if you need a hard
boundary.

> **Do not pass `0` as a blanket flag.** `Multibonk/Networking/SteamNetworking.cs:580` does
> exactly this — `SendMessageToConnection(conn, ptr, (uint)len, 0, out long _)` — which is
> `Unreliable`. That is correct for its position-snapshot-only payload. It is wrong for
> anything in our message set except the position tick.

### Size limits

| Channel | Max message | Fragmentation |
|---|---|---|
| Reliable | 512 KB (`k_cbMaxSteamNetworkingSocketsMessageSizeSend`) | Yes, transparent |
| Unreliable | ~1200 bytes single-datagram | Fragmented **unreliably** — one lost fragment discards the message |

Anything with a list, a string, or an inventory snapshot is reliable-only. See
[`../netplay/02-delivery-method-reference.md`](../netplay/02-delivery-method-reference.md).

---

## Concept mapping

| LiteNetLib | Steam |
|---|---|
| `NetManager` | `SteamNetworkingSockets` (static) |
| `NetPeer` | `HSteamNetConnection` |
| `netManager.Start(port)` (host) | `CreateListenSocketP2P(nVirtualPort, 0, null)` → `HSteamListenSocket` |
| `netManager.Connect(endpoint, key)` | `ConnectP2P(ref SteamNetworkingIdentity, nVirtualPort, 0, null)` |
| `NatPunchModule` | — (SDR handles traversal) |
| relay server | — (SDR) |
| `EventBasedNetListener.PeerConnectedEvent` | `SteamNetConnectionStatusChangedCallback_t` → `k_ESteamNetworkingConnectionState_Connected` |
| `EventBasedNetListener.PeerDisconnectedEvent` | same callback → `ClosedByPeer` / `ProblemDetectedLocally` |
| `ConnectionRequest.Accept()` | `AcceptConnection(HSteamNetConnection)` |
| `ConnectionRequest.Reject(data)` | `CloseConnection(conn, reason, debugString, false)` |
| `peer.Send(bytes, DeliveryMethod)` | `SendMessageToConnection(conn, ptr, cb, nSendFlags, out long msgOut)` |
| `netManager.PollEvents()` | `ReceiveMessagesOnConnection(conn, ptrs, maxMessages)` + `SteamAPI.RunCallbacks()` |
| `peer.Ping` | `GetConnectionRealTimeStatus(...).m_nPing` |
| `peer.Disconnect()` | `CloseConnection(conn, reason, debugString, bEnableLinger)` |
| connection key validation | SteamID authentication (built in) |
| rendezvous server / match codes | `SteamMatchmaking` lobbies |

`nVirtualPort` is an application-level port, not a UDP port. Any small constant works;
Multibonk uses `1`. Pick one and keep it stable — it must match between host and client.

---

## Host setup

```csharp
using Steamworks;

// Once, at plugin startup — begins fetching the SDR ticket.
SteamNetworkingUtils.InitRelayNetworkAccess();

// Register the connection lifecycle callback BEFORE creating the socket.
private Callback<SteamNetConnectionStatusChangedCallback_t> statusChanged;
statusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);

// Host
HSteamListenSocket listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
```

## Client setup

```csharp
var identity = new SteamNetworkingIdentity();
identity.SetSteamID(hostSteamId);

HSteamNetConnection hostConn = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
```

## Connection lifecycle

```csharp
private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t cb)
{
    var conn  = cb.m_hConn;
    var info  = cb.m_info;
    var state = info.m_eState;

    switch (state)
    {
        case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
            if (isHost)
            {
                // Optional: gate on lobby membership before accepting.
                var result = SteamNetworkingSockets.AcceptConnection(conn);
                if (result != EResult.k_EResultOK)
                {
                    Plugin.Log.LogWarning($"AcceptConnection failed: {result}");
                }
            }
            break;

        case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
            RegisterPeer(conn, info.m_identityRemote.GetSteamID());
            break;

        case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
        case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
            Plugin.Log.LogInfo($"Connection closed: {info.m_eEndReason} — {info.m_szEndDebug}");
            UnregisterPeer(conn);
            // Always close your side, even when the peer closed first, or the handle leaks.
            SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
            break;
    }
}
```

`AcceptConnection` is the natural place for the protocol version gate's second line of
defence — the first being the lobby-metadata filter, which stops incompatible clients before
they ever connect. See [P1-3](../netplay/01-critical-fixes.md#p1-3).

## Sending

```csharp
private readonly byte[] sendScratch = new byte[65536];
private GCHandle sendHandle;   // pinned once at Initialize(), freed at Shutdown()

private void SendRaw(HSteamNetConnection conn, ReadOnlySpan<byte> payload, int steamFlags)
{
    if (conn.m_HSteamNetConnection == 0 || payload.Length == 0) return;

    payload.CopyTo(sendScratch);
    var result = SteamNetworkingSockets.SendMessageToConnection(
        conn, sendHandle.AddrOfPinnedObject(), (uint)payload.Length, steamFlags, out long _);

    if (result != EResult.k_EResultOK)
    {
        Plugin.Log.LogWarning($"SendMessageToConnection failed: {result}");
    }
}
```

Reusing one pinned buffer avoids the per-message `GCHandle.Alloc`/`Free` pair that Multibonk
does. For the per-tick enemy delta, consider `SendMessages` (the batch variant) instead.

**Always check the `EResult`.** LiteNetLib's `Send` is fire-and-forget; Steam's returns a
status, and swallowing it is how "why did nothing arrive" bugs happen.

## Receiving

```csharp
private const int MaxMessagesPerPoll = 64;
private readonly IntPtr[] messagePtrs = new IntPtr[MaxMessagesPerPoll];

public void Poll()
{
    SteamAPI.RunCallbacks();   // see Gotcha 1 in the migration plan re: double-pumping

    foreach (var conn in AllConnections())
    {
        int n;
        while ((n = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, messagePtrs, MaxMessagesPerPoll)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(messagePtrs[i]);
                var buf = new byte[msg.m_cbSize];              // TODO: pool this
                Marshal.Copy(msg.m_pData, buf, 0, msg.m_cbSize);

                HandleMessage(conn, buf, msg.m_cbSize);

                // MUST release, or Steam leaks the message.
                SteamNetworkingMessage_t.Release(messagePtrs[i]);
            }
        }
    }
}
```

Three things that will bite:

- **`SteamNetworkingMessage_t.Release` is mandatory.** Forgetting it leaks native memory on
  every message.
- **The `byte[] buf` allocation is per message.** At our rate that is significant GC pressure
  — pool it, or deserialize directly from `msg.m_pData` with an unmanaged `MemoryPack` reader.
- **Call `Poll()` from Unity's `Update`.** That is what makes all handlers main-thread. See
  [`00-migration-plan.md`](00-migration-plan.md#why).

## Statistics

```csharp
SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, null);

status.m_nPing;                  // ms
status.m_flConnectionQualityLocal;
status.m_flOutPacketsPerSec;
status.m_flOutBytesPerSec;
status.m_cbPendingReliable;      // send queue depth — rising means you are over budget
status.m_cbSentUnackedReliable;
```

`m_cbPendingReliable` is the single most useful number for tuning: if it grows during a final
swarm, you are sending more than the link can carry and latency will climb.

## Lanes

Optional, but the legitimate route to the bandwidth reduction the Sea-Bass fork was chasing:

```csharp
// lane 0: gameplay events (high priority, small)
// lane 1: enemy/projectile state (low priority, bulk)
SteamNetworkingSockets.ConfigureConnectionLanes(conn, 2,
    new int[] { 0, 1 },        // priorities — lower number = higher priority
    new ushort[] { 1, 4 });    // relative weights for bandwidth share
```

Then pass the lane index in the `SendMessages` path. Bulk enemy state can no longer delay a
`ChestOpened`, and reliable-ordered head-of-line blocking is scoped per lane — which also
recovers what is lost by mapping `ReliableUnordered` onto `Reliable`.

---

## Lobbies (replaces the rendezvous server)

```csharp
// Create
SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, maxMembers);
// → LobbyCreated_t

// Advertise
SteamMatchmaking.SetLobbyData(lobbyId, "name", SteamFriends.GetPersonaName() + "'s Lobby");
SteamMatchmaking.SetLobbyData(lobbyId, "proto", Protocol.Version.ToString());
SteamMatchmaking.SetLobbyData(lobbyId, "mode", modeName);

// Browse — filter by protocol version so incompatible builds never appear
SteamMatchmaking.AddRequestLobbyListStringFilter("proto", Protocol.Version.ToString(),
    ELobbyComparison.k_ELobbyComparisonEqual);
SteamMatchmaking.RequestLobbyList();
// → LobbyMatchList_t (CallResult, not Callback)

// Join
SteamMatchmaking.JoinLobby(lobbyId);
// → LobbyEnter_t

// Who is the host
CSteamID owner = SteamMatchmaking.GetLobbyOwner(lobbyId);

// Members
int n = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
CSteamID member = SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, i);
```

Callbacks to register:

| Callback | Purpose |
|---|---|
| `LobbyCreated_t` | host: lobby is up, store the ID |
| `LobbyEnter_t` | client: joined, now `ConnectP2P` to the owner |
| `LobbyDataUpdate_t` | metadata changed |
| `LobbyChatUpdate_t` | member joined/left |
| `GameLobbyJoinRequested_t` | **friends-list "Join Game"** — the headline UX win |

`LobbyMatchList_t` is a `CallResult<T>`, not a `Callback<T>`. Mixing them up is a common
first-time error.

Also set presence so friends see something useful:

```csharp
SteamFriends.SetRichPresence("status", "In Lobby");
SteamFriends.SetRichPresence("connect", $"+connect_lobby {lobbyId.m_SteamID}");
```

---

## Disconnect reasons

Map `info.m_eEndReason` to user-facing text instead of the current generic
"Host has disconnected":

| Range / constant | Meaning |
|---|---|
| `k_ESteamNetworkingConnectionEnd_App_Min`..`Max` (1000–1999) | **Your** reasons — use these for version mismatch, kick, lobby full |
| `k_ESteamNetworkingConnectionEnd_AppException_Min`..`Max` (2000–2999) | Your errors |
| `k_ESteamNetworkingConnectionEnd_Local_*` | Local network problem, no SDR route |
| `k_ESteamNetworkingConnectionEnd_Remote_Timeout` | Peer stopped responding |
| `k_ESteamNetworkingConnectionEnd_Misc_*` | Steam infrastructure |

Reserve a couple of app codes up front:

```csharp
const int EndReason_ProtocolMismatch = 1001;
const int EndReason_LobbyFull        = 1002;
const int EndReason_Kicked           = 1003;
```

`CloseConnection(conn, EndReason_ProtocolMismatch, "protocol v3 required, client sent v2", false)`
gives the client an actionable message — a strict upgrade over today.

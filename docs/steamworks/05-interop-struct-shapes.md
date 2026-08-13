# Which Steamworks structs are safe to touch, and why

Written at the start of Phase 4, because the standing rule — *never call a Steamworks method that
takes a struct by reference* — blocks `ConnectP2P`, which is the one call a client cannot do
without. Either Phase 4 stops here or the rule gets understood well enough to say where its edge
is. This is that.

**The rule is not wrong. It is a safe over-approximation of a narrower rule**, and the narrower one
is checkable offline against the game's own interop assembly.

## The real failure mechanism

Two crashes were charged to "struct by reference". Both were `SteamRelayNetworkStatus_t`. The
mechanism is not the by-ref calling convention — it is a **layout mismatch**:

```
native  SteamRelayNetworkStatus_t { int; int; int; int; char m_debugMsg[256]; }   ~272 bytes
IL2CPP  SteamRelayNetworkStatus_t { int; int; int; int; Il2CppStructArray<byte>; }  ~24 bytes
```

Il2CppInterop cannot express an inline `char[256]`, so it emits a **reference** where native has
256 inline bytes. Steam then writes 272 bytes into a 24-byte allocation. Reading the field
afterwards dereferences whatever landed in the pointer slot. That is why it crashed reading the
field, and why it still crashed once the read was removed and only the call remained: the
overflow happens during the call.

So the rule that actually holds is:

> **Never pass by reference a Steamworks struct whose IL2CPP layout differs from its native
> layout.** In practice that means: any struct that has an `Il2CppStructArray<T>` field, directly
> or in a nested struct, because native has inline bytes there and IL2CPP has a pointer.

That is decidable without running anything. Every field of every one of these types is in
`BepInEx/interop/com.rlabrecque.steamworks.net.dll`'s metadata.

## The audit

Read out of the game's interop assembly on buildid 21750826. **CONFIRMED** — this is metadata, not
inference. Every row was additionally compiled against that assembly.

| Struct | IL2CPP layout | By-ref safe? |
|---|---|---|
| `SteamNetworkingIdentity` | `int m_eType; int m_cbSize; uint m_reserved0..31` — 136 B, all inline | **Yes** |
| `SteamNetworkingMessage_t` | `IntPtr m_pData; int m_cbSize; HSteamNetConnection m_conn; SteamNetworkingIdentity m_identityPeer; …` — all inline | **Yes** |
| `SteamNetworkingConfigValue_t` | blittable; `Il2CppStructArray<SteamNetworkingConfigValue_t>` constructs | **Yes** |
| `SteamRelayNetworkStatus_t` | `Il2CppStructArray<byte> m_debugMsg_` | **No** — the crash |
| `SteamNetAuthenticationStatus_t` | same shape | **No** |
| `SteamNetworkingIPAddr` | `Il2CppStructArray<byte> m_ipv6` | **No** |
| `SteamNetConnectionInfo_t` | contains `SteamNetworkingIPAddr`, plus `Il2CppStructArray<byte>` ×2 and `Il2CppStructArray<uint>` | **No** |
| `SteamNetConnectionRealTimeStatus_t` | `Il2CppStructArray<uint> reserved` | **No** |
| `SteamNetConnectionStatusChangedCallback_t` | contains `SteamNetConnectionInfo_t` | **No** |

`SteamNetworkingIdentity` having thirty-two separate `uint m_reservedN` fields rather than one
array is the whole reason it is safe. Il2CppInterop unrolled the union, so the inline 128 bytes
survived as 128 inline bytes.

## What that permits, and what it costs

**Permitted — the entire connection path.** Nothing on it is layout-broken:

| Call | Shape | Note |
|---|---|---|
| `CreateListenSocketP2P(int, int, Il2CppStructArray<…>)` | no by-ref struct | pass a zero-length array, not null |
| `ConnectP2P(ref SteamNetworkingIdentity, int, int, …)` | by-ref, layout matches | the call this document exists for |
| `AcceptConnection`, `CloseConnection`, `CloseListenSocket` | handles and primitives | |
| `CreatePollGroup`, `DestroyPollGroup`, `SetConnectionPollGroup` | handles | |
| `ReceiveMessagesOnPollGroup(h, Il2CppStructArray<IntPtr>, int)` | array of pointers | reuse one array |
| `SendMessageToConnection(h, IntPtr, uint, int, out long)` | `out long` is a primitive | |
| `FlushMessagesOnConnection` | handle | |
| `SteamNetworkingMessage_t.FromIntPtr` / `.Release(IntPtr)` | pointer in, blittable copy out | |
| `GetIdentity(out SteamNetworkingIdentity)` | layout matches | see the probe below |

**Denied, and each costs something concrete:**

| Call | Lost | Replacement |
|---|---|---|
| `GetConnectionRealTimeStatus` | ping, packet loss, send-queue depth | **`GetLatency` returns -1 on this transport.** The lobby panel's `rtt` goes blank |
| `GetConnectionInfo` | polling a connection's state | the status callback carries it |
| reading `SteamNetConnectionStatusChangedCallback_t` as a struct | the whole connection lifecycle | read the native payload with `Marshal` at fixed offsets — below |

Losing ping is a real regression against the LiteNetLib path and should not be quietly accepted
forever. The route back is Gotcha 1 option (c): direct P/Invoke to
`SteamAPI_ISteamNetworkingSockets_GetConnectionRealTimeStatus` with **our own** blittable struct
declaration, which creates no second callback registry because this interface has no callbacks of
its own. Not done here — it is a diagnostic, and one unverified native-binding assumption per
branch is enough.

## Reading the status callback payload

`SteamCallback` delivers the raw native pointer, which is the shape we want anyway — the same one
`SteamService` already reads `SteamRelayNetworkStatus_t` through, verified in game. Offsets are
derived from the SDK layout, not from `dump.cs`: **`dump.cs` records the IL2CPP layout, which for
this struct is the broken one.**

```
SteamNetConnectionStatusChangedCallback_t
  +0    HSteamNetConnection      m_hConn
  +4    (padding — m_info aligns to 8, it contains an int64)
  +8    SteamNetConnectionInfo_t m_info

SteamNetConnectionInfo_t (relative to +8)
  +0    SteamNetworkingIdentity  m_identityRemote      136
          +0  m_eType            +4  m_cbSize          +8  m_steamID64
  +136  int64                    m_nUserData             8
  +144  HSteamListenSocket       m_hListenSocket         4
  +148  SteamNetworkingIPAddr    m_addrRemote           18
  +166  uint16                   m__pad1                 2
  +168  SteamNetworkingPOPID     m_idPOPRemote           4
  +172  SteamNetworkingPOPID     m_idPOPRelay            4
  +176  ESteamNetworkingConnectionState m_eState         4
  +180  int32                    m_eEndReason            4
  +184  char                     m_szEndDebug[128]
  +312  char                     m_szConnectionDescription[128]
  +440  int32                    m_nFlags
  +444  uint32                   reserved[63]
```

`m__pad1` existing in the SDK at all is the corroboration that `m_addrRemote` is 18 bytes and the
next field lands on 168 — the SDK author closed that gap by hand.

**Absolute offsets used:** `m_hConn` 0, remote `m_eType` 8, remote `m_cbSize` 12, remote SteamID
16, `m_eState` 184, `m_eEndReason` 188.

**These are checked at runtime, not trusted.** Every payload is validated before it is acted on:
`m_eType` must be `k_ESteamNetworkingIdentityType_SteamID` (16) and `m_cbSize` must be 8. Both sit
in front of the arithmetic that could be wrong, so if the base is off they will not hold. A
payload that fails the check is logged once and dropped rather than acted on — a connection that
never establishes is a bug report; a connection acted on from garbage is a crash.

## What has actually been run

**CONFIRMED in game on buildid 21750826, 2026-08-12**, by `SteamNetSelfTest` in a single-player
session:

```
[steam-net] Self-test: bringing the transport up as a host.
[steam-net] Identity layout verified: Steam filled in 76561198045461149, which matches the game's own SteamID.
[steam-net] Listening on virtual port 0.
[steam-net] Self-test: Running, hosting, 0 peer(s).
[steam-net] Shut down.
[steam-net] Self-test finished: PASSED
```

**The first line of that is the one that matters, and it closes the question this document opened.**
`GetIdentity` filled a `SteamNetworkingIdentity` through native code and it read back as the SteamID
the game already knew. The 136-byte layout round-trips, so passing that struct to `ConnectP2P` is
standing on measured ground rather than on a metadata argument. The audit table above is no longer
the only evidence for it.

Three more things came up with it and are also confirmed:

- `CreateListenSocketP2P` and `CreatePollGroup` bind through the game's own interop assembly with a
  zero-length `Il2CppStructArray` for options.
- `CallbackDispatcher.Register` accepts a `SteamCallback` carrying
  `SteamNetConnectionStatusChangedCallback_t` — the first use of that mechanism for a type in the
  sockets callback range.
- Teardown releases the connection, poll group and listen socket without crashing, and adds nothing
  to Steam's own `ErrorLog.log`, which came out byte-identical to the run before the self-test
  existed.

## What is still unverified, and needs two machines

1. **The callback registering is not the callback firing.** Nothing connected, so
   `SteamNetConnectionStatusChangedCallback_t` has never been delivered. If the callback id derived
   from the type's attribute is wrong, the symptom is silence — a connection that hangs in
   `Connecting` forever with nothing in the log.
2. **The offsets above have never been read from a real payload.** The identity sentinels sit in
   front of the arithmetic so a wrong base is loud rather than silent, which is the most that can be
   arranged without a peer.
3. **The receive path and the delivery mapping are entirely untouched.** Steam has no loopback, so
   one player cannot send a byte to themselves.

## What this does not change

Everything the handover said still holds. `SteamRelayNetworkStatus_t` and
`SteamNetAuthenticationStatus_t` stay out of reach by reference forever — they are in the table
above as **No**, and `SteamService` already reads them the safe way. Nothing here is a licence to
revisit those; it is a licence to pass the one struct that was never the problem.

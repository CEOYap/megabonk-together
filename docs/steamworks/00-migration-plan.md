# Steamworks.NET Migration Plan

Moving from LiteNetLib + NAT punchthrough + self-hosted rendezvous to
[`Steamworks.NET`](https://github.com/rlabrecque/Steamworks.NET) `ISteamNetworkingSockets`.

Reference the [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) source and the
[official Steamworks documentation](https://steamworks.github.io/) directly. Where this document
says "another implementation for this game", that is a deliberate anonymisation — do not name a
mod or its author here or in a commit.

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
- **No callback registry of our own.** Phase 2 took the game's own Steamworks.NET rather than
  shipping a second managed wrapper, because two wrappers over one manual-dispatch pipe would
  consume each other's callbacks. Everything later phases would have taken from a `Callback<T>`
  has to be polled or routed through a config-value function pointer instead. See
  [Gotcha 1](#gotcha-1).

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
- ~~Land P0-1, P0-2, P0-4, P0-3, P1-3.~~ P0-1, P0-2, P0-3, P0-4 are landed (also P0-5, P1-7,
  P2-1, P2-4).
- **P1-3 is not a prerequisite and cannot be one — it is now a *deliverable of this
  migration*.** The version gate was attempted on the LiteNetLib path and disproved in-game:
  `ConnectionRequestEvent` never fires after NAT introduction, because both peers `Connect`
  at each other and LiteNetLib reconciles the cross-connect internally. See P1-3 in
  [`../netplay/01-critical-fixes.md`](../netplay/01-critical-fixes.md) for the evidence. The
  gate lands in Phase 3 as lobby metadata instead, where it also covers relayed sessions.
- ~~Add per-message-type byte counters~~ — **done.** `BandwidthDiagnostics` reports KB/s, sends/s
  and B/send per message type. The two `LobbyUpdates` senders are labelled by hand
  (`LobbyUpdates(players)` / `LobbyUpdates(enemies)`) because they share a type and the merged
  bucket could not attribute the traffic it was being blamed for.
- **Latency: partial.** `rtt` is reported per peer and works on the direct path (65 ms measured on
  an internet session). There is no packet-loss readout — LiteNetLib does not expose one, and
  `GetConnectionRealTimeStatus` supplies it for free after Phase 4. Do not build one now.

#### Exit criteria — revised, because 4 and 6 players cannot be played

The original criterion was *"a baseline bandwidth profile recorded at 2 / 4 / 6 players"*. **The
project has two players available and no route to more.** Left as written, this blocks the
migration permanently on something nobody can do, so it is split into the part that can be
measured and the part that has to be accepted.

1. **Charging bugs fixed** — done, verified in-game.
2. **2-player profile recorded in-game** — done. Host egress median 20.8 KB/s, p90 37.5, max 98.6
   (down from 32.1 / 97.3 / 321.1 before the stream work), with per-stream attribution.
3. **4 and 6 players: derive, do not play.** Bandwidth is payload x rate x peers, and the payloads
   are measurable directly by serializing representative records against the real MemoryPack
   serializer — no session required. That method is trustworthy here rather than merely convenient:
   it predicted `PlayersStateUpdate` at 98 B/send and the shipped build measured **exactly 98**.
   Current derived figures are in the `PlayersStateUpdate` summary; the 6-player full `Player`
   record is **1628 B**, over `MAX_PACKET_SIZE_BYTES`, which is why the player stream was split.
4. **What derivation cannot cover, recorded as accepted risk rather than as a task.** Six-player
   *behaviour* — CPU across six interpolators, enemy-cap scaling, encounter-barrier contention
   with six reporters, and the budgeted enemy stream's per-tick cap actually binding — has never
   been exercised and will not be before the migration. Phase 4 should therefore not claim
   6-player parity, only 2-player parity plus derived bandwidth.

> **Do not quietly re-add "capture at 6 players" to a later phase.** It was carried as an open
> blocker across three handovers before anyone wrote down that it is not achievable.

> **Knock-on:** P1-2 (golden shrine sync) changes the wire format and was blocked behind P1-3.
> It is therefore blocked behind this migration too — or it ships accepting that a
> version-mismatched pair desyncs silently instead of refusing to connect.

### Phase 1 — Introduce the seam (no behaviour change) — **DONE and playtested**
- ~~Add `INetTransport` and `NetDelivery`.~~ `src/plugin/Services/INetTransport.cs`,
  `NetDelivery.cs`.
- ~~Make `UdpClientService` implement it; map `NetDelivery` → `LiteNetLib.DeliveryMethod`.~~
  `IUdpClientService : INetTransport`; the mapping is the single `ToLiteNetLib` switch.
- ~~Replace `SendToClient(NetPeer, ...)` with `SendToClient(uint connectionId, ...)`.~~ Also
  `SendToAllClientsExcept(int, uint, T)` → `(uint excludedConnectionId, T)`.
- ~~Decide the `RelayEnvelope.ToFilters` question.~~ Ported deliberately, both defects collapsed.
- **Exit criteria met:** no `LiteNetLib` *type* appears outside `UdpClientService` — the only
  remaining mention anywhere else is a string literal in a bandwidth log line. Builds clean.
- **Playtested 2026-08-08**, two instances on one PC. A full run start-to-finish: both barriers
  worked (`[readiness]` round 1 completed, `[barrier]` report/release/apply on both peers),
  `EncounterClosedStamped` and `ClientReadyStamped` both observed on the wire, and per-stream
  bandwidth shape unchanged. **This validates function, not bandwidth:** `rtt 0 ms` on one machine
  is not a network test, and the session was too short to compare against Phase 0's
  median 24.1 / p90 64.9 / max 201.3 KB/s internet baseline. A regression check against that
  baseline still wants an internet session.

**Deviation from the plan as written, and why.** The plan named the implementation
`LiteNetTransport`. It stayed `UdpClientService`: that class is more than a transport — it also owns
matchmaking handshake, NAT introduction, relay fallback and the per-tick send loop — so renaming it
would have asserted a separation that does not exist yet, while touching every DI and call site in a
phase whose whole value is being a provable no-op. The separation that *does* matter is expressed as
the interface split: gameplay code depends on `INetTransport`, and `IUdpClientService` carries the
session lifecycle. Phase 5 can extract the class once the Steam implementation shows where the real
seam falls.

**One deliberate behaviour change**, which makes this not a pure no-op and should be watched in the
playtest. `SendToAllClientsExcept` used to exclude *the peer the packet arrived on* (a
`NetPeer.Id`); it now excludes *the player that originated the message* (a connection id). These
coincide in the star topology the mod runs, which is why the old code worked. Where they could
diverge, the new behaviour is the correct one — the exclusion exists so an originator does not
receive its own delta back (P1-1).

**What Phase 1 hands Phase 4** — read `NetDelivery`'s remarks before writing the Steam mapping.
Steam's flags (`k_nSteamNetworkingSend_*`, [ISteamNetworkingSockets](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets),
via [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET)) offer only `Reliable` (always
ordered) and `Unreliable`, plus `NoNagle` / `NoDelay`. So:

| `NetDelivery` | Steam | Consequence |
|---|---|---|
| `Unreliable` | `k_nSteamNetworkingSend_Unreliable` | exact |
| `ReliableOrdered` | `k_nSteamNetworkingSend_Reliable` | exact, and Steam fragments |
| `ReliableUnordered` | `Reliable` | **degrades** — regains the head-of-line blocking it exists to avoid |
| `ReliableSequenced` | *none* | **no equivalent**; neither flag means "only the newest is guaranteed" |

`ReliableSequenced` has exactly one user — the implicit default on the client's `Introduced`
handshake, which is a one-shot that arguably should never have been on that channel. **Settle that
call site before choosing a mapping**, rather than picking a lossy translation by default.

Also recorded on `INetTransport`: sends return `void`, so a Steam implementation must log its own
`EResult` failures — throttled, with a suppressed count, naming the message type and target.
`SendMessageToConnection` can fail where `NetPeer.Send` could not, and these paths reach 233
sends/s, so an unthrottled log would cost more than the sends.

This is the largest mechanical step and the one most likely to introduce a regression. Do it
alone, and playtest it as a no-op change before continuing.

#### The two-id-space collapse, and `ToFilters`

Today's signature takes **two ids in different spaces**:

```csharp
void SendToAllClientsExcept<T>(int netPlayerId, uint sender, T data)
//                             ^ LiteNetLib NetPeer.Id   ^ game connection id
```

`netPlayerId` filters direct peers (`gamePeers.Where(p => p.Value.Id != netPlayerId)`);
`sender` builds `RelayEnvelope.ToFilters` for relayed peers. Transposing them is easy and
silent — it already cost one wrong fix attempt on P1-1, where passing `OwnerId` for both would
have echoed a gold *delta* back to its originator and double-counted it.

The new `SendToAllClientsExcept(uint excludedConnectionId, ...)` takes one id in one space,
which makes that mistake unrepresentable. Good — but the mapping does not vanish, it moves
inside `LiteNetTransport`, which must still resolve a connection id to a `NetPeer.Id` for the
direct path and to a `ToFilters` entry for the relay path.

**`ToFilters` is no longer an open question.** It was traced end to end in Phase 0 — both plugin
branches and the server's forwarding — and is **correct**: each id is used only against the map
that speaks it, and the empty-filter fallback is safe because a `sender` that misses the lookup
is by definition a direct peer, and direct peers are not in the relay session's client set. Full
trace: [`../netplay/01-critical-fixes.md`](../netplay/01-critical-fixes.md#p1-1) under *Sender
exclusion in relay mode*.

Two defects were found and left in place, because they change nothing today and this phase is
where they get rewritten anyway. **Do not port either forward:**

1. The `toExcept` lookup is an identity function (`dict[sender].ConnectionId == sender` by
   construction) with a fallback that scans the same dictionary on the field that equals its key
   — a LINQ linear scan that can never produce a different answer.
2. `0` is the "not found" sentinel and is also a legal connection id, since
   `ConnectionIdPool.NewId()` draws a random `uint` and excludes only ids in use. It currently
   recovers by accident.

Both collapse to one `TryGetValue` and no sentinel in the new signature.

Two acceptable outcomes for the branch itself, either is fine:

1. Port it, with the two defects above collapsed rather than carried.
2. Delete the relay branch as part of this phase rather than porting a mechanism Phase 5
   removes anyway — provided relay fallback is still needed until SDR lands in Phase 4, which
   it is, so this really means "port it deliberately, not by copy-paste".

What is **not** acceptable is carrying it across the seam unexamined, because after Phase 1 the
call sites no longer show the id-space split that makes the hazard visible.

### Phase 2 — Steam plumbing (no transport change yet) — **BUILT, NOT YET PLAYTESTED**
- ~~Reference `Steamworks.NET`.~~ The game's own
  `BepInEx/interop/com.rlabrecque.steamworks.net.dll`, not a copy we ship. See
  [Gotcha 1](#gotcha-1) — the recommendation there was reversed on decompiled evidence.
- ~~Verify Steam is already initialised by the game; **do not** call `SteamAPI.Init()`.~~ Done,
  and the check is now made at runtime rather than by reading the log. `SteamManager.IsInitialized`
  returns the same static byte `SteamManager.Update` tests before pumping callbacks, so it means
  Steam is up *and* being serviced. `ISteamService` gates every call on it.

  The caveat that nearly cost a phase still stands and is now handled rather than remembered: the
  assumption holds *only when the game is launched through Steam* — `[Message: Unity] Steam
  initialized`. Launched by running `Megabonk.exe` directly, the same build logs
  `[Error : Unity] [Steamworks.NET] SteamAPI_Init() failed` and runs with Steam down.
  [`../netplay/05-local-testing.md`](../netplay/05-local-testing.md)'s two-instance harness runs
  its second copy direct-from-exe by necessity, so **that instance has no Steam and cannot
  validate this phase**. It now logs that it is in that state instead of failing.
- ~~Call `SteamNetworkingUtils.InitRelayNetworkAccess()` at plugin startup.~~ Done — but **not at
  plugin startup, because that is impossible**. `SteamManager` initialises from a
  `RuntimeInitializeOnLoadMethod(AfterSceneLoad)`, which runs after BepInEx loads plugins; at
  `Plugin.Load` time Steam's interface pointers are still null and calling through one is a native
  access violation. `Scripts/SteamStatusTicker.cs` retries at 1 Hz until the gate opens, then
  stops. `InitAuthentication` is requested in the same place, per [6a](#gotcha-6a).
- ~~Add a debug command that prints `SteamUser.GetSteamID()` and SDR relay status.~~ There is no
  console in this mod, so it is a `Diagnostics.LogSteamStatus` config toggle printing every 10s,
  matching `LogAllocationRate` and `LogBandwidth`. It names which half of SDR is missing, which
  separates "still measuring pings" from "cannot reach a relay at all".
- **Exit criteria, none of them met yet:** the mod loads, Steam calls succeed, and
  achievements/leaderboards still behave exactly as before (patched in
  `Patches/SteamStatsManager.cs`, `Patches/LeaderBoards.cs`).

This phase is where an IL2CPP/Steamworks conflict will surface. Do not proceed until the game
is demonstrably stable.

**The one thing to watch first.** `GetRelayNetworkStatus` and `GetAuthenticationStatus` both take
an `out` struct and have no overload that does not. Both structs carry a managed `byte[]` for
their debug message, so Il2CppInterop generates them as `ValueType`-derived proxy classes rather
than blittable structs, and an `out` parameter of one is a shape this project has never exercised.
If it is wrong it will be wrong *natively* — a crash with no managed stack trace, not the
exception `SteamService` catches. The readiness gate deliberately reads the **return value**
instead of `pDetails.m_eAvail`, which Steam documents as the same value, so that a bad read of the
struct degrades the diagnostics rather than the decision. If it does crash, the fallback is direct
P/Invoke to the flat C API for these two calls only — see option (c) in [Gotcha 1](#gotcha-1).

### Phase 3 — `SteamLobbyService` — **PARTLY DONE. Read the exit criterion before believing otherwise.**

**Verified over the internet with two accounts, both directions, zero mod errors on either side.**
Steam lobbies, discovery by code, the protocol gate, and invites all work:

```
[steam-invite] A friend invited us to lobby 109775241659443022.
[steam-lobby]  Joined lobby ..., code ULZVEZ, owner False, 2 member(s), protocol 1.
[steam-invite] Invite resolved to room code IFN1OZ.
[steam-invite] Opening the netplay menu to act on an invite.
[steam-invite] Joining room IFN1OZ from an invite.
```

| Item | State |
|---|---|
| Create/join lobbies via `SteamMatchmaking` | done, verified |
| `Protocol.cs` re-created, published and filtered on | done, verified — a joiner reads `protocol 1` back |
| Lobby list search by code, filtered on version | done, verified |
| `GameLobbyJoinRequested_t` | done, verified — invites accepted mid-session |
| `+connect_lobby` launch invites | done, verified |
| `SetRichPresence` connect string | done, verified (Join Game appears) |
| Host name as lobby data | done |
| Both players in one Steam lobby | done — host creates, client follows by room code |
| Roster and readiness from Steam member data | **tried and reverted** — see below |
| Retire tags 73 and 74 | **not done, and blocked until Phase 4** |
| Persona names | done, **unverified** — cache plus `PersonaStateChange_t` |
| Player count and mode as lobby data | **not done** — no lobby browser needs them yet |
| `LobbyDataUpdate_t` | **not needed** — the panel already refreshes twice a second, and member data is read on that tick |
| **Exit criterion: find and join a lobby without the rendezvous server** | **NOT MET, and cannot be met before Phase 4** |

**What actually shipped is a bridge, not the replacement.** The Steam lobby carries the WebSocket
matchmaker's room code as lobby data (`mt_mm_code`); discovery and invites are Steam's, and the
session itself is still matched and carried by the rendezvous server exactly as before. That was
deliberate — it makes invites work without touching the transport — but it means Phase 3's own
exit criterion is unmet and the server cannot be decommissioned yet.

**Why the exit criterion cannot be met in Phase 3, and should be moved.** It reads "players can
find and join a lobby without the rendezvous server", with the old transport still carrying
gameplay. Finding is done — Steam does discovery, codes and invites. *Joining* is not, and cannot
be: the rendezvous server is not only a matchmaker, it performs the NAT introduction that lets two
LiteNetLib peers reach each other at all, and it relays when they cannot. Nothing short of SDR
replaces that. **This criterion belongs to Phase 4**, and carrying it here makes Phase 3 look
permanently unfinished for a reason that has nothing to do with lobbies.

#### Retiring tags 73 and 74 was attempted and reverted. Do not retry it this way

Readiness was moved onto Steam lobby member data, with a guard: use Steam only when the Steam
lobby's member count equals the replicated roster's, and fall back to the messages otherwise. It
was playtested and broke readiness outright — a client pressing Ready did nothing the host could
see.

**The guard was evaluated independently on each machine.** Those two membership sets are filled by
different mechanisms at different moments — a host sitting alone has an empty roster and one Steam
member — so the two ends could land on different answers. A client on the Steam path wrote its
readiness into member data and sent nothing; a host on the message path read a set nobody had
written. Neither end could tell. The same comparison could flip between calls on one machine as the
roster filled, so even the client's own label sometimes did not move.

**The lesson is not "write a better guard".** Any rule derived separately on each machine can
disagree, and readiness is a correctness property — the Start button is gated on it, and the
failure mode is a host starting a run with somebody who never readied. There is no safe way to run
two readiness mechanisms side by side while the roster and the Steam lobby are separate sets.

So this waits for Phase 4, when the Steam lobby *is* the session and there is one membership set to
consult. At that point member data is the only mechanism and there is nothing to switch between.

What remains genuinely open:

1. `character` / `skinType` / `hat` as member data, alongside `ready`. Same shape, same mechanism;
   they are simply not moved yet.
2. Player count and mode as lobby data, when something wants a browser.
3. `mt_mm_code` disappears when the Steam lobby *is* the session, which is Phase 4's business.
4. **`ModConfig.PlayerName` is slated for removal in Phase 5.** The Steam persona is already
   adopted onto it at startup, so the config entry is a vestige — it exists for the name box in the
   netplay menu and for instances with no Steam. Removing it means deciding what a non-Steam
   instance is called, which matters because the two-instance test harness runs its second copy
   without Steam.

- ~~Handle `GameLobbyJoinRequested_t` (friends-list "Join Game")~~ and `LobbyDataUpdate_t`.
- Filter the lobby list by protocol version — this is [P1-3](../netplay/01-critical-fixes.md#p1-3)
  in its final form, and the **only** form of it that works. Three carried-over decisions:
  - `src/common/Protocol.cs` was written and then reverted with the failed LiteNetLib attempt.
    **Re-create it here**; it does not currently exist.
  - **Treat a lobby publishing no version as incompatible**, not as compatible-by-default.
    That is the only way a new build refuses an old one — and it means the first release
    carrying the gate cannot join any earlier version's lobby, which needs a loud changelog
    entry, not a quiet one.
  - **Do not key the gate off the plugin's semantic version.** Two releases differing only in
    gameplay or UI stay wire-compatible; bump the protocol number only when a type under
    `Messages/` changes.

#### Delete tags 73 and 74 here — do not port them

The lobby panel added three tags: **73** `LobbyReadyChanged`, **74** `LobbyReadyState`, **75**
`LobbyStartRequested`. Two of the three exist only because the current matchmaker has nothing
better, and Steam lobbies replace them with a platform feature.

Per-member state belongs in **lobby member data**
([`ISteamMatchmaking`](https://partner.steamgames.com/doc/api/ISteamMatchmaking)):

```csharp
SteamMatchmaking.SetLobbyMemberData(lobbyId, "ready", isReady.ToString());   // own row only
SteamMatchmaking.GetLobbyMemberData(lobbyId, memberId, "ready");             // on LobbyDataUpdate_t
```

| Tag | Fate at Phase 3 | Why |
|---|---|---|
| 73 `LobbyReadyChanged` | **delete** | becomes `SetLobbyMemberData(…, "ready", …)` |
| 74 `LobbyReadyState` | **delete** | becomes `GetLobbyMemberData` on `LobbyDataUpdate_t` |
| 75 `LobbyStartRequested` | **keep** | "the host says go" is an instruction, not replicated state, and has no member-data equivalent |

Three consequences worth having written down before the work starts:

1. **Host authority for readiness stops being ours to enforce.** Steam only lets a member write
   its own row, so "a client cannot set someone else's readiness" becomes a platform guarantee
   instead of something `LobbyViewService` maintains. That is a real simplification, not just a
   relocation.
2. **The optimistic-write reconciliation can go with it.** `pendingLocalReady` and its timeout
   exist because our set is host-mediated and a broadcast generated before the host saw a toggle
   can arrive after it. Single-writer member data has no such race: write locally, write the key,
   done.
3. **Character, skin and hat are the same shape.** They are per-member facts that today ride
   their own messages; member data would carry them the same way. Worth considering together
   rather than migrating readiness alone.

Tags 73 and 74 stay in the union permanently once shipped — tags are append-only and are never
renumbered or reused. "Delete" here means *stop sending and stop handling them*, leaving the
numbers burned, exactly as tags 1, 65 and 66 were left when their stamped replacements landed.
- Set `SteamFriends.SetRichPresence` for lobby/in-game status.
- **Exit criteria:** players can find and join a lobby without the rendezvous server. The old
  transport still carries gameplay traffic.

### Phase 4 — `SteamNetTransport` — **BUILT AND SETUP-PROVEN, NOT WIRED, NO PEER YET**

The transport itself exists: `Services/SteamNetTransport.cs` behind
`Services/ISteamNetTransport.cs`, with the marshalling it needs in `Services/SteamNetLayout.cs`.

| Item | State |
|---|---|
| Identity layout round-trips through native code | **verified in game** — `GetIdentity` returned our own SteamID |
| `CreateListenSocketP2P` + poll group on the host | **verified in game** — opens, listens, tears down clean |
| `ConnectP2P` on clients | built; its struct is now proven, the call is not |
| `SteamNetConnectionStatusChangedCallback_t` → `PeerConnected` / `PeerDisconnected` | built and **registers**; has never **fired** — no peer has connected |
| Send methods, throttled `EResult` logging, one reused pinned buffer | built |
| `ReceiveMessagesOnPollGroup` from `Update`, capped drain, `Release` in a `finally` | built |
| `NetDelivery` → Steam send flags | built; `ReliableSequenced` retired rather than translated |
| Behind a config flag so both transports ship in one build | **`ISteamNetTransport` is registered separately; `INetTransport` still resolves to LiteNetLib** |
| Session start, host and client, off the Steam lobby | built behind `Network/UseSteamTransport`; **never run** |
| **Exit criterion: a full run on Steam sockets, under 3% loss** | **not met — the Steam path has never carried a byte between two machines** |
| **Inherited from Phase 3: find *and join* without the rendezvous server** | built, unverified |

#### Confirmed in game, 2026-08-12

The full connection handshake, over the internet, two machines:

```
[steam-net] Connection 513257305 to 76561198042727765 is now …_Connecting.
[steam-net] Accepted a connection from 76561198042727765.
[steam-net] …_FindingRoute.  →  …_Connected.
[steam-net] Connected to 76561198042727765.
[steam-net] Suneo introduced as connection 82462037 (host: False).
```

That closes the last thing the single-player self-test could not reach: **the status callback fires
under the right callback id**, `ConnectP2P` works against the game's own interop assembly, SDR
routes, and the introduction handshake completes with the derived connection id accepted. Both
players saw each other in the lobby and reached character selection.

**Still unrun on Steam sockets:** everything past Start — the run itself, the per-tick streams, the
readiness barriers, disconnects mid-run.

**One known gap, deliberately left.** `MapController` locks the matchmaker lobby at Start so nobody
joins between pressing it and the run loading. There is no matchmaker here, and the Steam
equivalent — `SetLobbyJoinable(false)` — is not exposed by `ISteamLobbyService`, so the run starts
unlocked and a player could in principle join in that window.

#### How to test the Steam path

**Both players must set `Network/UseSteamTransport = true`.** A Steam host and a matchmaker client
cannot see each other at all — there is no negotiation and no fallback, deliberately, because a
silent fallback would put a player on a transport their config says they are not using.

Edit the config with the game closed; BepInEx rewrites it on exit. Then host on one machine, read
the room code off the lobby panel, and join with it on the other. What to watch for, in order:

```
[steam-session] Hosting; the lobby has been told the socket is open.
[steam-net] Accepted a connection from <steamid>.
[steam-net] <name> introduced as connection <id> (host: False).
[steam-session] Seed <n> taken from the lobby.        (client)
[steam-net] Connected to <steamid>.                    (client)
```

A client that sits at `Waiting` forever means the host never published `mt_ready` — check the host's
log for a listen-socket failure. A connection that reaches `Connecting` and stops means the
status callback is registered under the wrong id, which is the one thing the single-player self-test
could not prove.

**The struct-by-reference rule had to be understood before any of this could be written**, because
`ConnectP2P` takes one and a client cannot avoid it. The audit is
[`05-interop-struct-shapes.md`](05-interop-struct-shapes.md) and it is the document to read before
touching this code. Summary: the rule is a safe over-approximation of *"no struct whose IL2CPP
layout differs from its native layout"*, the difference being an `Il2CppStructArray` field where
native has inline bytes. `SteamNetworkingIdentity` has none and is safe;
`SteamNetConnectionInfo_t`, `SteamNetConnectionRealTimeStatus_t` and both status structs do and are
not.

**Two consequences worth knowing before planning around them:**

- **`GetLatency` returns -1 on this transport.** `GetConnectionRealTimeStatus` is the only source
  of ping and its struct is one of the unsafe ones, so the lobby panel's `rtt` goes blank and the
  packet-loss readout Phase 0 was told to wait for does not arrive after all. The route back is
  direct P/Invoke to the flat C API, which composes with the current approach and creates no second
  callback registry — deliberately not taken yet.
- **The connection-status payload is read at hand-derived native offsets**, validated on every
  payload by two identity sentinels sitting in front of the arithmetic. UNVERIFIED until a real
  connection changes state.

**What is left, in order:**

1. ~~Run `Diagnostics/SteamNetSelfTest` once.~~ **Done, PASSED, 2026-08-12.** The identity layout is
   confirmed against `GetIdentity`, and the socket, poll group, callback registration and teardown
   all came up. See [`05-interop-struct-shapes.md`](05-interop-struct-shapes.md) for the log and for
   the three things that are still open — chiefly that a callback which *registers* is not a
   callback that *fires*, and only a peer can settle that.
2. ~~Move the session lifecycle out of `UdpClientService`.~~ **Done, in two commits.** The receive
   switch is `Services/NetMessageRouter.cs` and the per-tick streams are
   `Services/StateBroadcastService.cs`; `UdpClientService` went from 2241 lines and eight
   dependencies to 1263 and two. Both are behaviour-preserving by construction and **neither has
   been run** — see the playtest note below.

   What was left behind in the transport is the answer to "what is actually transport-specific":
   eight receive cases whose handling depends on *which peer a message arrived on* rather than on
   its contents, plus matchmaking handshake, NAT introduction and relay fallback.
3. ~~Give `SteamNetTransport` the peer half.~~ **Done.** It routes through `INetMessageRouter` and
   handles three peer-scoped cases itself — `Introduced` both ways, `PlayerDisconnected` on a
   client, `SelectedCharacter` on the host. The other five the LiteNetLib side carries are all
   relay, and SDR is invisible above the socket, so they are absent rather than ported and left
   dead.
4. ~~Decide who assigns connection ids.~~ **Done** — `src/common/SteamConnectionId.cs`. A peer's
   connection id **is its Steam account id**, the low 32 bits of the SteamID64. Valve packs the
   account id there with instance, type and universe above it, so two accounts differ in the low
   word exactly when they differ at all: collisions are impossible by construction rather than
   merely unlikely, which is why this beats hashing a SteamID into 32 bits.

   **This is not the mistake the readiness revert was paid for**, and the distinction is worth
   holding onto. That failure was a *guard* evaluated separately on each machine over two
   membership sets filled by different mechanisms at different moments, so the two ends could reach
   different answers. This is a pure function of one immutable input both ends already hold. The
   rule is "never let two machines **decide** independently", not "never compute anything locally".

   The claimed id is checked against the sender's own SteamID on arrival, so a peer cannot index
   itself under somebody else's id.
5. ~~Start a Steam session.~~ **Done** — `Services/SteamNetSessionService.cs`, behind
   `Network/UseSteamTransport`. Hosting creates a Steam lobby instead of a matchmaker room, joining
   enters one by code, and `INetTransport` resolves to `SteamNetTransport`.

   **The handshake is one-way through lobby data, and that is the part to preserve.** The host
   opens its listen socket first and only then publishes `mt_ready`; a client connects only after
   reading it, and to `GetLobbyOwner` rather than to anyone's opinion of who the host is. Steam
   permits only the owner to write lobby data, so there is one writer and one moment. **The
   alternative — each side retrying on its own timer against its own idea of whether the other was
   up — is the shape that broke Steam-backed readiness.** A shipping implementation for this game
   arrived at the same design independently.

   The seed travels the same way, and the client reads it *before* connecting, because world
   generation is downstream of it.
6. ~~Point `INetTransport` at it behind the config flag.~~ **Done**, as a factory that reads the
   flag once at startup — so a session cannot change transport halfway through.
7. Gate `AcceptConnection` on Steam lobby membership, and close with
   `SteamNetEndReason.ProtocolMismatch` on a version mismatch. The reasons are reserved; nothing
   uses them.
8. Then, and only then, retire tags 73/74 — `LobbyDataUpdate_t` is reachable through
   `SteamCallback`, and at that point the Steam lobby is the one membership set.

#### The playtest — done, PASSED

**Two players over the internet, 2026-08-12.** Three unverified changes were stacked on the
LiteNetLib path — the readiness revert carried over from Phase 3, the receive-switch extraction and
the stream extraction — and all three are now verified together.

| Check | Evidence |
|---|---|
| Join, both peers introduced | client `Connected to host`; both connection ids present in the host's roster |
| **Readiness — the outstanding Phase 3 item** | `[readiness] Round 1 open over 2 participant(s)` → `1137855259 ready (1/2)` → `712709437 ready (2/2)` |
| Run starts, six levels completed | client `Received RunStarted message`, host released barrier rounds 1–6 |
| **Enemies stream to the client** | `LobbyUpdates(enemies)` at 31–39/s throughout — the silent failure a first draft of the router would have caused did not occur |
| **Every message type routed** | **zero** `Unknown message type received` on either side, across the whole session. All 95 moved cases are wired |
| Stream pacing unchanged | `PlayersStateUpdate` 60.0/s at 98 B/send — exactly the derived figure; `LobbyUpdates(players)` 5.0/s, the 5 Hz heartbeat |
| Bandwidth | 16–19 KB/s host egress at two players, in line with the Phase 0 baseline |
| Latency | `peer 712709437 rtt 62 ms` |

**Three pre-existing faults surfaced, none of them from this branch or from Steam.**

1. **`WindowManagerPatches.Update_Postix` throws every frame in the pre-run menus** — 3798 times in
   this session, stopping when the run started. `WindowManager.activeWindow as CharacterMenu`
   returns null and is dereferenced immediately; `WindowManager.cs` was last touched 2026-07-31 and
   is untouched by this branch. It is the `as` versus `TryCast<T>()` rule, and it costs real frame
   time in the lobby. Its own branch off `main`.
2. **`TumbleWeedInterpolator` calls `GetComponent` on a destroyed object** — five times on the
   client, with the host logging the matching `TumbleWeed not found in SpawnedObjectManagerService
   when processing OnTumbleWeedDespawned`. A despawn race, not a routing fault.
3. The custom-button NREs, already recorded in [`../ui/04-custom-button-null-background.md`](../ui/04-custom-button-null-background.md).

### Phase 5 — Decommission
- **Drop `NetworkMenuTab` so TOGETHER! goes straight to the lobby.** Planned separately, with the
  parts that are not UI — session setup, the connect coroutine, the connecting state — needing a
  home first: [`../ui/05-drop-the-netplay-menu.md`](../ui/05-drop-the-netplay-menu.md).
- **Drop `ModConfig.PlayerName`.** The Steam persona is adopted onto it at startup already, so it
  is a vestige — but something has to decide what an instance with no Steam is called, and the
  two-instance test harness runs its second copy that way.
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
- **Three options for the managed wrapper.** The original recommendation here was (a). **It was
  reversed at Phase 2 on decompiled evidence, and (b) was taken.**

  **(a) Ship your own managed `Steamworks.NET.dll`.** It P/Invokes the same native
  `steam_api64.dll`, so it works, and it is the conventional approach — but it means two managed
  callback registries over one native dispatch. This was written up as "verify it does not
  double-fire the game's own callbacks". **Double-firing is not the failure mode.** Three
  decompiles:

  ```
  SteamManager.Update           -> SteamAPI.RunCallbacks() every frame, gated on the same
                                   static byte SteamManager.IsInitialized returns
  SteamAPI.RunCallbacks         -> CallbackDispatcher.RunFrame(false)
  CallbackDispatcher.RunFrame   -> SteamAPI_GetHSteamPipe, then
                                   SteamAPI_ManualDispatch_GetNextCallback / FreeLastCallback
                                   / GetAPICallResult
  ```

  Manual dispatch is a **consuming** queue on the process's single pipe. A second managed
  Steamworks.NET brings its own `CallbackDispatcher` over that same pipe, and the two would take
  each other's callbacks — the game intermittently losing its own achievement and leaderboard
  results, non-deterministically, with nothing in any log to say so. That is worse than
  double-firing and far harder to catch in a playtest, because the symptom is an achievement that
  sometimes does not unlock.

  **(b) Use the game's IL2CPP assembly via interop. ← taken.** One registry, pumped by the game.
  Nothing to ship, no redistribution question, and no possible version skew against the
  `steam_api64.dll` sitting next to it. The cost is real and is written up under "what (b) costs"
  below.

  **(c) Direct P/Invoke to the flat C API** (`SteamAPI_ISteamNetworkingSockets_*`) with
  `SteamAPI_ManualDispatch_*` for callbacks. Most work, cleanest isolation, no registry conflict.
  Still the fallback for anything (b) cannot express, and it composes with (b) — a P/Invoke for
  one call does not create a second registry.

#### What (b) costs

**We have no callback registry of our own, and cannot get one without becoming (a).** Everything
later phases would have taken from a `Callback<T>` has to arrive another way:

| Wanted | Phase | Route without a registry |
|---|---|---|
| `SteamRelayNetworkStatus_t`, `SteamNetAuthenticationStatus_t` | 2 | Poll `GetRelayNetworkStatus` / `GetAuthenticationStatus`. **Done and verified** — the callbacks were never needed. |
| `LobbyCreated_t`, `LobbyEnter_t` and other call results | 3 | Poll `SteamUtils.IsAPICallCompleted` then `GetAPICallResult`. **Verified in game** — see below. |
| `LobbyDataUpdate_t` | 3 | Poll `GetLobbyMemberData`. The lobby panel already refreshes twice a second, so there is no new timer. |
| `GameLobbyJoinRequested_t` (friends-list "Join Game") | 3 | **No polling equivalent.** This one genuinely needs a callback, and is the first place (c) will be required. |
| `SteamNetConnectionStatusChangedCallback_t` | 4 | ~~`SteamNetworkingUtils.SetConfigValue` with a function pointer.~~ **Not needed.** `SteamCallback` carries it, same as the two status types. |

None of that is free, but it is all cheaper than a class of bug that only shows up as somebody's
achievement quietly not unlocking.

#### Why the polling route is the recommendation, not the consolation prize

**IL2CPP is ahead-of-time compiled, and a generic only exists for the type arguments the game
itself used.** From `dump.cs`, the complete list of concrete instantiations:

| Generic | Instantiated for | Also present |
|---|---|---|
| `Callback<T>` | `GameOverlayActivated_t`, `PersonaStateChange_t`, `UserStatsReceived_t` | `__Il2CppFullySharedGenericType` |
| `CallResult<T>` | `LeaderboardFindResult_t`, `LeaderboardScoreUploaded_t`, `LeaderboardScoresDownloaded_t` | `__Il2CppFullySharedGenericType` |

Those six are exactly what the game's own overlay, persona, stats and leaderboard code uses.
**Every type this migration needs — `SteamNetConnectionStatusChangedCallback_t`,
`LobbyDataUpdate_t`, `GameLobbyJoinRequested_t`, `LobbyCreated_t`, `LobbyEnter_t`,
`LobbyMatchList_t`, `SteamRelayNetworkStatus_t` — has no concrete instantiation.**

The full-generic-sharing fallback is compiled in, so a novel instantiation is not proven
impossible; it would run through the shared path with a boxed representation, and Il2CppInterop
would have to build the generic instance and marshal a delegate on top of that. Nobody has tried
it. Treat it as UNVERIFIED and unlikely to be worth the attempt, rather than as a closed door.

> **A correction, because the wrong version of this test was written down here.** An earlier draft
> proposed settling it with `Callback<PersonaStateChange_t>.Create`. That is one of the three the
> game already instantiates, so it would have succeeded and proved nothing. **Any test must use a
> type the game never instantiates** — `LobbyDataUpdate_t` is the natural choice.

Mod S proves nothing either way: it ships its own managed wrapper, where `Callback<T>` is ordinary
C# and AOT instantiation does not apply. See
[`03-observed-steam-usage.md`](03-observed-steam-usage.md).

#### Phase 3 does not need generics at all — CONFIRMED in game

**Proved on buildid 21750826, 2026-08-10**, by `SteamLobbySelfTest` in a single-player run:

```
[steam-lobby] Self-test: strings round-trip correctly.
[steam-lobby] CreateLobby requested, max 6.
[steam-lobby] Lobby 109775241643755414 created, owner True, 1 member(s).
[steam-lobby] Self-test: lobby data write True, read '1', compatible True.
                         Member data write True, read '1'. Members: 1.
[steam-lobby] Self-test finished: PASSED
```

Two things that were open are now closed:

1. **Strings marshal correctly** across the boundary into Steamworks calls. The `AssetBundle`
   failure was specific to `ReadOnlySpan<char>.GetPinnableReference`; Steamworks.NET's own UTF-8
   handle is unaffected.
2. ~~**The game's callback pump does not invalidate a polled call result.**~~ **Wrong — retracted.**
   The claim was made off one passing run and the run after it disproved the reasoning behind it.

### RESOLVED: register a CallResult with the game's dispatcher

**The race is over, and the answer generalises.** Rather than compete with the game's callback pump
for our own results, we register with it. `SteamCallResult` derives from Steamworks' **non-generic**
abstract `CallResult`, is injected with `ClassInjector`, and is handed to
`CallbackDispatcher.Register`. `RunFrame` then retrieves each completed call, looks the handle up in
`m_registeredCallResults`, and invokes our instance. It cannot lose the race because it *is* the
race.

Verified in game, buildid 21750826:

```
[steam-lobby] CreateLobby requested, max 6.
[steam-lobby] Result delivered by the game's dispatcher.
[steam-lobby] Lobby 109775241651088375 created, code R9XUW7, owner True, 1 member(s).
[steam-lobby] Code R9XUW7 resolved to lobby 109775241651088375.
[steam-lobby] Self-test finished: PASSED
```

Three things that were open are now closed:

1. **`ClassInjector` handles an abstract IL2CPP base.** All three abstract slots are filled and the
   dispatcher accepts the injected type. The existing precedent (`CustomButton : MyButtonNormal`)
   was a concrete base; this is a stronger case and it works.
2. **The lobby-list search works**, filtered on code *and* protocol version.
3. **Steam does return a lobby to a machine already in it.** That was recorded as unresolvable
   single-player; it is now simply answered, which is why the self-test could pass alone.

**Polling is kept alongside it, and both paths fire in practice.** In the same run, the first
create was delivered by the dispatcher and a second create was picked up by the poll — our ticker
updates before the game's `SteamManager`, so on the frame a result lands the poll can legitimately
get there first. They race each other harmlessly and converge on one `ApplyResult`; whoever is
second finds nothing pending. Do not "simplify" this by deleting the poll without re-reading the
history above: the poll alone is what produced `InvalidHandle`.

#### The same trick unlocks plain callbacks, which is the bigger prize

`Callback`'s non-generic base has exactly the same shape — abstract, no type parameter, result as a
raw `IntPtr`:

```csharp
public abstract class Callback {
    public   abstract bool get_IsGameServer();
    internal abstract Type GetCallbackType();
    internal abstract void OnRunCallback(IntPtr pvParam);
    internal abstract void SetUnregistered();
}
```

So the three callbacks the migration had written off as unreachable are very likely reachable the
same way: **`GameLobbyJoinRequested_t`** (accepting an invite while the game is already running —
the one gap in invites), **`LobbyDataUpdate_t`** (per-member readiness without polling), and
**`SteamNetConnectionStatusChangedCallback_t`** (Phase 4's connection lifecycle, which was going to
need a config-value function pointer).

That removes the last structural blocker in the migration. It is *likely*, not proven — `Callback`
has a fourth abstract member and is registered through a different overload — but the shape is
identical and the hard part is done.

### Historic: the pump race, and correcting the claim above

A second run, identical but for the lobby type, failed at the same point:

```
[steam-lobby] CreateLobby requested, max 6.
[steam-lobby] IsAPICallCompleted reported an IO failure.
```

`IsAPICallCompleted` returned *completed* with its failure flag set, which is what a handle that
no longer exists looks like. The reasoning that produced the retracted claim — "fifteen pump
cycles elapsed and the result survived, so the mechanisms must be independent" — was the wrong
inference from a single success. **One run cannot establish the absence of a race; it can only
fail to observe it.** The likelier explanation of the first run is that the completion happened to
land in a frame where our poll ran before the game's pump did.

So the honest state is:

- **Polling `GetAPICallResult` works when we reach the result first.** Verified — a lobby was
  created, written to and read back.
- **The game's pump can reach it first, and then the result is gone.** Observed once.

Two changes follow. The failure path now reports `GetAPICallFailureReason`, so
`k_ESteamAPICallFailureInvalidHandle` can be distinguished from an ordinary network failure rather
than guessed at — the next run says which this is. And the service is polled **every frame while a
call is in flight** rather than at 4 Hz, so we look in the first frame the result exists.

**That narrows the window; it does not close it.** It relies on our `Update` running before the
game's `SteamManager.Update`, which is true in practice — our GameObject is created in
`Plugin.Load`, long before the game's — but Unity does not guarantee ordering between two
default-priority scripts. If `InvalidHandle` survives this change, the answer is not a faster
poll: it is to stop depending on the shared pipe at all, and the options there are direct
P/Invoke with our own `HSteamPipe`, or injecting a `CallResult` subclass into the game's own
dispatcher.

Both of the other findings from the first run stand, and neither is affected by the race:

- **Strings marshal correctly** into Steamworks calls.
- **Lobby data, member data and the member list all work**, including `Protocol.IsCompatible`
  against a version written and read back.

Every call the lobby flow needs exists non-generically in the game's assembly:

```csharp
SteamAPICall_t CreateLobby(ELobbyType, int cMaxMembers);
SteamAPICall_t JoinLobby(CSteamID);
bool  IsAPICallCompleted(SteamAPICall_t, out bool pbFailed);          // SteamUtils
bool  GetAPICallResult(SteamAPICall_t, IntPtr pCallback, int cubCallback,
                       int iCallbackExpected, out bool pbFailed);      // SteamUtils
int      GetNumLobbyMembers(CSteamID);
CSteamID GetLobbyMemberByIndex(CSteamID, int);
string   GetLobbyMemberData(CSteamID, CSteamID, string pchKey);
void     SetLobbyMemberData(CSteamID, string pchKey, string pchValue);
```

`GetAPICallResult` takes an `IntPtr` and a size, so the result lands in a buffer **we** allocate
with `Marshal.AllocHGlobal` and read at known offsets. That is strictly safer than the proxy
structs, not a workaround for them — it is the same class of call that already crashed us, done
the way that cannot crash, because no `ValueType`-derived proxy is involved and we own the memory.
Field offsets come from `dump.cs`; `k_iCallback` for each type is in the dump as a constant.

**One thing to check early rather than assume:** `GetLobbyMemberData` passes a `string` *into* the
boundary. Ordinary game methods take strings fine throughout this codebase, but `AssetBundle`'s
did not — it marshalled through `Il2CppSystem.ReadOnlySpan<char>.GetPinnableReference`, which is
unbound here (see [`../ui/01-ui-asset-bundle.md`](../ui/01-ui-asset-bundle.md)). Steamworks.NET
marshals through its own `InteropHelp` UTF-8 handle instead, so it should be fine — but one
`SetLobbyData` round trip proves it in a minute and is worth doing before the flow is built on it.

### 2. Call `InitRelayNetworkAccess()`

```csharp
SteamNetworkingUtils.InitRelayNetworkAccess();
```

Call it at plugin startup, well before the first `ConnectP2P`. It begins fetching the SDR
network configuration and authentication ticket. Another implementation for this game does not call it, which costs a
multi-second stall on the first connection.

**Calling it is not enough — poll the result.** A shipping Steamworks implementation for this
game (see [`../netplay/00-fork-comparison.md`](../netplay/00-fork-comparison.md), Mod S) also
reads `SteamNetworkingUtils.GetRelayNetworkStatus` and inspects `SteamRelayNetworkStatus_t`
(`m_eAvail`, `m_eAvailNetworkConfig`, `m_eAvailAnyRelay`, `m_bPingMeasurementInProgress`)
before treating the network as usable. `InitRelayNetworkAccess` is asynchronous; connecting
while it is still measuring is the stall it exists to avoid, just moved.

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

One implementation for this game pins a `byte[]` per message on its send path:

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

**On the host, use a poll group instead of per-connection receive.**
`CreatePollGroup` / `SetConnectionPollGroup` / `ReceiveMessagesOnPollGroup` / `DestroyPollGroup`
drains every peer in one call rather than looping `ReceiveMessagesOnConnection` per connection.
At 6 players that is one interop call per frame instead of five, on a path that already
dominates the receive cost. Mod S uses all four; the other implementation does not, which is one reason its
send/receive path should not be copied. The message identifies its sender via
`SteamNetworkingMessage_t.m_identityPeer` / `m_conn`, so nothing is lost by not knowing which
connection you polled.

### 6a. Gate connecting on authentication, not just on relay access

`InitAuthentication` starts it; `GetAuthenticationStatus` and
`SteamNetAuthenticationStatus_t.m_eAvail` report readiness. Connecting before authentication
is available fails in a way that looks like a NAT problem. Mod S gates on both this and the
relay status above.

`GetAuthSessionTicket` / `CancelAuthTicket` are also worth knowing about: they are how a peer
proves identity rather than asserting it. That is the structural answer to the identity
problem behind internet-play fault 4 — our `ConnectionId` is reissued per WebSocket session, so
a peer that reconnects mid-handover changes identity and the relay routes for an id nobody
uses. A SteamID cannot be reassigned that way, and a session ticket makes it checkable.

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

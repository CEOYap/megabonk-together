# How Mod S uses Steamworks

Read from a decompilation of a shipping Steamworks implementation for this game
(`megabonk-re/mod-s-decompiled/`, ~4,600 lines under its two Steam namespaces). Anonymised as
**Mod S**, per the standing instruction — do not name it here or in a commit.

Status legend per [`../README.md`](../README.md). Everything below is CONFIRMED by reading the
decompiled source unless marked otherwise; what it *does not* tell us is called out at the end,
because two of those gaps matter more than most of what it does tell us.

---

## 1. It does not coexist with the game's Steam. It replaces it.

This is the first thing to understand, and the reason most of the rest is not directly copyable.

```csharp
[HarmonyPatch(typeof(SteamManager), "Load", new Type[] { })]
private static class SteamManagerLoadPatch
{
    private static bool Prefix(SteamManager __instance) => false;
}
```

The game's `SteamManager.Load` is prefixed away entirely. Mod S then runs its own
`RestartAppIfNecessary` → `SteamAPI.Init()` in its own injected MonoBehaviour, pumps
`SteamAPI.RunCallbacks()` from that behaviour's `Update`, and calls `SteamAPI.Shutdown()` on
teardown.

**And it ships its own managed Steamworks.NET rather than using the game's.** CONFIRMED, not
inferred: it calls `CreateListenSocketP2P(0, array.Length, array)` with
`Array.Empty<SteamNetworkingConfigValue_t>()`, and `ReceiveMessagesOnPollGroup(handle, IntPtr[],
int)` with a managed array. The game's Il2CppInterop proxies take
`Il2CppStructArray<SteamNetworkingConfigValue_t>` and `Il2CppStructArray<IntPtr>` — verified by
compiling a deliberate type error against `BepInEx/interop/com.rlabrecque.steamworks.net.dll`. A
managed `T[]` cannot bind to those, so it is not talking to the game's assembly.

So the shape is: **one wrapper, one init, one pump — theirs.** That is the coherent form of option
(a) in [Gotcha 1](00-migration-plan.md#gotcha-1), and it confirms our finding from the other
direction. You cannot simply add a second managed wrapper alongside the game's, because both would
drain the same manual-dispatch pipe and consume each other's callbacks. Mod S avoids that by
making sure the game's dispatcher never runs at all.

**We must not copy this.** Suppressing `SteamManager.Load` means the game's own
`SteamAchievementsManager`, `SteamStatsManager` and `Leaderboards` — which are IL2CPP code bound
to the game's dispatcher — stop receiving callbacks. For a mod that suppresses uploads anyway that
is an acceptable trade. For us it is a regression in singleplayer, which the mod is supposed to
leave untouched.

---

## 2. Bringing SDR up

Both prerequisites, requested once:

```csharp
SteamNetworkingUtils.InitRelayNetworkAccess();
SteamNetworkingSockets.InitAuthentication();
```

Then readiness is watched **two ways at once**, which is worth noticing rather than reading as
redundancy:

- `Callback<SteamRelayNetworkStatus_t>` and `Callback<SteamNetAuthenticationStatus_t>`, whose
  handlers read `m_eAvail`, `m_bPingMeasurementInProgress`, `m_eAvailNetworkConfig`,
  `m_eAvailAnyRelay` and `m_debugMsg` off the struct the dispatcher hands them.
- A direct poll, which reads **only the return value**:

```csharp
if (SteamNetworkingUtils.GetRelayNetworkStatus(out var _) == k_ESteamNetworkingAvailability_Current)
if (SteamNetworkingSockets.GetAuthenticationStatus(out var _) == k_ESteamNetworkingAvailability_Current)
```

The poll answers "is it up right now" for a listener that started after the callback already
fired; the callback carries the detail. If neither is current it retries on a 100 ms
`Task.Delay` loop and marshals the result back through a main-thread action queue.

### The out-struct rule, which cost us a crash

**`out var _` is not a stylistic choice, and it is the whole reason this document exists.**

`SteamRelayNetworkStatus_t` and `SteamNetAuthenticationStatus_t` both carry a managed `byte[]` for
their debug message, so Il2CppInterop generates them as `ValueType`-derived proxy classes rather
than blittable structs. The `out` parameter is safe to *pass* and fatal to *read*: our first Phase
2 build read one field for a diagnostic line and died with

```
Fatal error. System.AccessViolationException: Attempted to read or write protected memory.
   at Steamworks.SteamRelayNetworkStatus_t.get_m_bPingMeasurementInProgress()
```

on reaching the main menu — in a build where the calls themselves had already succeeded and logged
a SteamID. Note that Mod S is not subject to this at all, because its structs come from its own
managed wrapper. It arrives at the same code shape for a different reason, which is exactly the
kind of thing that makes a reference implementation misleading if you copy without asking why.

**Rule for us: pass the out parameter, discard it, decide on the return value. If the contents are
ever needed, they have to come from a `Callback<T>`.**

### Gating

`CreateListenSocketP2P` is not called until both are `Current`:

```csharp
private bool IsReadyToListen() => _netAuthenticationStatusCurrent && _relayNetworkAccessStatusCurrent;
```

That matches [Gotcha 6a](00-migration-plan.md#gotcha-6a) and is worth keeping: connecting before
authentication is available fails looking like a NAT problem.

---

## 3. Host: listen socket and poll group

```csharp
_steamListenSocketHandle = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, empty);
_steamNetPollGroupHandle = SteamNetworkingSockets.CreatePollGroup();
```

Virtual port **0**, no config options. On a connection reaching `Connecting`: `AcceptConnection`,
check the `EResult`, then `SetConnectionPollGroup`. On close or problem: clear the poll group with
`HSteamNetPollGroup.Invalid`, drop it from the map, `CloseConnection(..., bEnableLinger: true)`.
Teardown uses `bEnableLinger: false`.

Receive is one call for every peer, per the poll-group advice in
[Gotcha 6](00-migration-plan.md#gotcha-6):

```csharp
do {
    n = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, buffer, buffer.Length);
    if (n <= 0) break;
    /* dispatch */
    if (++iterations >= 4) break;
} while (n == buffer.Length);
```

Three details worth taking:

- **The `IntPtr[]` buffer is reused**, not allocated per frame, and each slot is zeroed after use.
- **The drain loop is capped at 4 iterations per frame.** A peer that floods cannot stall the
  frame indefinitely; the backlog waits for next frame. The loop only repeats at all when the
  buffer came back full, which is the signal there is more queued.
- **`SteamNetworkingMessage_t.Release(ptr)` in a `finally`.** Each received message is a native
  allocation and leaks otherwise.

There are two dispatch paths behind a `SafeRW` flag: `SteamNetworkingMessage_t.FromIntPtr(ptr)`
and `Unsafe.ReadUnaligned<SteamNetworkingMessage_t>(ptr.ToPointer())`. Same handling, the second
skipping a marshalled copy. A runtime switch between a safe and a fast reader is a reasonable
pattern for a path this hot.

## 4. Client

```csharp
SteamNetworkingIdentity identityRemote = default;
/* SetSteamID(host) */
_steamNetConnectionHandle = SteamNetworkingSockets.ConnectP2P(ref identityRemote, 0, 0, empty);
```

Virtual port 0 again — host and client must agree, and 0 is the value both sides use. The remote
identity is then re-read from `connectionInfo.m_identityRemote` on the status callback and checked
for `k_ESteamNetworkingIdentityType_SteamID` before being trusted.

## 5. Send path

```csharp
var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
try {
    result = SteamNetworkingSockets.SendMessageToConnection(
        conn, handle.AddrOfPinnedObject(), (uint)length, sendFlags, out var _);
} finally { handle.Free(); }

if (result != EResult.k_EResultOK) LogSendFailureThrottled(result, message);
```

A pin per message — the cost [Gotcha 5](00-migration-plan.md#gotcha-5) already flags, and the
reason that gotcha recommends a reused pinned buffer or the batch `SendMessages`. But note **the
`EResult` is checked and failures are logged throttled**, which is exactly what `INetTransport`'s
remarks require of a Steam implementation, since sends return `void` there.

The send loop is rate-limited to 60 Hz (`timeAsDouble - _lastSendTime > 1.0 / 60.0`) and drains a
queue rather than sending inline.

Send flags are raw Steam bits behind their own enum, and they line up with our `NetDelivery`
mapping:

| Their flag | Value | Steam |
|---|---|---|
| `Unreliable` | 0 | `k_nSteamNetworkingSend_Unreliable` |
| `NoNagle` | 1 | `_NoNagle` |
| `NoDelay` | 4 | `_NoDelay` |
| `Reliable` | 8 | `_Reliable` |

No `ConfigureConnectionLanes` anywhere, and no `SetConfigValue`. Lanes remain untried by anyone.

## 6. Lobbies

Lobby-level state through `SetLobbyData` / `GetLobbyData`: a mod marker key set to `"1"`, lobby
name, map, tier, seed, challenge, a `serverReady` flag, and quickplay keys (`_qp`, `_qp_state`,
`_qp_created`).

Per-member state through **lobby member data** — and the keys are:

```
ready, character, skinType, hat
```

Which is precisely the set [`00-lobby-panel.md`](../ui/00-lobby-panel.md) predicted should migrate
together, arrived at independently. That is good corroboration for the Phase 3 plan to retire
tags 73 and 74 and move character/skin/hat the same way.

Asynchronous lobby calls go through `CallResult<T>`: `LobbyCreated_t` for `CreateLobby`,
`LobbyEnter_t` for `JoinLobby`, `LobbyMatchList_t` for `RequestLobbyList`. Friends-list joins come
from `Callback<GameLobbyJoinRequested_t>`, alongside `GameRichPresenceJoinRequested_t`,
`PersonaStateChange_t` and `AvatarImageLoaded_t`.

---

## What this does **not** tell us

Two gaps, and they are the two questions our Phase 3 actually turns on.

**1. Whether `Callback<T>` and `CallResult<T>` work through the game's interop assembly.** Mod S
uses both heavily and passes plain method groups to `Create`, with no
`DelegateSupport.ConvertDelegate` — but it is using *its own managed wrapper*, where that is just
ordinary C#. Against the game's IL2CPP generic it is a different question entirely, and this
decompilation is no evidence either way.

That question is now the pivot of the whole migration. If it works, Phase 3 gets
`GameLobbyJoinRequested_t` and the status structs' contents for free, and the polling table under
*What (b) costs* in the plan shrinks to nothing. If it does not, friends-list join needs direct
P/Invoke. **Settle it with a single throwaway `Callback<PersonaStateChange_t>.Create` before
designing Phase 3 around either answer** — it is a ten-minute experiment guarding a week of work.

**2. Whether the game's own Steam integration survives any of this.** Mod S does not have to care;
it turned the game's Steam off. We do, so nothing here is evidence about our Phase 2 exit
criterion.

## What to take, and what not to

| Take | Why |
|---|---|
| `out var _` on both status calls, decide on the return value | Non-negotiable now — we have the crash |
| Gate the listen socket on relay **and** auth being `Current` | Matches Gotcha 6a |
| Poll group, reused `IntPtr[]`, `Release` in a `finally`, capped drain loop | Correct, and the cap is a nice touch nobody wrote down |
| Check `EResult` and log throttled | `INetTransport` requires it |
| `ready` / `character` / `skinType` / `hat` as member data | Confirms the Phase 3 plan |
| Virtual port 0 | Only has to be consistent; no reason to differ |

| Do not take | Why |
|---|---|
| Prefixing `SteamManager.Load` to `false` | Kills the game's own achievement, stats and leaderboard callbacks |
| Shipping a second managed Steamworks.NET | Only safe *because* of the line above |
| A `GCHandle` pin per message | Gotcha 5 — reuse a pinned buffer or use `SendMessages` |
| Their absence of `InitRelayNetworkAccess` polling detail | They read the details struct safely; we cannot |

# Handover: into Phase 4

Written at the close of the branch that built Steam lobbies, discovery, invites and Steam names.
Most of it is verified in game — over the internet, two Steam accounts, both directions.

## Start here

1. [`00-migration-plan.md`](00-migration-plan.md) — Phase 3's table of what is done and what is
   not, **Gotcha 1** (whose recommendation this work reversed), and the Phase 4 entry.
2. [`03-observed-steam-usage.md`](03-observed-steam-usage.md) — how a shipping implementation for
   this game does the same things, and the two places copying it would hurt.
3. This file's *Three rules* section. They each cost a crash or a broken feature.

## Three rules, each paid for

**Never call a Steamworks method that takes a struct by reference.** `GetRelayNetworkStatus` and
`GetAuthenticationStatus` both do, and both are gone. That shape produced a fatal
`AccessViolationException` twice: first reading a field off the struct it fills, then — after that
was "fixed" and the call looked safe — merely *passing* it, on another player's machine, having run
fine here dozens of times. Status arrives by callback and is read from the native payload with
`Marshal`. **"It works on this install" is not evidence about this shape.**

**Do not race the game's callback pump; join it.** The game drains the process's single
manual-dispatch pipe every frame and frees each message. Polling `GetAPICallResult` competes for our
own results and loses silently — `k_ESteamAPICallFailureInvalidHandle`. `SteamCallResult` and
`SteamCallback` derive from the **non-generic** abstract bases, are injected with `ClassInjector`,
and are registered with the game's own dispatcher, which then delivers to us. Both are verified
working.

One asymmetry that will fail silently if forgotten: `GetCallbackType()` is **inert** on a
`CallResult` (looked up by call handle) and **load-bearing** on a `Callback` (looked up by callback
id, derived from the `[CallbackIdentity(N)]` attribute on the type returned).

**Two peers must not decide a shared mechanism independently.** Readiness was moved onto Steam
member data behind a guard comparing the Steam lobby's member count with the replicated roster's.
The guard ran separately on each machine over two sets filled at different moments, so the ends
disagreed: a client wrote readiness to member data and sent nothing, a host read a set nobody had
written. Reverted. Readiness stays on tags 73/74 until there is **one** membership set.

## What works, and is proven in game

| | |
|---|---|
| SDR relay and authentication come up | `readiness Ready, relay available, auth available` |
| Create / join / leave a Steam lobby, lobby data, member data | verified |
| Protocol version published, filtered on, and checked on entry | verified — a joiner reads `protocol 1` |
| Join by six-character code, strangers included | verified |
| Steam invites — game closed (`+connect_lobby`) and game running (`GameLobbyJoinRequested_t`) | verified both |
| Accepting an invite joins the room | verified |
| Steam persona names, cached, with `PersonaStateChange_t` | verified |
| Both players in one Steam lobby | built, **unverified** |

**Unverified and worth confirming first:** the readiness revert. It restores the path that was
playtested before this branch touched it, but the revert itself has not been run. Two players, press
Ready on both, watch it tick across and Start ungrey.

## Phase 4 — what it actually is

`SteamNetTransport` behind the `INetTransport` seam Phase 1 added. Read
[`01-api-mapping.md`](01-api-mapping.md) and Gotchas 3–7 before writing any of it.

**Phase 3's exit criterion moved here, and this is why.** "Find and join a lobby without the
rendezvous server" splits: finding is done, joining is not and could not be. The rendezvous server
is not only a matchmaker — it performs the NAT introduction that lets two LiteNetLib peers reach
each other, and relays when they cannot. Only SDR replaces that.

Two things Phase 4 inherits that were not available when it was planned:

- **Callbacks work.** `SteamNetConnectionStatusChangedCallback_t` can use `SteamCallback` rather
  than the config-value function pointer the plan proposed. Register it before the first
  `ConnectP2P`.
- **`LobbyDataUpdate_t` is reachable too**, which is what makes retiring tags 73/74 possible once
  the Steam lobby is the session.

Known traps, all recorded in the plan: `ReliableUnordered` has no Steam equivalent and degrades to
reliable-ordered; unreliable messages above ~1200 bytes fragment unreliably and lose the whole
message; use a poll group on the host rather than per-connection receive; reuse one pinned buffer
instead of a `GCHandle` per send; and sends return `EResult`, so failures must be logged throttled.

## Two things parked, both diagnosed

**Every mod-made button throws on hover or click** —
[`../ui/04-custom-button-null-background.md`](../ui/04-custom-button-null-background.md). Cause,
stack and all 18 call sites recorded. Pre-existing, unrelated, its own branch off `main`.

**Dropping the netplay menu** —
[`../ui/05-drop-the-netplay-menu.md`](../ui/05-drop-the-netplay-menu.md). Five ordered steps for
Phase 5. `INetplaySessionService` is already extracted, registered, and **called by nothing on
purpose** — it is not dead code to delete, it is step 1's landing site.

## Two habits worth keeping

**Read Unity's player log, not only BepInEx's.** `LogOutput.log` records an exception's message and
discards its stack trace; `%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log` keeps the whole
thing. The button crash was unattributable for three sessions because of this.

**Count `[Error  :     Unity]` as well as `Error  :MegabonkTogether`.** Unity logs the mod's own
exceptions under its own tag whenever they are thrown inside a Unity callback, so grepping only for
the mod's name hides an entire class of bug.

## Standing constraints

- Two players is the testing maximum. Do not design anything needing more.
- Nothing is verified until run in-game. There is no test suite; "builds clean" means very little.
- One logical change per commit, with what is unverified stated in the body.
- MemoryPack union tags are append-only — never renumber, reuse or remove.
- Do not name another mod or its author in commits, titles or docs.

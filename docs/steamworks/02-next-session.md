# Handover: after Phase 2 and most of Phase 3

Written at the close of the branch that built Steam lobbies, discovery and invites. Unusually for
this project, **most of it is verified in game** — over the internet, with two Steam accounts,
both directions, zero mod errors on either side.

## Read these first

1. [`00-migration-plan.md`](00-migration-plan.md) — Phase 3's table of what is done and what is
   not, and **Gotcha 1**, whose recommendation this work reversed.
2. [`03-observed-steam-usage.md`](03-observed-steam-usage.md) — how a shipping implementation for
   this game does the same things, and the two places copying it would hurt.

## What works, and is proven

| | |
|---|---|
| SDR relay + authentication come up | `readiness Ready, relay available, auth available` |
| Create / join / leave a Steam lobby | verified |
| Lobby data, member data, member list | verified |
| Protocol version published, filtered on, and checked on entry | verified — a joiner reads `protocol 1` back |
| Join by six-character code, strangers included | verified |
| Steam invites, accepted with the game **closed** | verified |
| Steam invites, accepted with the game **running** | verified |
| Accepting an invite joins the room | verified |

## The two mechanisms this branch discovered

Both were unblockers, and both are load-bearing for Phase 4. Neither is obvious.

**Injected `CallResult` and `Callback`.** `CallResult<T>` and `Callback<T>` have concrete IL2CPP
instantiations only for the six type arguments the game itself used, and none is a lobby or
networking type — IL2CPP is ahead-of-time compiled. The **non-generic** bases are abstract, take no
type parameter, and hand their payload over as a raw `IntPtr`. `ClassInjector` can derive from them,
and the game's own dispatcher accepts the result. `SteamCallResult` and `SteamCallback`.

One difference between them that will silently break things if forgotten: `GetCallbackType()` is
inert on a `CallResult` (looked up by call handle) and **load-bearing** on a `Callback` (looked up
by callback id, derived from the `[CallbackIdentity(N)]` attribute on the type you return).

**Never pass a struct by reference to Steamworks.** `GetRelayNetworkStatus` and
`GetAuthenticationStatus` both do. That shape caused a fatal `AccessViolationException` twice: once
reading a field off the struct, and once — after that was "fixed" — merely passing it, on a
different machine, having run fine here dozens of times. Both calls are gone; status arrives by
callback and is read from the native payload with `Marshal`. **"It works on this install" is not
evidence about this shape.**

## What Phase 3 still owes

**Phase 3's own exit criterion is not met.** What shipped is a bridge: the Steam lobby carries the
WebSocket matchmaker's room code as `mt_mm_code`, and the session is still matched and carried by
the rendezvous server. Invites and discovery are Steam's; the session is not. Order of remaining
work is in [`00-migration-plan.md`](00-migration-plan.md) — readiness onto member data and tags 73
and 74 retired, then the Steam lobby becoming the session's identity, then the server is removable.

## Two things deliberately parked

**The button crash.** Every mod-made button throws a `NullReferenceException` on hover or click,
including the main menu's own. Pre-existing, unrelated, diagnosed in full, and left for its own
branch: [`../ui/04-custom-button-null-background.md`](../ui/04-custom-button-null-background.md).

**Read Unity's player log, not just BepInEx's.** `LogOutput.log` records an exception's message and
discards its stack trace; `%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log` keeps the whole
thing. And grepping `Error  :MegabonkTogether` hides every exception thrown inside a Unity callback,
because Unity logs those under its own tag — count `[Error  :     Unity]` too. Both cost this branch
real time.

## Standing constraints

- Two players is the testing maximum. Do not design anything needing more.
- Nothing is verified until run in-game. There is no test suite; "builds clean" means very little.
- One logical change per commit, with what is unverified stated in the body.
- MemoryPack union tags are append-only — never renumber, reuse or remove.
- Do not name another mod or its author in commits, titles or docs.

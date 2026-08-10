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
the rendezvous server. Invites and discovery are Steam's; the session is not.

Both players are now **in** the host's Steam lobby — host creates and publishes, client follows by
the room code — so the membership retiring tags 73 and 74 depends on exists. Nothing reads it yet.

### Retiring tags 73 and 74: what actually blocks it

Worth reading before starting, because the obvious approach does not work and the reason is not
obvious.

**The roster is keyed by `ConnectionId`; Steam member data is keyed by SteamID, and there is no
mapping between them.** `Player` cannot carry a SteamID — it is serialized inside `LobbyUpdates`,
union tag 0, and MemoryPack is positional, so widening it corrupts sessions between builds. So
readiness cannot simply be re-pointed at member data while the roster stays as it is. The panel's
member list has to come from the Steam lobby too, which is what
[`../ui/00-lobby-panel.md`](../ui/00-lobby-panel.md) always intended. `LobbyMemberView.ConnectionId`
is used in exactly one place — a GameObject name — so the panel itself is not the obstacle.

**Three things are:**

1. **The Start gate would be computed over a different set than the session.** `AreAllMembersReady`
   decides whether the host may start, and a client that failed to reach the Steam lobby — host
   without Steam, a search that found nothing — would be invisible to it. The host could start with
   somebody not ready, silently. Any implementation needs an explicit guard that the Steam lobby's
   membership matches the matchmaker roster, and must fall back to the tag path when it does not.
2. **Names.** A roster from Steam needs persona names, and `GetFriendPersonaName` returns nothing
   useful for a non-friend until their info has been requested and a `PersonaStateChange_t` has come
   back. That is a cache and another callback, not a getter — the implementation for this game
   described in [`03-observed-steam-usage.md`](03-observed-steam-usage.md) has a whole class for it.
3. **"Delete 73 and 74" should mean "stop sending them when the Steam path is active"**, not remove
   them. The fallback in (1) needs them, and they are permanently burned in the union either way.

None of this is hard; all of it is easy to get subtly wrong, and it changes the most-verified thing
on this branch. **Do it as its own change, with the two-account test available**, rather than
tacking it onto something else.

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

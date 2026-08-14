# Handover: out of Phase 4, into Phase 5

Written at the close of the branch that built `SteamNetTransport` and got a full co-op run onto
Steam sockets. 47 commits on `claude/steam-phase4-transport`, off `main`, **not pushed**.

## Start here

1. [`00-migration-plan.md`](00-migration-plan.md) — Phase 4's real status, and Phase 5's list.
2. [`05-interop-struct-shapes.md`](05-interop-struct-shapes.md) — **read before touching any
   Steamworks call.** It is the audit that made `ConnectP2P` possible.
3. This file's *Rules this branch paid for* section. Each cost at least one playtest.

## What works, proven in game

Two machines, over the internet, both on `Network/UseSteamTransport = true`:

lobby → invite or join-by-code → character select → map → a full run → level transitions.

Connection and handshake over SDR, session settings through lobby data, enemies, shrines, chests,
XP, mutual visibility, correct characters, skins, hats, and per-peer latency. The rendezvous server
carries none of it.

**The single most useful thing that got settled:** the interop struct-shape rule. The standing rule
was *"never call a Steamworks method that takes a struct by reference"*, which blocks `ConnectP2P`
outright. The real rule is *"never pass by reference a struct whose IL2CPP layout differs from its
native layout"* — the difference being an `Il2CppStructArray` field where native has inline bytes.
`SteamNetworkingIdentity` has none, and `GetIdentity` proved the round trip on a live install before
any connection was attempted.

## The one exit criterion left

**A full run under 3% simulated packet loss. Run once on 2026-08-15 at ~40% loss and substantially
passed — one gap remains.** Full results and the numbers:
[`07-lossy-link-playtest.md`](07-lossy-link-playtest.md).

All three original worries are answered:

- `ReliableUnordered` degrading to reliable-ordered on Steam — **real but bounded.** The reliable
  backlog is visible (473 B pending, 1476 B unacked on the client) and it drained; `queue` read
  0.0 ms in all 207 samples. It cost throughput, not correctness.
- Unreliable sends above ~1200 bytes fragmenting unreliably — **cannot occur.** Every unreliable
  send is bounded below 1000 B by the stream splitter or by its own shape.
- The `MaxUnreliableBytes` promotion **has still never fired**, and that is now a measurement rather
  than an assumption: it logs, and it stayed silent for 207 samples on both peers. It also *cannot*
  be exercised by a loss run, because promotion keys off payload size.

Also proven: 86 barrier rounds released and applied with zero stale reports, and zero send failures,
at ~60% delivery.

**The gap, and the only reason this is not a full pass: no level transition happened under loss.**
Loss was switched on after arriving at level 2 and the session ended there, so the readiness barrier
— the four-times-broken path where one lost message has historically been a permanent hang — was
never exercised lossy. **Run 2 starts clumsy in the lobby and crosses a level transition.**

## Open bugs

| | |
|---|---|
| **Reward window after a charge shrine occasionally desyncs** | Still not diagnosed, but **no longer for want of a log pair.** Session 27217500 (2026-08-15) is a same-session host/client pair and the desync did **not** reproduce in it — 86 barrier rounds, zero stale lines, at ~40% loss. That pair is the healthy baseline to compare the next occurrence against. Reproducing it is now the blocker, not capturing it |
| [OB-11](../netplay/08-observed-bugs.md#ob-11) | Minibosses spawn near the host. Structural — the mod supplies no spawn position, and the game's spawner knows about one player |
| [OB-12](../netplay/08-observed-bugs.md#ob-12) | Interactable tallies diverge. **Cause now confirmed:** the tally is raised by events (`TrackStats` subscribers) that the mod's `Destroy`/`Used` shortcuts never run |
| The run starts without locking the lobby | `SetLobbyJoinable(false)` is not exposed by `ISteamLobbyService`; someone could join between Start and the map loading |
| Quickplay refuses on the Steam transport | Needs a lobby browser. Deliberate: silently using the other transport would contradict the setting |

## Rules this branch paid for

**1. A consumer that holds the implementation type instead of the seam is invisible until a second
implementation exists.** Five of them, each failing differently: `SynchronizationService` sent all
gameplay to LiteNetLib; `BandwidthDiagnostics` reported `rtt -1`; `NetPlayerCard` hid the latency
label; `WindowManager` and `MapController` gated on an empty peer set and passed unconditionally.
Phase 1 changed the *signatures*; anything keeping `IUdpClientService` kept compiling because the two
were the same object. **The grep is `IUdpClientService` outside `UdpClientService.cs`** — four
legitimate uses remain, so a sixth stands out.

**2. A replicated field is a snapshot. Any check combining one with round-scoped state must prove
both describe the same round.** The level transition failed four times on this. `IsReady` is a 5 Hz
snapshot of the host's view; a stamp becomes true the instant a round is adopted. Neither proves the
other is current. **An acknowledgement does**, because it is the only signal causally downstream of
the round opening.

**3. A destroyed Unity object compares *equal to null* while its managed reference is alive and still
in the dictionary.** `existing != null` is false for a destroyed `NetPlayer`, so the code fell
through to create a replacement, `TryAdd` failed against the corpse still keyed there, and the
"lost a race" branch destroyed the healthy new avatar and returned the dead one — forever. One peer
was visible and the other was not, and that asymmetry is the signature.

**4. State that only ever travels as an event does not exist for anyone who arrives later.** Hats,
skins and characters were all announced and never recorded. An avatar built at an arbitrary moment —
which on-demand creation and level transitions make routine — can only read the record. Events say
what *changed*; only the record says what *is*.

**5. Silent exits cost playtests.** Three separate stalls were unreadable because a guard returned
false without a word. Every exit from the readiness report routine now logs, and that turned the last
two failures into single-run diagnoses. When adding a guard on a path that can stall a session, make
it say so.

## A tooling note

`scripts/re/xrefs_headless.py` is new. `dump.cs` cannot answer *"who calls this"* by construction —
it lists declarations, while calls and `+=` subscriptions live in bodies — so grepping it for a
caller always returns nothing, which reads like "nothing calls it". That gap is what settled OB-12.

## Phase 5 — and one correction to its plan

The order is in [`../ui/05-drop-the-netplay-menu.md`](../ui/05-drop-the-netplay-menu.md). **Step 1 is
now half-done and the plan does not say so.**

`NetworkMenuTab.OnHostClicked` and `JoinWithCode` route through `INetplaySessionService` **only when
`UseSteamTransport` is on**; the matchmaker paths still run their own coroutines. That was deliberate
— it made the Steam transport reachable without disturbing the working path — but it means the
"two implementations of session setup" the service was extracted to prevent currently both exist, on
different transports.

So step 1 is now: **move the matchmaker paths onto the service too**, and delete the menu's
`HandleFriendlies` / `HandleConnectionStatus` coroutines. Steps 2–5 are unchanged.

**Before deleting anything:** Phase 5's own rule is to keep the LiteNetLib transport behind the flag
for at least one release. The 3% loss test above is what earns that release.

## Release consequences, not yet acted on

`Protocol.Version` is **2** — `Player` gained `Hat`, and MemoryPack serializes positionally, so a v1
peer misreads every field after it. **v1 and v2 lobbies refuse each other.** That is correct, and it
needs:

- A **loud** `CHANGELOG.toml` and `README.md` entry. To a player this is "I cannot join my friend any
  more" with no in-game explanation.
- A `<Version>` bump in `MegabonkTogether.Plugin.csproj`, still 5.1.0. This cannot ship as a patch.

## Standing constraints, unchanged

- Two players is the testing maximum.
- Nothing is verified until run in-game. There is no test suite.
- One logical change per commit, with what is unverified stated in the body.
- MemoryPack union tags are append-only.
- Do not name another mod or its author in commits, titles or docs.

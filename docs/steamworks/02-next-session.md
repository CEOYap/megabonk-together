# Handover: after Phase 2

Written at the close of the branch that styled the lobby panel and built Steamworks Phase 2.
Nothing on this branch has been run in game.

## Read these first

1. [`00-migration-plan.md`](00-migration-plan.md) — Phase 2's entry, and **Gotcha 1**, whose
   recommendation this branch reversed.
2. [`01-api-mapping.md`](01-api-mapping.md) — for Phase 4, unchanged except for anonymisation.
3. [`../ui/03-next-session.md`](../ui/03-next-session.md) — the lobby panel's remaining items.

## State

Branch `claude/ui-style-and-steam-phase2`, off `main` after PR #7 merged. Builds clean both
ways: against the install's interop assemblies, and against `stripped-libs/` with `MegabonkPath`
unset, which is what CI does.

| Change | Verified how far |
|---|---|
| Ready button re-fits when its label changes | compiles against the real interop assemblies |
| Lobby panel prefab styled | bundle builds, `VERIFY OK`, prefab loads with 2 root children |
| Steamworks referenced from the game's own assembly | compiles both ways |
| `ISteamService` brings up SDR relay + authentication | compiles |

## What to check, in this order

**1. The game still works at all.** Phase 2's actual exit criterion is that adding Steamworks
changes nothing. Launch **through Steam** — the Steam calls do nothing otherwise — get to the
menu, play a normal singleplayer run, and confirm achievements and stats behave. The mod blocks
Steam writes during netplay on purpose (`Patches/SteamStatsManager.cs`, `Patches/LeaderBoards.cs`);
a run that uploads a netplay score is the worst regression available here.

**2. `[steam]` in the log.** Expect one line naming the SteamID and `readiness Ready` within a few
seconds of the menu. `Diagnostics.LogSteamStatus = true` repeats it every 10s with which half of
SDR is up.

**3. The riskiest single thing on this branch.** `GetRelayNetworkStatus` and
`GetAuthenticationStatus` each take an `out` struct and have no overload without one. Both structs
carry a managed `byte[]`, so Il2CppInterop generates them as `ValueType`-derived proxies rather
than blittable structs, and an `out` of one is a shape this project has never used. **If it is
wrong it crashes natively** — no managed stack trace, and the `catch` in `SteamService` will not
see it. A crash a second or two after reaching the menu, in a build that was fine before, is this.

The fallback is direct P/Invoke to the flat C API for those two calls only. It does not undo the
Gotcha 1 decision: a P/Invoke does not create a second callback registry.

**4. The lobby panel, with two players.** The styling and the Ready button's re-fit both need
eyes. Specifically: does `NOT READY` now sit inside its button, and does the card read as one
piece rather than as a list floating above some buttons.

## What Phase 2 hands Phase 3

**There is no callback registry, and there cannot be one without undoing Gotcha 1.** This is the
single fact that shapes the rest of the migration. The table in
[`00-migration-plan.md`](00-migration-plan.md) under *What (b) costs* lists the route for each
callback the later phases wanted. Three of the four are polling, and the lobby panel already
refreshes twice a second, so most of it costs nothing new.

The exception is `GameLobbyJoinRequested_t` — friends-list "Join Game". It has no polling
equivalent and is the first place direct P/Invoke will be needed. Worth knowing before Phase 3
starts rather than discovering it three days in.

## Two things this branch did not do

**`SteamRichPresenceManager` is still unpatched.** Recorded in
[`../reverse-engineering/01-investigation-targets.md`](../reverse-engineering/01-investigation-targets.md)
as a decision rather than an oversight: it will broadcast netplay state to friends as though it
were a normal run. Phase 3 sets rich presence deliberately, so that is the moment to settle it.

**The named reference mod is gone from `docs/steamworks/` only.** It still appears in
`docs/AUDIT_optimized-netplay.md`, `docs/netplay/00-fork-comparison.md`,
`docs/netplay/04-performance-and-gc.md`, `docs/reverse-engineering/00-decompilation-guide.md`.
The fork-comparison document's whole subject is naming forks, so scrubbing it is a judgement call
somebody should make on purpose rather than a sweep.

## Standing constraints

- Two players is the testing maximum. Do not design anything needing more.
- Nothing is verified until run in-game. There is no test suite; "builds clean" means very little.
- One logical change per commit, with what is unverified stated in the body.
- MemoryPack union tags are append-only — never renumber, reuse or remove.
- Do not name another mod or its author in commits, titles or docs.

# Handover — first session with a PC

Written for the session that follows the two web sessions on
`claude/megabonk-distance-player-null-1ggr92`. Those sessions had **no .NET SDK and no game**, so
everything on that branch is analysis and code that has never been compiled or run.

**Your session is the one that changes that.** Almost every open decision below is gated on data
one or two playtests produce, so the order matters more than the volume of work.

For the standing queue and the older history, read
[`06-session-handoff.md`](06-session-handoff.md). This file is what to do *first*.

---

## The branch in one paragraph

22 commits on `claude/megabonk-distance-player-null-1ggr92`, off `main` at `b3be3aa`. 24 source
files changed. It closes the second dangling-transform path (P2-1), a disconnect-handler race
(P1-8), three families of global state that could be stranded by one exception (P1-9/P1-10/P1-11),
projectile cleanup on disconnect (P2-5), five per-frame performance costs, the shared-experience
barrier holes (SE-1…SE-4, SE-8, SE-9, SE-11), a controller confirm-vs-jump misfire, and adds two
diagnostics plus a static-analysis script. **None of it is built. None of it is played.**

---

## Step 0 — build it

```powershell
$env:MegabonkPath = "<path-to>\steamapps\common\Megabonk"
dotnet build MegabonkTogether.sln -c Debug
```

**Check the `MegabonkPath` trap first** (`CLAUDE.md` opens with it): if that path has no
`BepInEx/interop`, the csproj silently falls back to the committed `src/plugin/stripped-libs/` and
does **not** error. If a game type looks wrong, that is why.

Expect the build to be where the risk is. These signatures changed, so failures cluster here:

| Changed | Where |
|---|---|
| `AddSpawnedProjectile(ProjectileBase, uint)` | `IProjectileManagerService` + its one caller |
| `RegisterProjectileForInterpolation(uint, GameObject, uint)` | same service |
| `ProjectileInterpolator.RegisterProjectile(uint, GameObject, uint)` | `Scripts/Snapshot/` |
| `Plugin.SuppressOutbound()` / `Plugin.OutboundSuppression` | new, used at 28 sites |
| `getNetplayerPositionRequestQueue` element type | now `(uint ConnectionId, int Frame)` |
| `EnemyInterpolator.Update()` → `Tick(double)` | driven by `EnemyInterpolatorManager` |
| `IEncounterService` | four new members for the failsafe |

Two checks that need no SDK, worth re-running after any edit:

```bash
pip install tree_sitter tree_sitter_c_sharp dnfile
python3 scripts/checks/csharp_static_checks.py          # syntax + using/try scope breaks
python3 scripts/re/interop_members.py MyInputManager    # what type is this game field?
```

The first one already caught one real compile error on this branch (`SpawnReviver`'s
`desertGraveInstance` scoped into a `using` block). It checks syntax and block scope only — it says
nothing about types.

---

## Step 1 — deploy to **all three** installs

The csproj auto-copies to one `MegabonkPath`. The other two are manual, and two separate test runs
in an earlier session were invalidated by clients silently running a stale DLL.

**Freshness check that does not need the log:** delete `BepInEx/config/megabonk.together.cfg` on
each install before launching. The new build writes three new keys on first run —
`EncounterInputGraceSeconds`, `LogAllocationRate`, and the `Diagnostics` section. If a config file
comes back without them, that install is running an old DLL.

---

## Step 2 — the four runs, in this order

Everything on this branch is verified or falsified by these. Collect **all three** logs each time
(`<Steam>/steamapps/common/Megabonk/BepInEx/LogOutput.log` and the equivalents).

### Run A — 3 players, shared experience, mid-run disconnect, one player **dead and spectating**

The most information per minute. It exercises P2-1 (spectator camera), P1-8 (disconnect race),
P2-5 (projectile cleanup), and the barrier at the same time.

- have one player die and start spectating **before** a third player disconnects;
- keep the run going afterwards for at least a couple of minutes.

**Answers:** whether the spectator camera moves to another player instead of freezing; whether the
departed peer's card disappears everywhere; whether their projectiles vanish; and — from the
`Transform fallbacks fired` counters — whether the P2-1 fallback is finally dead, which is the
licence to delete three globally patched Unity properties
([`09-performance-audit.md`](09-performance-audit.md) item 1, the largest single perf change
available).

### Run B — shared experience, chests and level-ups overlapping, 3 players

Open chests at nearly the same time, repeatedly, with at least one player levelling up *during* a
chest window. That is what fills `rewardQueue`, which is SE-1's trigger.

**Answers:** whether the barrier still strands anyone, and whether the 20-second failsafe ever
fires. Also whether the chest `[chest #81]` / `[chest #93]` diagnostics say anything — see the log
table below.

### Run C — long shared-experience run, past level 100

Upstream #74's condition. The defect is a per-round probability, so it only appears once the rounds
are near-continuous.

**Answers:** whether #74 is fixed, bounded, or still live. Count failsafe lines per stage.

### Run D — host session with `LogAllocationRate = true`, through a final swarm

Turn it on in the config, play to a full swarm at 3+ players.

**Answers:** the `EnemyModel` allocation hypothesis, and by difference the `DynamicData` one. The
Unity Profiler **cannot** attach here (retail IL2CPP build, not a development player) — this
sampler exists because of that.

---

## The log lines that matter

Grep for these. Most of them are silent when healthy, which is the design.

| Line | Meaning |
|---|---|
| `Shared-experience failsafe fired after …s` | A barrier hole survived. Count per stage — absent means SE-1 was the whole of it, frequent means round identity (SE-5) is now urgent |
| `Transform fallbacks fired in the last ~5s` | The P2-1 dangling-reference fallback. **Zero across Run A is the licence to delete it** |
| `[chest #81]` | The game's item callbacks were stranded null — confirms the "only banish" hypothesis, and P1-9 is then the fix |
| `[chest #93]` | A `ChestWindowUi` field was null on open. If it names `b_open=False`, SE-9 was *not* the cause and the NRE is elsewhere |
| `[alloc] … KB/s managed` | Run D only. ~1 MB/s at a full swarm supports the `EnemyModel` churn hypothesis |
| `[input] A reward window opened with the confirm action already held` | The controller guard did its job — and confirms `UISubmit` and `Jump` share a physical button |
| `Dropped N stale netplayer position request(s)` | A push/pop pair leaked (P1-11). Names the frequency, not the site |
| `Player not found for ConnectionId: N (+M more)` | A per-frame caller is stuck on a departed player |
| `Disconnected player … was already removed` | Informational — the websocket path won the disconnect race. Not an error |

---

## What each result decides

**If Run A shows zero transform fallbacks** → delete the `__instance == null` branch from all three
prefixes in `Patches/Unity/UnityComponent.cs`. That removes a native `op_Equality` call from every
`.transform`, `.position` and `.rotation` read in the process. Biggest available win; do it on its
own and re-run Run A to confirm nothing regressed.

**If Runs B and C show no failsafe lines** → the shared-experience work is done for now; move to the
fix order in [`07-shared-experience-audit.md`](07-shared-experience-audit.md#fix-order), whose next
step is the XP delta (SE-7).

**If the failsafe fires repeatedly** → skip the XP delta and go straight to round identity
(SE-5/SE-6). It needs a wire change; the audit's recommendation is to append **new union tags**
(`EncounterClosedV2`) rather than add fields to the existing messages, which is the
positional-corruption hazard `CLAUDE.md` forbids.

**If `[chest #81]` appears** → P1-9's `try/finally` is the fix, and the exception it was hiding is
in the same log. Close upstream #81, which was closed without one.

**If Run D shows ~1 MB/s at a swarm** → pool the `EnemyModel`s. If it is an order of magnitude
lower, that hypothesis is dead and the effort belongs on `GetNetPlayerByWeapon` instead
([`09`](09-performance-audit.md) item 4).

---

## Traps that have each cost a session

1. **Two players is not enough** for anything involving a disconnect — at 2 the session ends the
   instant the only peer leaves, so the whole post-disconnect window never opens.
2. **Deploy to all three installs**, and verify with the config-file check above.
3. **Collect all three logs.** Several findings existed in only one of them.
4. **Symbols ship next to the DLL**, so managed exceptions carry file and line. That is the single
   most useful debugging property this project has — quote the whole stack, not the message.
5. **Netcode bugs are mostly invisible at 0% loss.** Use `clumsy` at 2-5% before calling anything
   fixed.
6. **Read the diagnostic counters per window, not in aggregate.** A path that only fires in the
   first few windows after an event is easy to miss entirely.

---

## If the branch misbehaves, bisect by theme

The commits are grouped, and the groups are independent:

| Theme | Commits |
|---|---|
| Dangling transform / spectator | `ac57c12` |
| Disconnect race | `d8d27a4` |
| Stranded global state | `6002e20`, `4f8d1c0`, `7ee8b02`, `e69aec8` |
| Projectile ownership | `27571ba`, `d89b4b0` |
| Performance | `dee539e`, `16d8551` |
| Shared experience | `91522fa`, `41d9a73`, `1f8f0b4` |
| Controller input | `e17c6ff`, `e0aeb01` |
| Diagnostics only | `3eea313` |
| Docs only | `9a0b454`, `a1fe60d`, `e9e41a3`, `95dc968`, `00deffd`, `904f2ee` |

The largest single behavioural change is `4f8d1c0` (27 mechanical rewrites of the
`CAN_SEND_MESSAGES` pattern). If something replicates wrongly in a way that looks arbitrary, revert
that one first and re-test — its risk is the happy path, not the exception path.

---

## Before shipping any of it

Per `CLAUDE.md`, three files move together and then a tag:

1. `src/plugin/MegabonkTogether.Plugin.csproj` → bump `<Version>`
2. `src/plugin/CHANGELOG.toml` → a new `[version."X.Y.Z"]` block, **written for a player**
3. `README.md` → matching changelog entry
4. tag `X.Y.Z` and push

Nothing enforces that the tag and `<Version>` agree. And **one fix per release** — upstream's issue
history is hard to attribute precisely because it was not: #81 was filed against 4.0.1 while 4.0.3
was already out, and the commit that *introduced* SE-1 shipped in the release whose changelog
claimed to prevent deadlocks.

Player-facing changes on this branch that need a changelog entry when they ship: the controller
input grace, the shared-experience failsafe, and the disconnect cleanup fixes.

---

## Standing rules worth restating

- **Nothing is verified until it has been run in-game.** "Builds clean" is stated as exactly that.
- The interop assemblies are signature-only stubs. Anything inferred from a game method's signature
  is **UNVERIFIED** until checked against `dump.cs` or a decompiled body — but note that *existence
  and type* can be settled offline with `scripts/re/interop_members.py`.
- One logical fix per commit; the body says what was done, what was deliberately not done, and what
  remains unverified.
- When an entry in the docs turns out to be wrong, **keep the wrong reasoning with a banner**.
  Three entries on this branch were corrected that way, and each had already been proposed twice.
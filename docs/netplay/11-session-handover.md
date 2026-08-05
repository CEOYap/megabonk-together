# Handover — after the first PC session

> **Superseded by [`12-session-handover.md`](12-session-handover.md).** Read that first. Most of
> this file still holds, with two corrections it makes explicitly: the stage-transition work turned
> out to be a third-party mod on one peer rather than anything on this branch, and Run B/C have now
> been played — the shared-experience failsafe fires, rarely, at level 113.

Supersedes [`10-session-handover.md`](10-session-handover.md), which was written *before* anything
on the branch had been compiled or run. That file's Step 0 and Step 1 are now done; its Run A is
done five times. Read this one first, then [`06-session-handoff.md`](06-session-handoff.md) for the
standing queue.

Branch: `claude/build-fixes-first-pc-session`, off `main` at `75c9260` (which is where the previous
22-commit branch was merged — it no longer exists, everything is on `main`'s history).

**22 commits. Everything builds, everything is deployed to all three installs, and roughly half of
it is now verified in-game.** That last part is new: before this session, nothing on the branch had
ever been run.

---

## What is verified, and what is not

This is the part worth reading carefully. "Builds clean" and "works" are different claims and the
distinction has been maintained.

### Confirmed working in-game

| Thing | Commit | Evidence |
|---|---|---|
| The game launches at all | `c0c59bb` | It didn't, for the first three attempts |
| Controller reward-window guard | `ceae717`, `6bae7c1` | `[input] A reward window opened…` fires; controller selection works |
| Ghost players no longer deadlock the lobby | `8a3819d` | `Cleared N lobby player(s)…`, lobby proceeds |
| Projectile NRE on disconnect | `d30ab54` | Zero occurrences across three runs; was ~10 per disconnect |
| Spectator camera moves on disconnect (P2-1) | pre-existing | `Spectated player is gone; switching…`, observed |
| Departed peer's card and projectiles vanish (P2-5) | pre-existing | Observed |
| Disconnect race (P1-8) | pre-existing | `websocket ClientDisconnected won the race` — the race happened and was absorbed |
| Kill attribution (P1-5) | pre-existing | `UNBALANCED-UNSET: 0, overwrite-while-set: 0` every run |
| Stale position requests (P1-11) | pre-existing | `Dropped N stale…` never appears |

### Built and deployed but never exercised

- `4f7fb28` — server relay lifetime. Needs a self-hosted server; the runs used LAN P2P or the
  official server, so this has never run.
- `86c7002` + `f95c733` — bandwidth counters. These *do* work (see the baseline below); what is
  unverified is the recorder's own cost at six players in a full swarm.
- `83cf04d`, `25163ee` — encounter-guard readiness and re-entrancy latches. **`83cf04d`'s diagnosis
  was wrong** and is documented as such; it was left in because it is cheap and correct on its own
  terms, not because it fixed anything.

### The entire shared-experience system is still untested

SE-1…SE-11 is roughly a third of the branch and **has never been stressed**. The failsafe fired
twice in one run and zero times in the four since — but Run A does not exercise the barrier, so
those zeros mean almost nothing. **Runs B and C are the only things that test it.**

---

## The one open bug

### Transform fallbacks — five runs, four fixes, zero movement

The host logs, from the moment a peer disconnects until the run ends:

```
Transform fallbacks fired in the last ~5s — get_transform: 714, get_position: 0, get_rotation: 0,
netplayer-not-found: 0. Destroyed instance type: UnityEngine.Transform.
Sampled caller: no MegabonkTogether frames (called from game code).
```

**~143/sec, ~2.4 per frame, host-only.** The number has been 707-720 in every window of every run,
across enemy counts of 17, 7, 6 and 2, and both spectator/disconnect pairings. Nothing has moved it.

What is established by measurement:

- The destroyed object is a **bare `UnityEngine.Transform`**, not a component or a NetPlayer. The
  original "suspected: a disconnected peer's NetPlayer" guess is **disproved**.
- The reads come from **game code** — no mod frames on the managed stack. Trustworthy only since
  `891bd9f`, which fixed the sampler naming its own patch class as the caller.
- It is **independent of enemy count**, which is what killed the enemy-targeting hypothesis.

Four fixes attempted, three of which found or targeted genuine dangling references, none of which
was the source:

| Attempt | What it did | Result |
|---|---|---|
| `379bd28` | Move enemies off the departed player's Rigidbody | Moved 17/7/6/2 real enemies. Rate unchanged. |
| `6e836d9` | Stop pickups following, keyed on our `ownerId` | Matched **zero** pickups — only one of two code paths sets that key |
| `7658a19` | Same, matched on `pickup.target` and nulls it | Matched **zero** again |
| `2ddd43c` | Stopped fixing; count distinct destroyed objects instead | **Awaiting a run** |

**Next step is `2ddd43c`'s output, not a fifth candidate.** The report now appends
`#<identity> (repeat), N distinct so far`:

- **One identity, repeating** → a single object. Becomes a lookup, and `Enemy.followTarget` (below)
  is worth pursuing.
- **Many distinct** → the shape of the problem is not what five runs of reasoning assumed, and every
  candidate so far was wrong for the same structural reason.

**The one structural lead left untried:** `dump.cs` shows `Enemy` has *two* target references —
`public Rigidbody target` (which `379bd28` clears) and **`public Transform followTarget`, which the
mod never touches anywhere**. It is a bare Transform on a game object read by game code, which fits.
It does *not* obviously fit the enemy-count independence. Its setter is **private**, so the mod may
not be able to clear it even if it is the holder. Do not act on this before the identity counter
reports.

**Consequence: the P2-1 licence stays denied.** Do not delete the `__instance == null` branches from
`Patches/Unity/UnityComponent.cs`. The fallback is catching ~143 dangling reads/sec and is the only
thing between this and a native crash. It remains the largest available perf win, gated entirely on
this bug.

---

## Reverse engineering — now the fastest tool available, use it earlier

This session generated five UNVERIFIED markers of the form "the body is a stub, so we cannot know",
and three were answered in one command once the decompiler was actually used. `CLAUDE.md` now
documents this; the short version:

| Question | Tool |
|---|---|
| Does this member exist? What type? Virtual? | `python scripts/re/interop_members.py Pickup` |
| **What is the field layout?** | `megabonk-re/build-21750826/dump.cs` |
| What does the body **do**? | decompile |

```bash
"$APPDATA/ghidra/ghidra_12.1.2_PUBLIC/venv/Scripts/python.exe" scripts/re/decompile_headless.py 0x1804D7800 DamageContainer
```

The middle tier was the one being skipped. A field layout from `dump.cs` settled in one lookup what
four playtests could not.

**`analyzeHeadless.bat` cannot run these scripts** — it launches Ghidra without the PyGhidra
provider and dies with "Ghidra was not started with PyGhidra". Use the launcher above.

**Two traps, both hit this session:** use the **VA** not the RVA (an RVA silently resolves to an
unrelated function — a lookup for `Enemy.set_target` landed on `TMP_FontAsset$$get_usedGlyphRects`);
and **Ghidra's applied struct field names can sit a slot out** — `Pickup$$Update.c` labels the
`pickedUp` byte as `.target` and reads the target pointer as `.speed`. `dump.cs`'s offsets are
authoritative.

Decompiled output is cached in `megabonk-re/decompiled/` — 87 files now, re-running is free.

---

## The bandwidth baseline (Steamworks Phase 0)

Solid across four runs at 3 players. **`LobbyUpdates` is ~95% of all host traffic**, flat at ~100/s:

```
[bw] 45.8 KB/s payload out over 10.0s at 3 player(s).
[bw]   LobbyUpdates          34.46 KB/s   63.3/s   558 B/send    ← everything
[bw]   SpawnedObject         10.91 KB/s   84.6/s   132 B/send
[bw]   ProjectilesUpdate      0.32 KB/s
[bw]   everything else       < 0.2 KB/s combined
```

Client egress is ~5.4 KB/s, almost all `PlayerUpdate` at 60/s.

That single message is where any bandwidth work belongs, and it is what Steam's connection lanes
would need to know about. **A 6-player capture closes the Phase 0 exit criteria** — 2 and 4 would
complete the set. Leave `LogBandwidth = true`.

---

## What to do next, in order

1. **Run B** — 3 players, shared experience, chests and level-ups overlapping. Open chests
   near-simultaneously and repeatedly, with at least one player levelling *during* a chest window;
   that is what fills `rewardQueue`, SE-1's trigger. This is the largest untested surface on the
   branch.
2. **Run C** — long shared-experience run past level 100 (upstream #74's condition).
3. **Run D** — `LogAllocationRate = true` for that run only, play to a full swarm at 3+.
4. **A 6-player session** at any point, for the baseline.

The identity counter rides along on any disconnect, so it needs no dedicated session.

### What each result decides

- **Failsafe silent in B and C** → shared-experience work is done for now; next is the XP delta
  (SE-7) per [`07-shared-experience-audit.md`](07-shared-experience-audit.md#fix-order).
- **Failsafe fires repeatedly** → skip SE-7, go to round identity (SE-5/SE-6). That is a wire
  change and must use **new union tags** (`EncounterClosedV2`), never new fields on existing
  messages — see the append-only rule in `CLAUDE.md`.
- **`[alloc]` ~1 MB/s at a swarm** → pool the `EnemyModel`s. An order of magnitude lower kills that
  hypothesis and the effort belongs on `GetNetPlayerByWeapon` instead.
- **Identity counter shows one repeated id** → the transform bug becomes a lookup; pursue
  `Enemy.followTarget`.

---

## Smaller things still open

- **`Animator.set_speed` NRE in `RestoreDeath`** (`Plugin.cs:415`) — fires on host and clients every
  single run. Contained by P1-9's try/catch, never fatal, but it is the last recurring exception and
  nobody has looked at it. Fully specified: file, line, and an IL2CPP stack.
- **`Enemy not found in EnemyManagerService when processing OnEnemyDied`** — several per run,
  post-disconnect.
- **Audit `02-delivery-method-reference.md`** against the branch's new shared-experience messages. A
  Steamworks prerequisite: the map must be correct before it is translated to a new API.
- **Internet play is blocked by four separate faults** and was parked. See the section below.

---

## Internet play — parked deliberately

Three sessions went into this. Four independent faults, none on the branch under test:

1. **The rendezvous server sees a private address** (`192.168.8.1`) for a host behind the same NAT,
   so hole punching is structurally impossible. Only an off-LAN server fixes it.
2. **Relay session lifetime was tied to the matchmaking WebSocket** — fixed in `4f7fb28`, still
   never exercised.
3. **Ghost players from retries** — fixed in `8a3819d`, confirmed working.
4. **ConnectionId is reissued per WebSocket session**, so a peer that reconnects during the relay
   handover changes identity mid-flight and the relay routes for an id nobody uses. **Unfixed, and
   the reason a CGNAT friend cannot join.**

Fault 4 has no small safe fix. It is also the fault that **Steamworks deletes outright**, since
SteamID has no session lifetime. If internet play matters more than the branch, the migration is the
answer rather than patching this.

Config for self-hosting, if it comes up again: server must bind `ASPNETCORE_URLS=http://0.0.0.0:5000`
(not the public IP — you cannot bind an address the machine does not have), and clients use
`ws://<ip>:5000`, **not `wss://`** — there is no TLS terminator.

---

## Standing rules, restated because they earned it this session

- **Nothing is verified until it has been run in-game.** Say "builds clean" and mean exactly that.
- **When three fixes in a row fail, the architecture is wrong, not the fix.** Four candidate fixes
  went into the transform bug before anyone measured which object it was. That is the single
  clearest lesson here.
- **One logical fix per commit**, and the body says what was deliberately not done and what remains
  unverified.
- **When an entry turns out to be wrong, keep the wrong reasoning with a banner.** `83cf04d`'s
  diagnosis and `c832a92`'s correction are both preserved for this reason. The exception is a live
  log line that would actively mislead — `891bd9f` deleted a disproved guess rather than bannering
  it, because it printed next to the measurement contradicting it.
- **Deploy to all three installs** and check the SHA256 matches. Two earlier test runs were
  invalidated by a stale DLL.
- **Remove UnityExplorer before a measured run.** It loads alongside the mod and throws its own
  IL2CPP exceptions.
- **Netcode bugs are mostly invisible at 0% loss.** Every run so far has been LAN at 0%. Use
  `clumsy` at 2-5% before calling anything fixed.

---

## Before shipping

Nothing has been released from any of this — `<Version>` is still 5.1.0 and untouched, per the
standing instruction not to bump without asking. When it ships, `CLAUDE.md`'s three-files-then-tag
process applies. Player-facing changes needing a changelog entry:

- the controller reward-window guard (and its known gap: `ChooseOffer` is virtual and unguardable,
  so the *chest's* offer selection is not covered — the level-up pick is, via `UpgradeButton`)
- the shared-experience failsafe
- the disconnect cleanup fixes
- the lobby deadlock fix

**One fix per release.** Upstream's history is hard to attribute precisely because it wasn't.

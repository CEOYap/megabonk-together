# Critical Netplay Fixes

Ranked, implementable fix list. Every item has a symptom, a root cause traced to a line on
`main` (`041881b`), a proposed patch, and a test that reproduces it.

**None of these patches have been compiled or run.** They are derived from source reading in
an environment with no .NET SDK. Build and playtest each one.

## Priority summary

> **Picking this up fresh?** Read [`06-session-handoff.md`](06-session-handoff.md) first. It has
> the current state of every item, the open-work queue in priority order, and the testing traps
> that have each cost a wasted session.

| ID | Title | Severity | Effort | Transport-independent |
|---|---|---|---|---|
| [P0-0](#p0-0) | Steam upload suppression unverified — ban risk | Critical | S | yes |
| [P0-6](#p0-6) | Reviving a disconnected player's coffin corrupts the rest of the run — **FIXED** | Critical | S | yes |
| [P0-5](#p0-5) | Netplay flag never cleared on teardown — singleplayer keeps taking the netplay path | Critical | XS | yes |
| [P0-1](#p0-1) | Shrine/pylon/lamp claim clobbering locks the object permanently | Critical | S | yes |
| [P0-2](#p0-2) | `KeyNotFoundException` in every charging stop path | Critical | S | yes |
| [P0-3](#p0-3) | Non-atomic network ID allocation | High | S | yes |
| [P0-4](#p0-4) | `NullReferenceException` on enemy target assignment | High | XS | yes |
| [P1-1](#p1-1) | ~~Client-originated XP / gold / encounter-close never reach peers~~ **not a defect, entry was wrong** | — | — | — |
| [P1-2](#p1-2) | Legendary (golden) shrine state not synced | Medium | XS | yes |
| [P1-3](#p1-3) | No protocol version gate — mismatched builds corrupt sessions | High | M | mostly |
| [P1-4](#p1-4) | `GetAllPlayersAlive()` allocates on every call, including per-tick paths — **FIXED** | Medium | S | yes |
| [P1-5](#p1-5) | ~~Damage ownership misattributed through shared static `DamageContainer`s~~ **every mechanism eliminated in-game; symptom unattributed** | — | — | — |
| [P1-6](#p1-6) | `ReTargetEnemies` throws on an empty alive-set, aborting the death handler — **FIXED**, plus 4 more sites via P1-4 | Medium | XS | yes |
| [P1-7](#p1-7) | One destroyed `NetPlayer` aborts `Reset()`'s destroy loop, orphaning the rest | Medium | XS | yes |
| [P1-8](#p1-8) | A lost race in `OnReceivedPlayerDisconnected` skips the whole disconnect handler, host retarget included — **FIXED** | High | XS | yes |
| [P1-9](#p1-9) | Save/restore pairs on game statics use nullness as the "saved?" flag, stranding the mod's handlers — **FIXED** | Medium | XS | yes |
| [P1-10](#p1-10) | 28 `CAN_SEND_MESSAGES = false` latches with no `try/finally` — one throw and the peer stops sending — **FIXED** | High | M | yes |
| [P1-11](#p1-11) | Stranded netplayer-position requests redirect the local player's transforms to a peer — **FIXED** | High | S | yes |
| [P2-5](#p2-5) | `RemoveProjectilesByOwnerId` matches the projectile id against the connection id, so it removes nothing — **FIXED**, both id spaces | Medium | S | yes |
| [P2-1](#p2-1) | Dangling transform hack is silent — instrumented, **two root causes found, both FIXED** (second unverified in-game) | Low | XS | yes |
| [P2-2](#p2-2) | Dead `GetAllPlayers()` calls in charging paths | Low | XS | yes |
| [P2-3](#p2-3) | Charging logic triplicated across shrine / pylon / lamp — **FIXED** | Low | M | yes |
| [P2-4](#p2-4) | `CheckForUpdates = false` is ignored, and the un-initialised service then NREs | Low | XS | yes |

### Suggested order

```
P0-0  →  P2-1  →  P0-1+P0-2+P2-2  →  P0-4  →  P0-3  →  P1-3  →  P1-1  →  P1-5  →  P1-2  →  P1-4+P1-6  →  P2-3
```

**P1-6 pairs with P1-4.** Its fix is to hoist the alive-set materialisation out of the retarget
loop and guard the empty case — which is the same edit
[`04-performance-and-gc.md`](04-performance-and-gc.md#4-enemymanagerservice--remaining-linq)
already asks for on that method. Doing them together is one change, not two.

Three deliberate departures from a straight severity ranking:

1. **P0-0 first.** It is the only item whose failure mode harms players outside the game
   (leaderboard bans). It is also small, and independent of everything else.
2. **P2-1 second, despite being P2.** It is XS, zero-risk, and purely *diagnostic* — it turns the
   silent dangling-transform hack into rate-limited evidence. Landing it early means every
   subsequent playtest in this sequence is also collecting data on it, which is the only
   realistic route to closing
   [investigation target #8](../reverse-engineering/01-investigation-targets.md#8-netplayer-lifetime-and-the-dangling-transform--nice).
   Doing it last wastes every test run before it.
3. **P1-3 before P1-1, P1-2 and P1-5.** All three change or add message handling; P1-2 and P1-5
   change the wire format outright. Landing the version gate first means a mismatched build is
   refused with a readable message instead of desyncing confusingly — which matters most
   precisely while you are iterating on message changes.

P0-1 and P0-2 touch the same six methods, and P2-2 deletes dead code in twelve methods that
overlap them. Do all three in one branch.

### What the decompilation changed

Findings from
[`../reverse-engineering/01-investigation-targets.md`](../reverse-engineering/01-investigation-targets.md)
(buildid 21750826) that move items on this list:

| Item | Change |
|---|---|
| [P1-2](#p1-2) | **Effort S → XS.** `ChargeShrine.Start` confirmed **not** to write `isGolden`, so the `Start` postfix is dead weight. Steps 1–3 alone are sufficient. |
| [P1-5](#p1-5) | **New.** `WeaponUtility.GetDamageContainer` ignores its `recycleDc` argument and returns a **static** container; `DamageUtility` and `XpDamager` hold statics too. Identity-keyed ownership collapses. |
| [P0-0](#p0-0) | **New.** `Leaderboards.UploadScore` carries mod detection, and two of its three uploads bypass that check entirely. |
| Final swarm cap | The vanilla cap is **550**, not 400. 400 is the *final-swarm* value, dropping to 300. See the rejection table below — and re-check our own 500/600 numbers. |
| `BaseSummoner` patch | The compounding is real but **inverts** the feared direction (more enemies, not fewer), and a cleaner patch point exists. Still not recommended as written. |

---

<a name="p0-0"></a>
## P0-0 — Steam upload suppression unverified (ban risk)

**Status:** CONFIRMED (game side) / UNVERIFIED (mod side)
**Files:** `src/plugin/Patches/LeaderBoards.cs`, `Patches/SteamStatsManager.cs`,
`Patches/SteamAchievementsManager.cs`

### Why this is first

`NETPLAY_CHANGES.md` states that uploading a netplay score can get players banned. Decompilation
of `Leaderboards.UploadScore` (VA `0x1803E2820`) shows the game **already detects mods**: it
calls `LeaderboardsNew_Sus.CheckMods(...)` above a score threshold and probes ~10 directory paths
with `Directory.Exists`.

Two facts make the mod's own suppression load-bearing rather than belt-and-braces:

1. **That detection gates only one of three uploads.** The primary `QueueLeaderboardUpload` is
   guarded by `canShowScore && !suspicious`; two further `QueueLeaderboardUpload(..., true)`
   calls run **unconditionally** at the end of the method.
2. **`SteamAchievementsManager.ENABLED` does not cover it.** That kill switch is declared on the
   achievements manager only, and it does gate `TryUnlockAchievement` completely (confirmed at
   VA `0x1803EBDE0`) — but `Leaderboards` has no equivalent.

### Resolution — leaderboards blocked UNCONDITIONALLY, achievements and stats allowed

**Decision:** the mod blocks **leaderboard uploads whenever it is loaded** — netplay session or
not. Steam achievements and stats are deliberately allowed, because playing co-op is still
playing. Only the competitive leaderboard carries real risk.

> **Why unconditional rather than netplay-only.** `UploadScore` calls
> `LeaderboardsNew_Sus.CheckMods`, which detects the mod by **probing directories on disk**. A
> solo run with this mod installed is therefore exactly as detectable as a netplay one — the
> detection has nothing to do with whether a session is running. Gating the block on
> `HasNetplaySessionInitialized()` would have left every singleplayer score exposed.
>
> The player's route to a legitimate leaderboard entry is to remove or disable the mod. This is
> stated plainly in `NETPLAY_CHANGES.md` rather than left as a surprise.
>
> A side benefit: the block no longer depends on session-state bookkeeping, so it cannot be
> broken by a teardown bug of the [P0-5](#p0-5) kind.

**Leaderboard suppression is confirmed correct and sufficient.** `Patches/LeaderBoards.cs`
prefixes `Leaderboards.UploadScore` and returns `false`. Decompilation shows:

- `Leaderboards.UploadScore` is the **only external caller** of
  `SteamLeaderboardsManagerNew.QueueLeaderboardUpload` (VA `0x180529E40`). The only other
  references are internal: `CheckUploadQueue` drains the queue, `UploadLeaderboardScore`
  performs the Steam call.
- `QueueLeaderboardUpload` merely enqueues onto a `Queue<T>`. Blocking entry to `UploadScore`
  means nothing is ever queued, so all three of its upload calls are covered — including the
  two that bypass the game's own `CheckMods` gate.

`Patches/SteamAchievementsManager.cs` was **removed**. It blocked `QueueUpload` and
`TryUploadAchievements`, neither of which is on the path that fires — so it was dead code that
also contradicted the new intent.

### Still open — Steam stats are not actually suppressed

`Patches/SteamStatsManager.cs` blocks `QueueUpload` and `TryUploadStats`. **Neither is the path
the game uses.** The real chain, all confirmed by decompilation and none of it patched:

```
TrackStats.OnEnemyDied            (mod adjusts, allows)
  -> SteamStatsManager.OnStatUpdated        caches into Dictionary<string,int>
  -> SteamStatsManager.Update               on a timer, calls SetCachedStats()
  -> SetCachedStats                         SteamUserStats.SetStat(...)   <- the ONLY SetStat caller
  -> SteamAchievementsManager.Update        StoreStats()                  <- commits everything
```

Two facts make this leak:

1. **`TryUploadAchievements` is nothing but `StoreStats()`** (VA `0x1803EBF10`), and
   `SteamUserStats.StoreStats` is Steamworks' single global commit — it flushes pending
   achievements **and** stats together. There is no achievements-only store.
2. **`TryUnlockAchievement` sets the same pending flag and timer that `QueueUpload` sets**
   (static offsets `+1` and `+4`), and `SteamAchievementsManager.Update` — unpatched — polls
   that flag and calls `StoreStats()`. So unlocking any achievement commits whatever stats
   `SetCachedStats` has already pushed.

**The single choke point is `SetCachedStats`** — a prefix there during netplay would leave
nothing pending on the stats side, so `StoreStats()` from the achievement path would commit
achievements only.

Deliberately **not** done in this pass: the current scope is leaderboards only, and stats are
accepted. Recorded because the existing `SteamStatsManager.cs` patch advertises protection it
does not provide, and `NETPLAY_CHANGES.md` now says so plainly rather than promising otherwise.

A second-order issue if this is ever picked up: blocking the push *during* the session does not
un-inflate the local stat store, which can be pushed later from singleplayer.

### Test

- [x] **Netplay run produces no leaderboard entry.** Confirmed in-game on a two-client localhost
      session, buildid 21750826.
- [x] **Singleplayer run produces no leaderboard entry either** — now the intended behaviour, not
      a bug. Confirmed in-game: `Blocking leaderboard upload (MegabonkTogether is installed)`
      appears both during the netplay session **and** in a later solo run in the same process,
      after networking had been torn down. The unconditional prefix does not depend on session
      state, so there is nothing left here that a teardown bug could break.
- [x] **Leaderboard works with the mod removed/disabled.** Confirmed in-game. This is the
      supported route to a legitimate entry.
- [ ] Confirm an achievement earned during netplay **does** unlock.

> **The singleplayer check found a Critical bug on the way — see [P0-5](#p0-5).** It is no longer
> a *leaderboard* concern, since that block is now unconditional and does not read session state.
> But the same stale flag still gates `SaveManager` and ~38 other patch sites, so singleplayer
> progression was silently not saving after any netplay session. Fixed; needs the re-test in P0-5.

> **Testing caveat.** The above was verified on localhost, which has no packet loss, latency, or
> reordering, and always takes the direct P2P path rather than the relay. That is sufficient for
> *this* item — the patch is an unconditional prefix, not a delivery-sensitive behaviour — but do
> not generalise a clean localhost result to netcode items. See
> [`05-local-testing.md`](05-local-testing.md#what-this-setup-cannot-tell-you).

> Recorded so the mod reliably **avoids** submitting modded runs to leaderboards. Do not attempt
> to influence or work around the game's detection; the goal is that no netplay run ever reaches
> the upload path.

---

<a name="p0-5"></a>
## P0-5 — Netplay flag never cleared on teardown; singleplayer keeps taking the netplay path

**Status:** CONFIRMED by inspection — FIXED, not yet verified in-game
**File:** `src/plugin/Scripts/NetworkHandler.cs` (`ResetNetworking`)

### Symptom

After playing **any** netplay session, returning to the menu and starting a **singleplayer** run:

- progression is **not saved** (`SaveStats`, `SaveProgression`, `SaveConfig` all skipped)
- map generation, spawning, interactables and skin selection keep taking their netplay branches

It persists until the game is restarted, or until another netplay session begins.

> Leaderboards are **not** affected: [P0-0](#p0-0) blocks that unconditionally and never reads
> session state. Silent progression loss is the whole of this bug — which makes it worse, not
> better, since there is no visible symptom at all.

### Root cause

`ISynchronizationService.HasNetplaySessionInitialized()` is:

```csharp
return Plugin.Instance.NetworkHandler.HasFoundMatch.HasValue
    && Plugin.Instance.NetworkHandler.HasFoundMatch.Value;
```

`hasFoundMatch` has exactly two assignments in `NetworkHandler`:

| Where | Value | When it runs |
|---|---|---|
| `OnMatchFound(bool success)` | `success` | a netplay match is found |
| `HandleNetworking()` | `null` | **starting** a session — the connect path |

`ResetNetworking()` — the teardown path, called from `WindowManager`, `NetworkMenuTab` (7 sites)
and `WebsocketClientService` — clears `isConnectedToMatchMaker`, `Plugin.Instance.Mode`,
`isHost`, and every service. **It never touched `hasFoundMatch`.** So the flag latched `true` at
the first match and stayed true for the rest of the process.

Nulling the service references in `ResetNetworking` does not help: the patches hold the DI
singleton, and that singleton reads `Plugin.Instance.NetworkHandler.HasFoundMatch` directly.

### Blast radius

`HasNetplaySessionInitialized()` gates **~40 patch sites**: `SaveManager` (×4),
`SteamStatsManager` (×2), `MapController`, `MapGeneration/*` (×8), `SpawnInteractables` (×3),
`RandomObjectSpawner`, `GenerateTileObjects`, `Interactables/*`, `SkinSelection`, `GameManager`,
`EffectManager`, `Unity/UnityObject`, and more.

**The saving failure is the serious one** — silent progression loss, with no error and no visible
symptom until the player notices their run did not count.

This is the root cause of the "mod breaks singleplayer after playing multiplayer" class that the
**bepinex** skill lists as recurring in this project.

### Fix

One line in `ResetNetworking()`:

```csharp
hasFoundMatch = null;
```

Safe: `Update()` already early-returns on `hasFoundMatch == null` (`NetworkHandler.cs:80`), which
is the correct state once a session has ended.

> **Why this hid so well.** Every netplay test passes — the flag is *supposed* to be true then.
> It only shows up in singleplayer *after* netplay, in the same process. Testing singleplayer with
> the mod disabled — the obvious check — cannot see it either, because the patches are not loaded.

### Test — and the trap in it

1. **Set `AllowSavesDuringNetplay = false`** (the default). This is essential — see below.
2. Play a netplay session, return to the menu.
3. **Without restarting**, play a singleplayer run to completion.
4. **Expected:** progression saves, and the log shows **no**
   `Skipping SaveStats during netplay session` lines during the solo run.

> **Two ways to get a false pass.**
>
> **`AllowSavesDuringNetplay = true` bypasses the bug entirely.** The guard is
> `if (HasNetplaySessionInitialized() || IsLoadingNextLevel()) { if (!AllowSavesDuringNetplay) { skip } }`
> — with the config on, it never skips regardless of the flag's value. Saves will work whether or
> not P0-5 is fixed, so the test proves nothing. It must run with the default `false`.
>
> **Testing with the mod disabled** also proves nothing: the patches are not loaded at all.
>
> The bug only shows as *mod enabled*, *saves not allowed during netplay*, *singleplayer after
> netplay*, *same process*.

---

<a name="p0-1"></a>
## P0-1 — Shrine/pylon/lamp claim clobbering locks the object permanently

**Status:** CONFIRMED
**Files:** `src/plugin/Services/SynchronizationService.cs`
**Affected methods:** `OnStartingToChargingShrine` (L2753), `OnStartingToChargingPylon` (L3052),
`OnStartingToChargingLamp` (L3145), `OnReceivedStartingToChargingShrine` (L2785),
`OnReceivedStartingToChargingPylon` (L3084), `OnReceivedStartingToChargingLamp` (L3177)

### Symptom

Two players touch the same shrine (or pylon, or graveyard boss lamp). The second player's
charge is correctly prevented — but from that moment the object can never be charged again by
anyone, for the rest of the run. Log shows repeated
`"Another player is already charging this shrine. Preventing re trigger."`

### Root cause

The host writes the charger list **before** checking whether the object is already claimed:

```csharp
// SynchronizationService.cs:2769-2779  (current)
var chargers = shrineChargingPlayers.FirstOrDefault(p => p.Key == shrineNetplayId).Value;

shrineChargingPlayers[shrineNetplayId] = [playerManagerService.GetLocalPlayer().ConnectionId];
//  ^ overwrites player 1's claim with player 2

if (chargers != null && chargers.Any())
{
    logger.LogInfo("Another player is already charging this shrine. Preventing re trigger.");
    return false;   // bails out — but the dictionary is already corrupted
}
```

The bail-out is correct. The write that precedes it is not. Sequence:

1. Player 1 starts charging. `shrineChargingPlayers[id] = [P1]`.
2. Player 2 touches the shrine. `chargers` captures `[P1]`, then the dictionary is
   overwritten to `[P2]`, then the method bails because `chargers.Any()` is true.
3. Player 1 stops charging. `OnStoppingChargingShrine` calls `.Remove(P1)` — but the list
   contains `[P2]`, so nothing is removed.
4. The list is permanently non-empty. Every subsequent start bails. The shrine is dead.

Step 3 is also where P0-2 fires.

### Fix

Check first, write second. Apply to all six methods.

```csharp
public bool OnStartingToChargingShrine(uint shrineNetplayId)
{
    var isHost = IsServerMode() ?? false;

    IGameNetworkMessage message = new StartingChargingShrine
    {
        ShrineNetplayId = shrineNetplayId,
        PlayerChargingId = playerManagerService.GetLocalPlayer().ConnectionId
    };

    if (!isHost)
    {
        udpClientService.SendToHost(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
        return false;
    }

    // FIX P0-1: check before writing, and use TryGetValue instead of an O(n) LINQ scan
    if (shrineChargingPlayers.TryGetValue(shrineNetplayId, out var chargers)
        && chargers != null && chargers.Count > 0)
    {
        logger.LogInfo("Another player is already charging this shrine. Preventing re trigger.");
        return false;
    }

    shrineChargingPlayers[shrineNetplayId] = [playerManagerService.GetLocalPlayer().ConnectionId];

    udpClientService.SendToAllClients(message, LiteNetLib.DeliveryMethod.ReliableOrdered);

    return true;
}
```

The `OnReceived*` variants have the same shape, keyed on `shrine.ShrineNetplayId` and
storing `shrine.PlayerChargingId`:

```csharp
// OnReceivedStartingToChargingShrine, host branch
if (shrineChargingPlayers.TryGetValue(shrine.ShrineNetplayId, out var chargers)
    && chargers != null && chargers.Count > 0)
{
    return;
}

shrineChargingPlayers[shrine.ShrineNetplayId] = [shrine.PlayerChargingId];
// ... rest unchanged
```

> `ICollection<uint>.Count` is O(1) on the `List<uint>` actually stored. `.Any()` allocates
> an enumerator. Prefer `.Count > 0` in all six methods.

### Test

1. Two clients, one shrine. Player 1 starts charging, player 2 walks into it, both walk away.
2. Player 1 walks back in. **Expected:** charging starts. **Currently:** blocked forever.
3. Repeat for a pylon and for a graveyard boss lamp.

### IMPLEMENTED and VERIFIED — buildid 21750826

Landed across all twelve methods (shrine / pylon / lamp × local / received × start / stop),
together with [P0-2](#p0-2) and [P2-2](#p2-2). Confirmed working on a two-client localhost
session: the shrine, pylon and lamp all remain chargeable after a second player walks in and out.

**Ownership model — decided, not incidental.** A single owner holds the charge. A second player
entering is rejected without touching the claim, and their exit removes nothing, so the owner's
charge runs to completion undisturbed. If the *owner* leaves while another player is standing
inside, the charge stops and that player must re-enter to take ownership. The alternative — any
occupant keeps it alive — was considered and deliberately not taken.

Two implementation notes for whoever does [P2-3](#p2-3):

- The write is `dict[id] = [localPlayer]`, a **replacement with a single-element list**, so the
  structure can never hold more than one charger despite being typed `ICollection<uint>`. That
  matches the single-owner model but makes the type misleading about intent.
- The pylon path logged `"...charging this shrine..."` — a copy-paste that would have misdirected
  anyone reading logs for exactly this bug. Corrected.

**Verified in the host log:** no `KeyNotFoundException` across two full sessions. The host is the
first client (`I am HOST`), not the matchmaking server — the server never sees game state and has
no charging logs.

---

<a name="p0-2"></a>
## P0-2 — `KeyNotFoundException` in every charging stop path

**Status:** CONFIRMED
**Files:** `src/plugin/Services/SynchronizationService.cs`
**Affected methods:** `OnStoppingChargingShrine` (L2846), `OnStoppingChargingPylon` (L3238),
`OnStoppingChargingLamp` (L3331), `OnReceivedStoppingChargingShrine` (L2878),
`OnReceivedStoppingChargingPylon` (L3272), `OnReceivedStoppingChargingLamp` (L3363)

### Symptom

Host throws `KeyNotFoundException` and the message-handling path aborts. Depending on where
it is caught, this either spams the log or drops the remainder of that frame's message batch.
Most likely to fire on: a stop arriving with no recorded start (packet reorder), a late join,
or a player disconnecting mid-charge.

### Root cause

Unguarded indexer read on a `ConcurrentDictionary`:

```csharp
// SynchronizationService.cs:2864  (current)
var chargers = shrineChargingPlayers.FirstOrDefault(p => p.Key == shrineNetplayId).Value;

shrineChargingPlayers[shrineNetplayId].Remove(playerManagerService.GetLocalPlayer().ConnectionId);
//                   ^^^^^^^^^^^^^^^^ throws KeyNotFoundException if the key is absent
```

Note the code already reads `chargers` — which is `null` when the key is missing — and then
indexes anyway. The null check happens *after* the throw.

`OnReceivedStoppingChargingShrine` (L2884-2885) has the identical defect.

### Fix

Guard, and reuse the reference you already fetched:

```csharp
public bool OnStoppingChargingShrine(uint shrineNetplayId)
{
    var isHost = IsServerMode() ?? false;

    IGameNetworkMessage message = new StoppingChargingShrine
    {
        ShrineNetplayId = shrineNetplayId,
        PlayerChargingId = playerManagerService.GetLocalPlayer().ConnectionId
    };

    if (!isHost)
    {
        udpClientService.SendToHost(message, LiteNetLib.DeliveryMethod.ReliableOrdered);
        return false;
    }

    // FIX P0-2: guard the key, and mutate the reference we already hold
    if (!shrineChargingPlayers.TryGetValue(shrineNetplayId, out var chargers)
        || chargers == null || chargers.Count == 0)
    {
        logger.LogInfo("No one is charging this shrine; ignoring stop.");
        return false;
    }

    chargers.Remove(playerManagerService.GetLocalPlayer().ConnectionId);

    if (chargers.Count > 0)
    {
        logger.LogInfo("Another player is still charging this shrine. Preventing stop trigger.");
        return false;
    }

    udpClientService.SendToAllClients(message, LiteNetLib.DeliveryMethod.ReliableOrdered);

    return true;
}
```

`OnReceivedStoppingChargingShrine` host branch, same shape with `shrine.PlayerChargingId`.

> **Note on the existing behaviour:** in the current code `chargers` and
> `shrineChargingPlayers[id]` are the *same object*, so `chargers.Any()` after the `.Remove`
> does reflect the post-removal state and the logic happens to be correct. Only the
> unguarded indexer is broken. The rewrite above preserves that behaviour while making the
> aliasing explicit.

> **Threading:** the values are plain `List<uint>` inside a `ConcurrentDictionary`. The
> dictionary is concurrent; the lists are not. If message handling can run off the main
> thread, mutating `chargers` is itself a race. This is resolved for free by the Steamworks
> migration (poll-based receive on the main thread) — see
> [`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md). Until then,
> if you want belt-and-braces, swap the value type to a lock-protected wrapper or a
> `ConcurrentDictionary<uint, byte>` used as a set.

### Test

1. Client starts charging a shrine, then disconnects without a clean stop.
2. Host: **expected** a log line and no exception. **Currently:** `KeyNotFoundException`.
3. Also: replay a `StoppingChargingShrine` message for an ID that was never started.

---

<a name="p0-3"></a>
## P0-3 — Non-atomic network ID allocation

**Status:** CONFIRMED — FIXED, not yet verified in-game
**Files:**
- `src/plugin/Services/EnemyManagerService.cs:185`
- `src/plugin/Services/PickupManagerService.cs:46`
- `src/plugin/Services/SpawnedObjectManagerService.cs:50`

The `//TODO: concurrency?` markers sat at `EnemyManagerService.cs:40`,
`PickupManagerService.cs:25`, `SpawnedObjectManagerService.cs:41`, and are now resolved.

> `Sea-Bass-cmd/optimized-netplay` claims to have fixed this in commit `0c7e313`. It did not
> — the automated script's regex failed to match, so only the `//TODO` comments were deleted.
> `git grep Interlocked` across that branch's `src/plugin` returns nothing. Do not assume it
> is done.

### Symptom

Two entities allocated concurrently receive the same network ID, or an ID is skipped. Result:
`TryAdd` fails, the method returns `0`, and the entity is never registered — so it exists on
the host but never spawns on clients, or spawns and is never updatable. Log shows
`"Attempted to add an enemy that already exists. EnemyId: N"`.

Rare, load-dependent, and therefore most likely at 400–600 enemies.

### Root cause

```csharp
// EnemyManagerService.cs:163-175  (current)
public uint AddSpawnedEnemy(Enemy enemy)
{
    currentEnemyId++;                                   // read-modify-write, not atomic
    if (!spawnedEnemies.TryAdd(currentEnemyId, enemy))  // and re-read here
    {
        Plugin.Log.LogWarning($"Attempted to add an enemy that already exists. EnemyId: {currentEnemyId}");
        return 0;
    }

    DynamicData.For(enemy).Set("netplayId", currentEnemyId);

    return currentEnemyId;
}
```

Three separate reads of a shared mutable field around a concurrent insert.

### Fix

```csharp
// EnemyManagerService.cs
private int currentEnemyId = 0;   // was: private uint currentEnemyId = 0; //TODO: concurrency?

public uint AddSpawnedEnemy(Enemy enemy)
{
    // FIX P0-3: atomic allocation, single read
    var newId = (uint)System.Threading.Interlocked.Increment(ref currentEnemyId);

    if (!spawnedEnemies.TryAdd(newId, enemy))
    {
        Plugin.Log.LogWarning($"Attempted to add an enemy that already exists. EnemyId: {newId}");
        return 0;
    }

    DynamicData.For(enemy).Set("netplayId", newId);

    return newId;
}
```

Because `Interlocked.Increment` returns the *new* value, the first id handed out is 1 and `0`
stays reserved as the failure sentinel these methods already return — the same guarantee the
`++`-then-read version had.

Apply the same shape to `PickupManagerService.AddSpawnedPickup` and
`SpawnedObjectManagerService.AddSpawnedObject`.

Reset paths: `PickupManagerService.ResetForNextLevel` and
`SpawnedObjectManagerService.ResetForNextLevel` zero their counter, and now do it via
`Interlocked.Exchange` for symmetry. **`EnemyManagerService.ResetForNextLevel` does not zero
`currentEnemyId`** — an earlier draft of this entry claimed it did, at a line number that has
never held that statement. Leave it alone: enemy ids staying monotonic across a stage boundary
is the safer behaviour, since a client holding a stale `netplayId` from the previous stage
cannot then collide with a freshly allocated one. The two that do reset are only safe because
they `Clear()` the dictionary in the same call.

### Test

Hard to reproduce deterministically. Add a temporary assertion that the returned ID is
strictly greater than the previous one, and run a final swarm at 4+ players. Absence of the
`"already exists"` warning over several runs is weak evidence; the `Interlocked` version is
correct by construction.

---

<a name="p0-4"></a>
## P0-4 — `NullReferenceException` on enemy target assignment

**Status:** CONFIRMED — FIXED and **VERIFIED in-game** (3-player session, buildid 21750826: two
peer disconnects with enemies spawning throughout, no NRE in `init_PostFix` in any of the three
logs)
**File:** `src/plugin/Patches/Enemies/Enemy.cs:51-84`

### Symptom

Host throws NRE inside the `Enemy.InitEnemy` postfix. Since this is a Harmony patch on enemy
spawn, a throw here can abort enemy initialisation entirely.

### Root cause

Two separate nullable derefs in the same block.

```csharp
// Enemy.cs:59-63  (current)
var randomPlayer = playerManagerService.GetNetPlayerByNetplayId(id);

__instance.target = randomPlayer.Rigidbody;      // randomPlayer may be null
DynamicData.For(__instance).Set("targetId", randomPlayer.ConnectionId);
```

`GetNetPlayerByNetplayId` returns `null` when the connection id has no entry in
`spawnedPlayers` — the peer disconnected. Nothing guards it.

Drop-in joining is **not** a factor: a run can only start once everyone is in the lobby, so
every netplayer is spawned before the first enemy. The reachable paths are all disconnects:

- **3+ players.** `UdpClientService.cs:435` only returns to the main menu when
  `gamePeers.IsEmpty`. With a third player still connected the session continues, the host
  broadcasts `PlayerDisconnected`, and `RemovePlayer` drops the departed peer from
  `spawnedPlayers` while enemies keep spawning. This window is the whole rest of the run.
- **2 players, teardown race.** The session does end here (confirmed in a live host log), but
  `Plugin.GoToMainMenu()` is not instantaneous — `InitEnemy` can still fire between
  `RemovePlayer` and scene unload.
- **Stale queue ids.** `RemovePlayer` cleans `getNetplayerPositionQueue`
  (`PlayerManagerService.cs:238`) only if it gets that far. Three early returns above it —
  player absent, `GameManager.Instance.player` null, netplayer already destroyed — skip the
  cleanup, leaving the departed peer's id queued for `TryGetGetNetplayerPosition` to hand
  back later.

The guard also covers a case a plain dictionary lookup does not: on the third path above,
`spawnedPlayers` can still hold a *destroyed* `NetPlayer`. `NetPlayer` is a `MonoBehaviour`,
so `!= null` uses Unity's overloaded operator and treats the destroyed object as null —
which is the behaviour we want, since `.Rigidbody` on it would throw.

`GetLocalPlayer()` is declared `Player?` and is likewise dereferenced unguarded — twice, once
per branch — so simply falling back to it moves the NRE rather than removing it.

### Fix

Keep the physics-target assignment (this is the line `Sea-Bass-cmd` accidentally dropped),
hoist the local-player lookup so all three uses share one guard, and add the netplayer guard:

```csharp
var host = playerManagerService.GetLocalPlayer();

if (playerManagerService.TryGetGetNetplayerPosition(out uint id))
{
    if (host != null && host.ConnectionId == id)
    {
        DynamicData.For(__instance).Set("targetId", host.ConnectionId);
    }
    else
    {
        var randomPlayer = playerManagerService.GetNetPlayerByNetplayId(id);

        if (randomPlayer != null)
        {
            __instance.target = randomPlayer.Rigidbody;
            DynamicData.For(__instance).Set("targetId", randomPlayer.ConnectionId);
        }
        else if (host != null)
        {
            // FIX P0-4: that peer's netplayer is gone — they disconnected. Fall back to the
            // host so targetId is never left unset.
            DynamicData.For(__instance).Set("targetId", host.ConnectionId);
        }
    }
}
else if (host != null)
{
    DynamicData.For(__instance).Set("targetId", host.ConnectionId);
}
```

A null `host` leaves `targetId` unset rather than throwing — that state only occurs during
teardown, and an unset target is recoverable where an aborted `InitEnemy` is not. Not logged:
`InitEnemy` runs once per spawn, so a warning there floods during a swarm.

> Do **not** copy the Sea-Bass version of this block. It collapses the branch in a way that
> silently deletes `__instance.target = randomPlayer.Rigidbody`, leaving the enemy's physics
> target pointing at the host while the network says otherwise. `TargetSwitcher.Update`
> repairs it, but only after a random 2–6 s delay — so every freshly-spawned enemy beelines
> at the host first.

### Test

Needs **3 players** — at 2 the disconnect ends the session almost immediately and the window
is too narrow to hit reliably. Start a 3-player run, have one peer alt-F4 mid-run while
enemies are spawning around them, and keep the remaining two playing for another stage. Watch
the host log for NREs in `init_PostFix`, and confirm enemies keep spawning and re-targeting
rather than freezing at their spawn point.

---

<a name="p1-1"></a>
## P1-1 — Client-originated XP / gold / encounter-close never reach peers

**Status:** ~~CONFIRMED~~ → **NOT A DEFECT. This entry was wrong when written.** Already
handled in upstream `f023d1e` (2026-02-12), five months before this plan was written
(2026-07-29). No code change made.
**File:** `src/plugin/Services/UdpClientService.cs:992-1013` (`HandleMessage`, host branch)

### Why the entry was wrong

The relay does not live in `SynchronizationService`. It lives one layer up, in the transport,
and the original analysis only read the sync service.

`UdpClientService.HandleMessage(message, netPeerId)` splits on `isHost`. The **host branch**
already forwards all three:

```csharp
case AddXp addXp:
    EventManager.OnAddXp(addXp);
    SendToAllClientsExcept(netPeerId, addXp.OwnerId, addXp);      // sender excluded
    break;
case EncounterClosed encounterClosed:
    encounterService.AddClosedEncounterForPlayer(encounterClosed.OwnerId);
    if (encounterService.IsClosable())                            // vote, then broadcast
    {
        SendToAllClients(closeMessage, DeliveryMethod.ReliableOrdered);
        EventManager.OnCloseEncounter(closeMessage as CloseEncounter);
    }
    break;
case GoldChanged goldChanged:
    EventManager.OnGoldChanged(goldChanged);
    SendToAllClientsExcept(netPeerId, goldChanged.OwnerId, goldChanged);
    break;
```

and the **client branch** (L727-735) applies what arrives. The full round trip closes:

```
Client B  ChangeGold(+50) locally, SendToHost(GoldChanged{OwnerId=B})
Host      EventManager.OnGoldChanged  → applies locally
          SendToAllClientsExcept(B's peer id, ...) → every peer except B
Client C  EventManager.OnGoldChanged  → applies       ✓ C is in sync
Client B  excluded, no echo                           ✓ no double-count
```

`EncounterClosed` is deliberately not a straight relay — it is a per-player vote accumulated by
`encounterService`, and the resulting `CloseEncounter` broadcast fires once the set is
complete. That is correct as written, not a missing forward.

`OnReceivedChangeGold` in `SynchronizationService` really does only apply locally, exactly as
this entry quoted. That is the right split: only the transport layer holds the LiteNetLib peer
id the exclusion needs.

### The fix this entry proposed would have introduced the bug it warns about

It suggested calling, from the sync service:

```csharp
udpClientService.SendToAllClientsExcept((int)changed.OwnerId, changed.OwnerId, changed);
```

`SendToAllClientsExcept` filters with `gamePeers.Where(p => p.Value.Id != netPlayerId)`
(`UdpClientService.cs:1731`). `p.Value.Id` is the **LiteNetLib peer id** — a small int LiteNetLib
assigns (0, 1, 2…) — not the game connection id. `OwnerId` is a game connection id like
`84989678`. The two are different id spaces, so the filter would never match, the originator
would receive the echo, and `ChangeGold` — which applies a *delta* — would double-count.

That is precisely the duplication exploit this entry set out to prevent. It would also have
relayed twice, since the transport already forwards. The entry's own warning to "check
`SendToAllClientsExcept`'s two parameters against its implementation" was the right instinct
aimed at the wrong line.

Correct usage is what the existing ~25 call sites do: pass the `netPeerId` handed to
`HandleMessage`, which came from `HandleMessage(deserializedMsg, peer.Id)`.

### Still worth knowing: the trap this entry identified is real

The reasoning about `SendToAllClients` was sound, and applies to any *future* relay added here.
It fans out to every peer in `gamePeers` with no sender filter, so the message is echoed back
to the client that sent it. For `AddXp` that is harmless — `playerXp.xp = xp.Xp` is an absolute
assignment and therefore idempotent. For `GoldChanged` it would be a **duplication exploit**,
because `ChangeGold(changed.Amount)` applies a *delta*:

```
Client A picks up gold  → ChangeGold(+50) locally
                        → sends GoldChanged{Amount=50, OwnerId=A} to host
Host                    → relays to ALL clients, including A
                        → applies ChangeGold(+50) locally
Client A receives echo  → ChangeGold(+50) again        <-- A now has +100
```

The shipped code already uses `SendToAllClientsExcept` for exactly this reason. **Any new
delta-carrying message relayed from the host must do the same** — absolute-value messages are
forgiving, deltas are not.

### Unverified: sender exclusion in relay mode

Only the direct-peer path was traced. `SendToAllClientsExcept`'s relay branch
(`UdpClientService.cs:1687-1714`) builds `RelayEnvelope.ToFilters` by looking `sender` up in
`gamePeersIntroducedByRelay`, and falls back to an **empty filter list** when the lookup misses.
An empty filter presumably means "send to all relayed peers" — harmless when the sender is a
direct peer, but a double-count if it can ever be reached with the sender among the relayed
set. Not traced through the server's forwarding logic and not tested. **UNVERIFIED** — worth a
look before adding any new delta message, not urgent otherwise.

### Test

No change to test. If confirming the existing behaviour anyway: three players, **client B**
(not host) picks up gold; B's total must increase by exactly the pickup amount and client C's
total must increase too. Force relay mode to exercise the unverified path above.

---

<a name="p1-2"></a>
## P1-2 — Legendary (golden) shrine state not synced

**Status:** CONFIRMED
**Files:** `src/common/Messages/GameNetworkMessages/SpawnedObject.cs`,
`src/plugin/Services/SynchronizationService.cs` (`SendSpawnedObject`),
`src/plugin/Patches/ChargeShrine.cs`

### Symptom

`ChargeShrine.isGolden` is decided host-side at spawn and never transmitted. Clients render
and treat legendary shrines as ordinary ones.

### Fix

Three parts. **Do P1-3 (version gate) first** — this changes the wire format.

**1. Extend the message.** `src/common/Messages/GameNetworkMessages/SpawnedObject.cs`:

```csharp
[MemoryPackable]
public partial class Specific
{
    public int ShadyGuyRarity { get; set; }
    public bool? IsGoldenShrine { get; set; }   // NEW — see P1-3, this breaks the wire format
}
```

**2. Populate it on send**, in `SendSpawnedObject`, alongside the existing shady-guy and
microwave rarity capture:

```csharp
var chargeShrine = obj.GetComponentInChildren<ChargeShrine>();
bool? isGoldenShrine = chargeShrine != null ? chargeShrine.isGolden : (bool?)null;

// ... in the message initialiser:
SpecificData = new Specific
{
    ShadyGuyRarity = rarity.HasValue ? (int)rarity.Value : -1,
    IsGoldenShrine = isGoldenShrine
}
```

**3. Apply it on receive**, in the spawned-object handler, next to where `ShadyGuyRarity` is
applied:

```csharp
if (toSpawn.SpecificData.IsGoldenShrine.HasValue)
{
    var chargeShrine = spawned.GetComponentInChildren<ChargeShrine>();
    if (chargeShrine != null)
    {
        chargeShrine.isGolden = toSpawn.SpecificData.IsGoldenShrine.Value;
    }
}
```

### On the `Start` postfix

`Sea-Bass-cmd` also adds a `ChargeShrine.Start` postfix that re-reads the flag from
per-object state. The reasoning is that Unity may run `Start()` after the receive handler has
already set `isGolden`, overwriting it.

That is a real ordering hazard, but the postfix as written only helps if the value was
already stored somewhere the postfix can read — it is a retry, not a fix.

> **RESOLVED — do not take the postfix.** `ChargeShrine$$Start` (VA `0x1804C2A60`) was
> decompiled in full: it rotates `circleProgress`, zeroes `audioStart` alpha, deactivates the
> zone block, builds two `MaterialPropertyBlock`s, applies colours, disables the renderer and
> fires the spawn action. **It never touches `isGolden`, and never reads `goldChance`.** Steps
> 1–3 above are sufficient and the postfix is dead weight.
>
> (`Start` *does* call `Random.ColorHSV` — that is the rune-stone's cosmetic colour, not the
> golden roll. The two were easy to conflate from field offsets alone.) See
> [`../reverse-engineering/01-investigation-targets.md`](../reverse-engineering/01-investigation-targets.md#chargeshrine).

### Test

Host until a legendary shrine spawns. Confirm the client sees the golden visual and receives
the legendary reward.

---

<a name="p1-3"></a>
## P1-3 — No protocol version gate

**Status:** CONFIRMED (by absence) — **attempted fix DID NOT WORK, disproved in-game and
reverted.** Deferred to the Steamworks migration. Read "Why the connect-key gate never fires"
below before attempting this again.
**Files:** `src/common/Messages/`, connection handshake in `UdpClientService.cs` /
`WebsocketClientService.cs`

### Symptom

A host and a client running different mod builds connect successfully, then desync in
confusing, hard-to-diagnose ways — or crash on deserialization.

### Root cause

All network messages use `MemoryPack`, which serializes members **positionally** for
`[MemoryPackable]` types. Adding, removing, or reordering a field in any message under
`src/common/Messages/` changes the wire layout. There is no version exchange at connect time,
so a mismatch is not detected.

This is not hypothetical: P1-2 above adds a field to `Specific`, and
`Sea-Bass-cmd/optimized-netplay` already did so without a gate.

### Attempted fix — DISPROVED, do not retry

> Everything in this section was implemented and **does not work**. It is kept because the
> reasoning looks correct and someone will propose it again. Step 2 is the broken step; read
> "Why the connect-key gate never fires" below before writing any of it.

**1. Define a protocol version** separate from the plugin's semantic version — bump it only
when the wire format changes:

```csharp
// src/common/ProtocolVersion.cs
namespace MegabonkTogether.Common
{
    public static class Protocol
    {
        /// <summary>
        /// Bump on ANY change to a type under Messages/ — added field, removed field,
        /// reordered member, changed type. MemoryPack is positional.
        /// </summary>
        public const int Version = 1;
    }
}
```

**2. Exchange it in the handshake.** `UdpClientService.cs:186` currently connects with a
placeholder key:

```csharp
netManager.Connect(target, "yourKey"); //TODO: technically we should use a key but do we really care ? will
```

and `UdpClientService.cs:201` accepts any request:

```csharp
Plugin.Log.LogInfo($"Got a connection request from remote"); //TODO: technically we should validate the request key
```

Both TODOs resolve here. Send the protocol version as the connect key and validate it host
side, rejecting mismatches with a clear reason so the client can surface a real message
rather than a generic timeout.

**3. Surface it in the UI.** On rejection, tell the player which side is out of date.

### Why the connect-key gate never fires

**Disproved in-game, buildid 21750826.** A host built with `Protocol.Version = 2` accepted a
client on the shipped build (which sends the old `"yourKey"` placeholder) and the session
played normally. No rejection, no warning.

`ConnectionRequestEvent` is not raised in this topology. After NAT introduction **both** peers
call `netManager.Connect` at each other — the host's own log shows `Connecting...` before
`Host: Client connected`. That is a simultaneous cross-connect, and LiteNetLib 1.3.5 reconciles
it inside `NetPeer.ProcessConnectRequest` (`ConnectRequestResult.P2PLose` / `Reconnection` /
`NewConnection` are all present in the shipped DLL). When a peer object already exists for the
remote endpoint in `Outgoing` state, the incoming connect is matched against that attempt and
the connection-request handler is skipped entirely.

The tell was already in the first host log captured for P1-7, before any of this was written:
a successful two-player session contains **no** `Got a connection request from remote` line,
even though that log statement predates the version gate. The handler has never run on this
path.

The connect key is therefore write-only. Any gate that depends on reading it — including the
`request.Reject` payload and the "which side is out of date" message built on top of it — is
dead code in the NAT-punch flow.

`ConnectionRequestEvent` still fires for a cold inbound connection with no matching outgoing
attempt, so the code is not harmful, just ineffective. **Do not delete it and assume the
problem is gone; it was never solved.**

### Reverted

`src/common/Protocol.cs`, the `ConnectionRequestEvent` validation, the rejection payload and
its three localization strings were all backed out once the gate was disproved. The two
`//TODO` comments about the connect key are replaced with comments recording *why* a gate does
not belong there, so the next reader does not re-derive this.

Also note the relay topology, which the connect-key approach could not have covered either:
**two relayed peers never exchange a `ConnectionRequest` with each other.** Each connects to
`RendezVousServer` and the server forwards packets between them. The relay connect key cannot
carry a version either — `RendezVousServer` parses it strictly as `id|endpoint|RELAY`
(`RendezVousServer.cs:311-328`) and rejects anything whose third part is not `RELAY`, so
riding along needs a coordinated server deploy.

### Where this goes instead: Steamworks lobby metadata

**Decision: deferred to the Steamworks migration.** Publish the protocol version as lobby
metadata via `SteamMatchmaking.SetLobbyData` and filter incompatible lobbies out of the browser
before anyone connects. That is strictly better than anything achievable on the LiteNetLib
path: it works for both direct and relayed sessions, it needs no new `[MemoryPackUnion]` tag,
and an incompatible lobby is never joined rather than being joined and then torn down.

Two requirements carried over from this attempt, both settled:

- **Treat silence as a mismatch.** A peer that publishes no protocol version is an old build
  and must be refused, not accepted by default. This is the only way a new host rejects a
  5.1.0 client — and it means the first release carrying the gate cannot play with any earlier
  version, which needs a loud changelog note rather than a quiet one.
- **Do not gate on the plugin's semantic version.** Two releases differing only in gameplay or
  UI stay wire-compatible; bump the protocol number only when a type under `Messages/` changes.

The intermediate option — an application-level handshake message exchanged after connect —
was considered and rejected. It needs a new union tag, an old peer hitting an unknown tag
throws in the deserializer rather than reporting a mismatch, and rejecting old builds requires
either a disconnect timer (which can fire wrongly on a laggy link and kill a legitimate
player) or a new gate at lobby start. Not worth it for a stopgap that Steamworks removes.

### Consequence for P1-2

P1-2 adds a field to `Specific`, and this entry was supposed to unblock it. **It does not.**
Until the Steamworks gate exists, any change to a type under `Messages/` is an undetectable
break between mod versions — so P1-2 either waits, or ships knowing that a mismatched pair
desyncs silently rather than refusing to connect.

### Test

There is nothing to test at present; the code is reverted. When the Steamworks gate is built:
confirm an incompatible lobby is filtered out of the browser rather than joined and dropped,
confirm a lobby publishing no version is treated as incompatible, and confirm two peers on the
same build still match — test the matching case first, since a mistake here breaks all
multiplayer rather than failing quietly.

---

<a name="p1-4"></a>
## P1-4 — `GetAllPlayersAlive()` allocates on every call

**Status:** CONFIRMED — FIXED, not yet verified in-game (no profiler capture taken)
**File:** `src/plugin/Services/PlayerManagerService.cs:133-136`

### Root cause

```csharp
public IEnumerable<Player> GetAllPlayersAlive()
{
    return [.. players.Where(p => p.Value.Hp > 0).Select(p => p.Value)];
}
```

The collection expression materialises a `Player[]` on every call, plus two LINQ iterator
objects. Called from:

- `GameBalanceService.PlayersCount` — which is read by `GetCreditsTimerMultiplier`,
  `GetEnemyHpMultiplier`, `GetFreeChestSpawnRateMultiplier`, `GetPickupXpValue`,
  `GetMaxEnemiesSpawnable`, `GetBossLampRequiredCharge`
- `TargetSwitcher.PickANewTarget` (L60) and `PickACloseTarget` (L80), each adding a `.ToList()`
- `FinalFightController` (L111, L136, L161)
- `SynchronizationService` (L2939-2940), which calls it **twice in consecutive lines**

`TargetSwitcher` is a per-enemy `MonoBehaviour`, so at 600 enemies this is the single worst
allocation site in the mod. See
[`04-performance-and-gc.md`](04-performance-and-gc.md#targetswitcher).

> `Sea-Bass-cmd` "fixes" this by adding an `is ICollection<Player>` test at the *call* site in
> `GameBalanceService`. Since the return value is already an array, the test succeeds and one
> enumerator allocation is saved — while the array and both iterators, which dominate, are
> untouched. Do not bother; fix the source.

### Fix

Maintain a reusable buffer and expose a non-allocating overload:

```csharp
// PlayerManagerService.cs
private readonly List<Player> alivePlayersBuffer = new(8);

/// <summary>
/// Non-allocating. The returned list is reused — copy it if you need to retain it,
/// and do not call this re-entrantly.
/// </summary>
public IReadOnlyList<Player> GetAllPlayersAliveNonAlloc()
{
    alivePlayersBuffer.Clear();
    foreach (var kv in players)
    {
        if (kv.Value.Hp > 0) alivePlayersBuffer.Add(kv.Value);
    }
    return alivePlayersBuffer;
}

/// <summary>Cheap count with no allocation at all.</summary>
public int GetAlivePlayerCount()
{
    int n = 0;
    foreach (var kv in players) if (kv.Value.Hp > 0) n++;
    return n;
}
```

Keep the existing `GetAllPlayersAlive()` for callers that retain the result. Then:

- `GameBalanceService.PlayersCount` → `playerManagerService.GetAlivePlayerCount()`
- `TargetSwitcher.PickANewTarget` / `PickACloseTarget` → `GetAllPlayersAliveNonAlloc()`,
  dropping the `.ToList()`
- `SynchronizationService` L2939-2940 → call once, reuse

> The buffer is single-threaded by assumption. That holds if all callers are on the Unity
> main thread — verify for `SynchronizationService`, which handles network messages. If it
> does not hold today, it will after the Steamworks migration; until then, give
> `SynchronizationService` its own buffer instance rather than sharing.

### What landed

Both helpers added. The thread-safety question above was resolved by **not sharing the buffer
with the network path at all**:

| Caller | Now uses | Why |
|---|---|---|
| `GameBalanceService.PlayersCount` | `GetAlivePlayerCount()` | only ever wanted the length; allocates nothing and has no shared state, so thread-safety is moot |
| `TargetSwitcher.PickANewTarget` / `PickACloseTarget` | `GetAllPlayersAliveNonAlloc()` | the hot path, and unambiguously main-thread (Unity `Update`) |
| `SynchronizationService:3009` | `GetRandomPlayerAliveConnectionId()` | see below |
| `FinalFightController` ×3 | `GetRandomPlayerAliveConnectionId()` | see below |
| `SynchronizationService:3652`, `:3697`, `:3886` | unchanged | they feed `ReTargetEnemies`, run once per death/disconnect, and already `.ToList()`. Cold path — not worth the buffer's aliasing risk |

`GetAllPlayersAliveNonAlloc()` is therefore called from exactly one file, on the main thread.
`SynchronizationService` never touches it, so the shared buffer cannot be reached from a network
handler and no second buffer instance is needed.

### Four latent P1-6 crashes found on the way through

Mechanically replacing the call sites surfaced the **same defect [P1-6](#p1-6) fixed in
`ReTargetEnemies`**, in four more places that P1-6 never mentioned:

```csharp
var allPlayers = playerManagerService.GetAllPlayersAlive();
var randomIndex = UnityEngine.Random.Range(0, allPlayers.Count());
var targetPlayer = allPlayers.ElementAt(randomIndex);   // throws when the set is empty
```

`Random.Range(0, 0)` returns `0`, and `ElementAt(0)` on an empty sequence throws
`ArgumentOutOfRangeException`. Identical shape, no guard, at `SynchronizationService:3009` and all
three `FinalFightController` prefixes.

The `FinalFightController` three are the worse ones: they are **Harmony prefixes**, so the throw
escapes into the IL2CPP trampoline rather than being caught by `MainThreadDispatcher` — the same
"worse failure mode" P1-6 identified for its local path. All three sit on final-boss orb spawning,
which is precisely when players are dying.

All four now use `PlayerManagerService.GetRandomPlayerAliveConnectionId()`, which already existed,
already guards the empty case, already logs it, and does the pick in one pass instead of
enumerating twice. Nothing new was written — the fix was to stop hand-rolling a guarded helper
that was already there.

> **Lesson for P1-6:** its conclusion — "guard inside `ReTargetEnemies` so a fourth caller cannot
> reintroduce it" — was right about that method and wrong about the codebase. The pattern was
> already duplicated in four unrelated methods. A repo-wide grep for
> `Random.Range` + `ElementAt` is the check that would have caught it; it now returns only
> guarded sites.

### Test

Unity Profiler → GC Alloc, during a final swarm at 4+ players. Compare before/after. **Not done —
no capture has been taken, so the improvement is reasoned, not measured.**

Separately, for the crash fixes: reach the final boss in a 3+ player session and have players die
during the orb phase. **Expected:** no `ArgumentOutOfRangeException`, and specifically no
`[Error:Il2CppInterop] During invoking native->managed trampoline` from the
`FinalFightController` prefixes.

---

<a name="p1-5"></a>
## P1-5 — Damage ownership misattributed through shared static `DamageContainer`s

**Status:** Decompilation CONFIRMED (Ghidra, buildid 21750826) — but the mechanism was
**write-only dead code**, now DELETED. The stated symptom, if real, has a different cause; see
"Where attribution actually happens" below.
**Files:** ~~`src/plugin/Patches/WeaponUtility.cs:51`~~ (removed)

### Symptom

Gold and kill credit are attributed to the wrong player, intermittently, and more often under
load. Item- and passive-sourced damage is usually correct; damage through weapons and the shared
combat utilities is not.

> **Caveat added after investigation:** this symptom was never tied to the `DamageContainer`
> path by observation — the link was inferred from the decompilation. Since that path turns out
> to have no consumer, the symptom cannot have come from it. Treat the symptom as **unattributed**
> until reproduced.

### Root cause

Three facts, each confirmed by decompilation:

1. **`DamageContainer` instances are recycled, not allocated per hit.** `Reuse(float, string)`
   takes the constructor's exact signature and re-initialises all 13 fields in place, with no
   allocation (VA `0x1804A64D0`).
2. **22 types cache a container as a field — and three of them are `static`.**
   `DamageUtility`, `WeaponUtility` and `XpDamager` hold theirs in `_StaticFields`: a *single
   instance shared process-wide*. The other 19 (items, passives, projectiles) are per-instance.
3. **`WeaponUtility.GetDamageContainer` ignores the `recycleDc` argument it is handed** and
   returns the static `weaponDc` regardless (VA `0x180434FF0`).

So every weapon attack receives **the same object**. A side table keyed on managed reference
identity — which is what both `DynamicData` and any `ConditionalWeakTable` replacement key on —
collapses all of them into one entry. Last writer wins.

The existing "already assigned?" guard at `WeaponUtility.cs:51` makes this **worse**: it reads a
shared static's stale entry and concludes it is valid, so it skips the reassignment that would
have corrected the owner.

**This also explains the intermittency.** The 19 per-instance holders belong to one player for
their lifetime, so ownership keyed on those is *accidentally* correct. Only the three shared
statics misattribute — which is why some damage sources look fine and others do not.

> Plausible but **unproven**: this may underlie the gold-desync reports patched three times in
> the changelog (v4.0.1, v4.0.2, v4.0.3). Those fixes addressed shared-experience delta
> computation, which is a different mechanism. Do not assume this closes them — verify.

### The missing check: nothing ever read it

The decompilation above is correct and the reasoning from it is sound. What neither established
is whether the `ownerId` being clobbered was **used**. It was not.

A repo-wide search for a read of a `DamageContainer`'s `ownerId` returns exactly one site:

```
src/plugin/Patches/WeaponUtility.cs:51   ← the postfix's own "already assigned?" guard
```

That guard reads a value only that same postfix writes. There is no other consumer anywhere in
the plugin. `DynamicData.For(<a DamageContainer>)` appeared in exactly one place in the entire
codebase — the same method.

So the shared-static collapse was real and had **no observable effect**: it corrupted a value
nobody consumed. Fixing the attribution would have fixed nothing, and a spare-field or
`Reuse`-hook approach would have been effort spent making a dead value accurate.

### Fix — deleted, not repaired

`GetDamageContainer_Postfix` is removed. `GetDamageContainer` is on the per-hit path, so this
was a `DynamicData` lookup plus a possible dictionary write on **every weapon attack** in the
game, for a value with no reader. The `LightningStrike` postfix in the same file is unrelated
and stays.

Removing it changes no behaviour by construction — a write-only value cannot influence anything.

### Where attribution actually happens

None of these touch the `DamageContainer`:

| Path | Mechanism |
|---|---|
| Kill / money-flying / item procs | `ITrackerService.currentPlayerId`, set around `enemy.EnemyDied(...)` on the receive path and read in `EnemyPatch.EnemyDied_Prefix` |
| Constant attacks (Aura, Aegis, Laser, DragonBreath…) | `ownerId` on the **attack instance**, set in `NetPlayer.RefreshConstantAttack` |
| Projectiles | `ownerId` on the **projectile instance**, set per-type in `SynchronizationService` |
| Pickups | `ownerId` on the **pickup**, set in `PickupManager` / `SynchronizationService` |

**If the reported symptom is real, `ITrackerService` is the place to look.** `currentPlayerId` is
a single process-wide mutable field, and `UnsetCurrentPlayerId()` clears it unconditionally — so
any nesting or interleaving of `EnemyDied` (chain damage, explosions, a remote death processed
while a local one is in flight) credits the wrong player or nobody. That is a far better fit for
"intermittent, worse under load" than a value nothing reads.

#### Named suspect: `ProjectileBase.HitEnemy`'s set/unset pair is asymmetric

Found while instrumenting, by inspection rather than from a log. There are three set/unset pairs:

| Site | Sets when | Unsets when |
|---|---|---|
| `ProjectileBase.cs:71` / `:91` | **host AND** the weapon maps to a remote netplayer | **always** |
| `SynchronizationService.cs:1551` / `:1555` | always | always |
| `SynchronizationService.cs:1617` / `:1621` | always | always |

The first pair does not balance. `HitEnemy_Prefix` sets only when `isServer` *and*
`GetNetPlayerByWeapon(...)` resolves — a local player's own projectile returns null and sets
nothing — while `HitEnemy_Postfix` unsets on every call. Harmony also runs postfixes when a
prefix returns `false`, and this prefix returns `isServer`, so on a **client** the postfix still
fires having never set anything.

That gives a concrete misattribution path:

```
Client receives EnemyDied      → SetCurrentPlayerId(died.DiedByOwnerId)
  enemy.EnemyDied(dc)
    → a projectile HitEnemy fires during it
      → HitEnemy_Postfix         → UnsetCurrentPlayerId()   ← outer attribution destroyed
  EnemyPatch.EnemyDied_Prefix   → GetCurrentPlayerId() is now null
                                → falls through to RegisterTrack()
                                → credit lands on the LOCAL player
```

`EnemyDied_Prefix` (`Enemy.cs:124-127`) only skips `RegisterTrack()` when the current id is
present *and* is not the local player. A null reads as "mine", so a stray unset does not merely
lose credit — it actively awards it to whoever is running the code.

**Still UNVERIFIED.** This is inferred from control flow, and the P1-3 and P1-5 lessons both say
inference is not evidence. The instrumentation below exists to settle it.

#### Instrumentation (landed)

`TrackerAttributionDiagnostics` in `src/plugin/Services/TrackerService.cs`, following the
`TransformFallbackDiagnostics` shape: count on the anomalous branch, report at most once per 5s
with counts since the last report, cleared by `NetworkHandler.ResetNetworking()`.

| Counter | Meaning |
|---|---|
| `overwrite-while-set` | `Set()` while a **different** id was live — outer attribution lost. Logs the id pair. |
| `redundant-set` | `Set()` with the same id — harmless, but proves nesting happens |
| `unset-while-clear` | `Unset()` with nothing set — the `ProjectileBase` asymmetry above |
| `track-with-no-owner` | `RegisterTrack()` with no id set — credit defaulted to the local player |
| `cross-thread` | calls arriving on more than one thread — would be a race, a *different* bug |

Two deliberate choices. It throttles on `DateTime.UtcNow`, not `Time.unscaledTime`, because
`UdpClientService.Poll()` also runs on a background task during connection setup and a Unity API
off the main thread throws — a diagnostic must not be able to crash what it measures. And it
stays silent when every counter is zero, so a healthy session produces no noise.

**It is built to falsify.** All counters zero across a real 3-player session with chain damage
means this hypothesis is wrong and this whole subsection should be struck.

#### Results — 3-player session, buildid 21750826

| Counter | Host | Client 2 | Client 3 | Reading |
|---|---|---|---|---|
| `cross-thread` | 0 | 0 | 0 | **No race.** That hypothesis is dead. |
| `overwrite-while-set` | 0 | 0 | 0 | **No live attribution replaced by a different one.** The primary hypothesis — credit stolen mid-flight — is not happening. |
| `unset-while-clear` | 0 | up to 657/5s | up to 1042/5s | **The `ProjectileBase.HitEnemy` asymmetry is real**, exactly as predicted, and client-only. |
| `track-with-no-owner` | 2–11/5s | 0 | 0 | **False positive — counter removed.** See below. |

**Two defects in the instrumentation, both mine.**

**1. `track-with-no-owner` measured nothing.** On the host, killing an enemy with your own weapon
leaves `currentPlayerId` null *by design* — `HitEnemy_Prefix` only sets an owner when the weapon
maps to a **remote** netplayer. So a null owner is the normal "I killed it" path, and
`EnemyDied_Prefix` treating null as "mine" is correct there. The host figure was just its own kill
rate; clients read 0 because their kills arrive via `OnReceivedEnemyDied`, which always sets.
Counter deleted rather than relabelled.

**2. The damaging case was never counted.** `RecordUnset(wasSet)` only incremented when
`!wasSet` — the *harmless* direction. The dangerous case, an unbalanced unset clearing a live
attribution it did not set, has `wasSet == true` and was lumped in with every legitimate paired
unset. So the run could rule out a race and an overwrite but **could not rule out the actual
bug**. Now fixed with depth tracking: `setDepth` increments on set, decrements on unset, and a
decrement that would go negative *while something was live* increments `UNBALANCED-UNSET`.

**Status after this run:** race ruled out, overwrite ruled out, asymmetry confirmed real but its
harmfulness still unmeasured. `UNBALANCED-UNSET` is the number that settles it; it did not exist
during this run. Needs one more 3-player session.

#### SETTLED — negative. The tracker is not the cause either.

That session ran, with all three instances on the current build. A client reported 17 windows:

```
UNBALANCED-UNSET: 0
```

Every window, zero. The unbalanced unset — the only mechanism that could take a live attribution
away from its owner — **never fires**. The `ProjectileBase.HitEnemy` asymmetry is real, produces
hundreds of `unset-while-clear` per 5s on clients, and is **genuinely harmless**: it only ever
clears a slot that was already empty.

So every proposed mechanism for P1-5 is now eliminated:

| Mechanism | Verdict |
|---|---|
| Shared static `DamageContainer` collapse | Dead code — the value had no consumer |
| Cross-thread race on `currentPlayerId` | 0 in every window, every machine |
| `Set()` overwriting a different live id | 0 in every window, every machine |
| Unbalanced `Unset()` stealing a live id | 0 in every window |

**The originally reported symptom — gold and kill credit landing on the wrong player — remains
unattributed and unreproduced.** It was never tied to any of these by observation; the link was
inferred from decompilation. Treat it as an open report with no known cause rather than a
diagnosed bug, and do not spend more effort here without a fresh in-game reproduction.

The instrumentation is cheap and silent when healthy, so it stays in as a tripwire.

### Also dead: the `PoolManager` half of this

`PoolManagerPatches.GetAttack_Postfix` reads `ownerId` off a `WeaponBase` and copies it to the
pooled `WeaponAttack`. Nothing sets `ownerId` on a `WeaponBase` — the only writer,
`NetPlayer.RefreshWeaponOwnerId()`, is **commented out** (`NetPlayer.cs:320-327`). So that
postfix reads null and no-ops on every pooled attack fetch.

Left in place rather than deleted: it and the commented-out method are a coherent dormant pair,
and removing half of someone's disabled work is worse than documenting it. Decide together —
either re-enable both or delete both.

### Test

Nothing to test for the deletion; a write-only value has no observable behaviour, and the build
is the only check that applies.

To chase the **symptom** properly, run the instrumentation: three players, each on a different
weapon, damaging the same enemy, then specifically provoke the nesting cases — **Lightning
Strike** (one hit, several deaths) and **exploder enemies** (one death causes more). Play to a
game over so `ResetNetworking()` runs.

Then read the host *and* both client logs for `Kill-attribution anomalies`:

- **No line at all** → every counter stayed zero. Hypothesis dead; strike the subsection.
- **`unset-while-clear` only** → the `ProjectileBase` asymmetry is real but benign in practice
  (nothing was pending when it cleared). Worth tidying, not a live bug.
- **`track-with-no-owner` or `overwrite-while-set` above zero** → credit is being misassigned.
  The logged id pair names which two players collided.
- **`cross-thread` above zero** → stop and treat it as a race first; nesting is then a
  secondary concern.

Counts matter more than presence. A couple per session is a rare interleave; hundreds means the
pairing is structurally broken and the fix is to make set/unset balance rather than to chase
individual call sites.

---

<a name="p1-6"></a>
## P1-6 — `ReTargetEnemies` throws on an empty alive-set, aborting the death handler

**Status:** CONFIRMED — observed in a live session log, buildid 21750826
**File:** `src/plugin/Services/EnemyManagerService.cs:65-66`
**Caller:** `SynchronizationService.OnReceivedPlayerDied` (`:3658`)

### Symptom

`BepInEx/LogOutput.log` carries, at the end of a two-player run:

```
System.ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less
than the size of the collection. (Parameter 'index')
   at System.Linq.Enumerable.ElementAt[TSource](IEnumerable`1 source, Int32 index)
   at MegabonkTogether.Services.EnemyManagerService.ReTargetEnemies(...)  EnemyManagerService.cs:66
   at MegabonkTogether.Services.SynchronizationService.OnReceivedPlayerDied(...)  :3658
   at MegabonkTogether.Scripts.MainThreadDispatcher.Update()
```

It appears immediately before `Blocking leaderboard upload`, which places it at game over.

### Root cause

```csharp
// EnemyManagerService.cs:64-66 — inside foreach (var oldEnemy in oldTargetEnemies)
var randomIndex = Random.Range(0, currentPlayersAliveExcludingOldOneId.Count());
var randomNewTargetId = currentPlayersAliveExcludingOldOneId.ElementAt(randomIndex);
```

When the sequence is empty, `Random.Range(0, 0)` returns `0` and `ElementAt(0)` throws. There is
no guard.

The callers build it as
`GetAllPlayersAlive().Where(p => p.ConnectionId != <diedId>)`. `players` **includes** the local
player — `IPlayerManagerService` exposes a separate `GetAllPlayersExceptLocal()`, which would be
redundant otherwise — so the set is non-empty whenever anyone is still alive. The empty case is
therefore *everyone dead*, i.e. the final death of a run.

**There are three call sites, all unguarded**, and two have now been observed throwing in
separate sessions:

| Call site | Trigger | Observed |
|---|---|---|
| `OnPlayerDied()` — `:3645` | the **local** player dies | yes — via `PlayerHealthPatches.PlayerDied_Postfix` |
| `OnReceivedPlayerDied()` — `:3690` | a **remote** player dies | yes |
| `OnReceivedPlayerDisconnected()` — `:3879` | a player disconnects | not yet, same pattern |

**The two paths fail differently, and the local one is worse.** The remote path runs inside an
enqueued action, so `MainThreadDispatcher` catches it and logs a warning. The local path runs
inside a **Harmony postfix**, so the exception escapes into the IL2CPP trampoline —
`[Error:Il2CppInterop] During invoking native->managed trampoline`. In both cases the handler
aborts partway; on the local path that means `SendToAllClients(RetargetedEnemies)` and
**`SpawnReviver(...)`** never run (`:3652-3654`).

Because all three callers share the same defect, **guard inside `ReTargetEnemies` itself** rather
than at each call site — one edit, and a fourth caller cannot reintroduce it.

> **This was scoped too narrowly.** Guarding inside `ReTargetEnemies` protects that method's
> callers, but the same `Random.Range(0, count)` + `ElementAt(index)` shape was independently
> duplicated in **four other methods** that do not go through `ReTargetEnemies` at all —
> `SynchronizationService:3009` and all three `FinalFightController` prefixes. Found while doing
> [P1-4](#p1-4) and fixed there; see "Four latent P1-6 crashes" in that entry.

### Why it matters more than "an exception at game over"

The throw aborts `OnReceivedPlayerDied` partway, so **neither** of the two statements after it
runs:

```csharp
udpClientService.SendToAllClients(message, ReliableOrdered);      // RetargetedEnemies — moot at game over
SpawnReviver(netPlayer.Model.transform.position, ..., diedPlayer.ConnectionId);   // NOT moot
```

At game over, losing the Reviver is harmless. But the guard gap is unconditional: any transient
state where the alive-set is momentarily empty with players remaining would silently cost that
player their coffin, and the symptom would be "the Reviver sometimes doesn't spawn" with no
error surfaced.

### Fix

Hoist the materialisation out of the loop and guard the empty case. This is the **same edit**
[`04-performance-and-gc.md`](04-performance-and-gc.md#4-enemymanagerservice--remaining-linq)
already requests for this method — `.Count()` then `.ElementAt()` inside a `foreach` walks the
sequence twice per enemy, which at 600 enemies is the documented O(enemies × players).

```csharp
public IEnumerable<(uint, uint)> ReTargetEnemies(uint oldTargetId,
                                                 IEnumerable<uint> currentPlayersAliveExcludingOldOneId)
{
    var retargetedEnemies = new List<(uint, uint)>();

    // FIX P1-6: materialise once, and bail if there is nobody left to retarget onto.
    var candidates = currentPlayersAliveExcludingOldOneId as IList<uint>
                     ?? currentPlayersAliveExcludingOldOneId.ToList();

    if (candidates.Count == 0)
    {
        // Everyone is dead — the run is over. Nothing to retarget onto.
        return retargetedEnemies;
    }

    var oldTargetEnemies = spawnedEnemies.Values.Where(/* unchanged */);

    foreach (var oldEnemy in oldTargetEnemies)
    {
        var randomNewTargetId = candidates[Random.Range(0, candidates.Count)];
        // ... unchanged
    }

    return retargetedEnemies;
}
```

Returning empty is correct rather than merely safe: with no living players there is no valid
target, and the caller's `RetargetedEnemies` message then carries an empty list, which receivers
already handle.

> The caller already does `.ToList()` on the sequence it passes, so the `as IList<uint>` path
> hits in practice today. Keep the fallback anyway — the parameter is typed `IEnumerable<uint>`
> and a future caller may not materialise.

### FIXED — buildid 21750826, not yet verified in-game

Guard added inside `ReTargetEnemies` rather than at the three call sites, so a fourth caller
cannot reintroduce it. The alive-set is materialised once via
`as IList<uint> ?? .ToList()`, an empty set returns an empty result, and the loop indexes
`candidates[Random.Range(0, candidates.Count)]` — removing the per-enemy double walk that
[`04-performance-and-gc.md`](04-performance-and-gc.md#4-enemymanagerservice--remaining-linq)
flagged as O(enemies × players).

The `as IList<uint>` path hits in practice: all three callers already pass a `.ToList()`. The
fallback stays because the parameter is typed `IEnumerable<uint>`.

### Test

Play a two-player session to game over. **Expected:** no `ArgumentOutOfRangeException` in either
client's log, from **either** failure mode — `[Warning]` via `MainThreadDispatcher` on the remote
path, or `[Error:Il2CppInterop] During invoking native->managed trampoline` on the local path.

Then confirm in a 3+ player session that a mid-run death still spawns the Reviver and still
retargets enemies — the Reviver is what the abort was costing.

---

<a name="p1-7"></a>
## P1-7 — One destroyed `NetPlayer` aborts `Reset()`'s destroy loop, orphaning the rest

**Status:** CONFIRMED — observed in a live session log, buildid 21750826. FIXED and **VERIFIED
in-game**: a 3-player session reached `All players disconnected, returning to main menu` with no
`Error while destroying spawned player game objects during reset` — the line that was present
before the fix.
**File:** `src/plugin/Services/PlayerManagerService.cs:507`

### Symptom

Between two netplay sessions, the host log carries:

```
[Error] Error while destroying spawned player game objects during reset:
  Il2CppInterop.Runtime.Il2CppException: System.NullReferenceException
  at UnityEngine.Component.get_gameObject ()
  at MegabonkTogether.Services.PlayerManagerService.<Reset>b__50_0(KeyValuePair`2 p)
  at System.Collections.Generic.List`1.ForEach(Action`1 action)
  at MegabonkTogether.Services.PlayerManagerService.Reset()   PlayerManagerService.cs:509
```

### Root cause

```csharp
// PlayerManagerService.cs:506-515
try
{
    spawnedPlayers.ToList().ForEach(p => GameObject.Destroy(p.Value.gameObject));
}
catch (Exception ex)
{
    logger.LogError($"Error while destroying spawned player game objects during reset: {ex}");
}
spawnedPlayers.Clear();
```

A `NetPlayer` in `spawnedPlayers` has already been destroyed game-side, so `.gameObject` throws
through the interop boundary.

**The defect is the blast radius, not the throw.** `List<T>.ForEach` abandons the whole iteration
on the first exception, and the `try` wraps the *entire loop* rather than each item. So one
already-destroyed entry means **every remaining `NetPlayer` is never destroyed** — and
`spawnedPlayers.Clear()` on the next line then drops the only references to them.

Those GameObjects are now orphaned: untracked, undestroyed, and surviving into the next session.

With two players there is one spawned netplayer, so nothing is left behind when it throws — which
is why the observed session looks harmless. At 3–6 players a single stale entry orphans all the
others.

### Why it is also evidence

This is the **first stack trace** of the dangling-`NetPlayer` reference that
[#8](../reverse-engineering/01-investigation-targets.md#8-netplayer-lifetime-and-the-dangling-transform--nice)
has been chasing, and it names where the stale reference is held: `spawnedPlayers`.

It also explains why [P2-1](#p2-1)'s counters stayed at zero across two sessions — those
fallbacks are gated on `HasNetplaySessionStarted()`, and by the time `Reset()` runs the session
has already ended, so they cannot fire on this path.

### Fix

Guard per item, so one bad entry cannot abandon the rest:

```csharp
foreach (var kv in spawnedPlayers.ToList())
{
    try
    {
        var netPlayer = kv.Value;
        if (netPlayer == null)          // Unity's overloaded == catches a destroyed object
        {
            continue;
        }

        var go = netPlayer.gameObject;
        if (go != null)
        {
            GameObject.Destroy(go);
        }
    }
    catch (Exception ex)
    {
        // FIX P1-7: per-item, so one destroyed NetPlayer cannot orphan the others.
        logger.LogWarning($"Could not destroy a spawned player during reset: {ex.Message}");
    }
}
```

Note the null check alone is **not** sufficient: `netPlayer == null` uses Unity's overloaded
operator and catches the common case, but a native object freed underneath the managed wrapper
can still throw on `.gameObject` — see the **il2cpp** skill. Keep the try.

### The same defect, one line down

`Reset()` continues:

```csharp
playerInventories.ToList().ForEach(kv => kv.Value.Cleanup());   // no try at all
playerInventories.Clear();
localConnectionId = 0;
isLocalPlayerSet = false;
hasSelectedCharacter = false;
seed = 0;
```

Identical `ForEach`-abandons-on-throw shape, and this one is not wrapped at all — so a throw
from `Cleanup()` propagates out of `Reset()` and skips `playerInventories.Clear()` **and all
four field resets below it**, leaving `localConnectionId`, `isLocalPlayerSet`,
`hasSelectedCharacter` and `seed` carrying the previous session's values into the next one.
That is a wider blast radius than the `NetPlayer` loop, not a narrower one.

Not observed in a log — `Cleanup()` throwing is hypothetical where the `.gameObject` throw is
evidenced — but it is the same class of bug in the same method, so it was fixed in the same
pass with a per-item try and a `?.` on the value.

### Test

Run a 3+ player session, return to the menu, and start a second one. **Expected:** no
`Error while destroying spawned player game objects` line, and no leftover netplayer models in
the new session.

---

<a name="p1-8"></a>
## P1-8 — A lost race in `OnReceivedPlayerDisconnected` skipped the whole disconnect handler

**Status:** CONFIRMED from a 2026-07-31 log — **FIXED, not yet verified in-game.**
**File:** `src/plugin/Services/SynchronizationService.cs:3835`

The handler opened with:

```csharp
var disconnectedPeer = playerManagerService.GetPlayer(disconnected.ConnectionId);
if (disconnectedPeer == null)
{
    logger.LogWarning("Disconnected player not found in PlayerManagerService when processing OnReceivedPlayerDisconnected.");
    return;
}
```

**Two independent paths remove a player, and they race on every peer:**

| Path | Where |
|---|---|
| The host's `PlayerDisconnected` message | `UdpClientService` → `EventManager.OnPlayerDisconnected` → this handler |
| The rendezvous server's `ClientDisconnected` over the websocket | `WebsocketClientService.HandleClientDisconnected` → `PlayerManagerService.RemovePlayer` |

The websocket path removes the player record without doing any of the rest. When it won, this
handler found no record and returned, skipping **everything**:

- `playerManagerService.Disconnect` — so the departed peer's **player card stayed on screen** and
  their inventory was never released;
- `projectileManagerService.RemoveProjectilesByOwnerId`;
- on a host, the entire retarget — **which reinstates [P2-1](#p2-1)'s host-side dangling
  `Rigidbody`, the exact bug that retarget exists to prevent.** The host is subscribed to the
  websocket too, so the host can and does lose this race.

The observed log line was on a client, which is why the cost was first read as client-only. It is
not: the same early return sits in front of the host's retarget.

### Fix

The record is needed for exactly one thing — the notification's player name. Everything else needs
only the connection id. So:

1. The lookup no longer gates anything. `Disconnect` and the projectile cleanup always run.
2. The host's encounter-close and retarget moved into `RetargetAfterDisconnect(uint)`, called
   behind `isHost && GameManager.Instance?.player != null` rather than behind an early return.
3. The notification — cosmetic — **runs last, in its own `try`**. It used to run *first*, with
   `AudioManager.Instance.uiAbort` and a localisation lookup on the path, so a throw there took
   the retarget with it. Same lesson as [P0-6](#p0-6), [P1-6](#p1-6) and [P1-7](#p1-7): order by
   importance, and never let the least important statement abort the most important one.
4. The warning is kept but reworded — losing the race is not itself an error, it just means this
   peer showed no disconnect notification.

**Clients still do not retarget locally, deliberately.** `ReTargetEnemies` picks each new target
with `Random.Range`; a client running its own would diverge from the host. Clients apply the
host's `RetargetedEnemies` list, which is precisely why the host's copy of this handler must not
be skippable.

### Test

3 players, mid-run disconnect, all three logs. **Expected:** the departed player's card
disappears on every remaining peer; no `Disconnected player ... was already removed` line is fatal
(it is informational, and may legitimately appear on whichever peer lost the race); no
`get_transform` fallback burst on the host.

---

<a name="p1-9"></a>
## P1-9 — Save/restore pairs on game statics can strand the mod's handlers permanently

**Status:** CONFIRMED by inspection — **FIXED, not yet verified in-game.** Found while containing
[open item 3](06-session-handoff.md) (`RestoreDeath`'s NRE).
**Files:** `src/plugin/Plugin.cs:277`, `:305`, `:425`, `:433`

The mod swaps three game statics — `PlayerHealth.A_Died`, `WeaponInventory.A_WeaponAdded`,
`PlayerStatsNew.A_StatUpdate` — and restores them afterwards. Both pairs used **the saved value's
nullness as the "did we save?" flag**:

```csharp
public void PreventDeath()
{
    if (originalDiedAction != null) { Log.LogWarning("Death already prevented"); return; }
    originalDiedAction = PlayerHealth.A_Died;      // may legitimately be null
    PlayerHealth.A_Died = new Action(OnPlayerDied);
}

public void RestoreDeath(bool invokeDeathEvent)
{
    if (originalDiedAction == null) { Log.LogWarning("Death not prevented"); return; }
    ...
}
```

A game event with no subscribers is null. If any of the three is null when the mod saves it, the
save still swaps the static, and the restore then reads that null as "never prevented" and returns
**without restoring anything**. The mod's handler — or, for the inventory pair, a hard `null` —
stays on a game static for the rest of the process, singleplayer included. `RestorePlayerInventoryActions`
is worse: its two guards return before restoring *either* static, so a null `A_WeaponAdded` also
strands `A_StatUpdate`.

Whether the game ever leaves these null in practice is **UNVERIFIED** — the stripped interop
assemblies have no method bodies. The asymmetry is a defect regardless: a restore must not be
gated on a value the save cannot guarantee.

### Fix

- `hasPreventedDeath` / `hasSavedInventoryActions` flags record the state; the saved delegates are
  data. A null original now round-trips faithfully.
- `RestoreDeath` hands the delegate back **before** anything that can throw, and
  `RestorePlayerInventoryActions` restores both statics together.
- The two call sites that bracket game code — `NetPlayer.Initialize` (the `PlayerInventory`
  constructor) and `SynchronizationService.OnReceivedWeaponAdded` (`AddWeapon` +
  `RefreshConstantAttack`) — now use `try/finally`. A throw between save and restore used to
  silence both callbacks for the rest of the process. Same shape as [P0-6](#p0-6), where one
  throw latched two statics and broke 581 consecutive enemy spawns.

`RestoreDeath`'s invocation of the game's death handler is also contained now — see
[open item 3](06-session-handoff.md); the underlying NRE is unchanged and still undiagnosed.

### Test

Play a session to game over, return to the menu, and start a **singleplayer** run. **Expected:**
death behaves normally in singleplayer (the game's own death screen, not spectator mode), weapons
still fire their pickup callbacks, and no `Death not prevented` / `Player inventory actions not
saved` warnings.

---

<a name="p1-10"></a>
## P1-10 — `CAN_SEND_MESSAGES = false` latches with no `try/finally`

**Status:** CONFIRMED by inspection — **FIXED, not yet verified in-game.** 28 uncommented sites,
all in `src/plugin/Services/SynchronizationService.cs`; one was converted with [P1-9](#p1-9) and
the remaining 27 here.

The pattern is everywhere in the receive handlers:

```csharp
Plugin.CAN_SEND_MESSAGES = false;
<game call that applies the received state>
Plugin.CAN_SEND_MESSAGES = true;
```

The flag suppresses the mod's own outbound messages while applying someone else's state, so the
peer does not echo it back. **If the game call throws, the flag stays `false` for the rest of the
run and this peer stops sending anything** — every subsequent local action is silently invisible to
the others. That is a total, unrecoverable desync from a single unlucky exception, and
[P0-6](#p0-6) proved this exact failure happens in practice (a string interpolation threw and
latched two statics for 581 spawns).

### Fix

`Plugin.SuppressOutbound()` returns a `Plugin.OutboundSuppression` scope; every site is now

```csharp
using (Plugin.SuppressOutbound())
{
    <game call that applies the received state>
}
```

- A `readonly struct`, so `using` allocates nothing on paths that run per received message.
- It restores the **previous** value rather than hard-coding `true`. Identical at every current
  site (all are entered with the flag set) and the only version that stays correct if two
  suppressed regions ever nest — the hand-written pairs would have re-enabled sending inside an
  outer suppressed region.
- Mechanical, and verified as such: a whitespace-insensitive diff of the whole file against the
  previous revision shows **only** the 27 paired `false;`/`true;` lines replaced by the scope. No
  statement moved, and only blank lines at the edges of the new blocks were dropped.

One site (`OnReceivedWeaponAdded`) keeps an explicit `try/finally` from [P1-9](#p1-9), because it
also restores the inventory actions.

Two more nulled-out game statics were fixed the same way while sweeping — `TomeInventory.A_TomeUpgrade`
in `OnReceivedTomeAdded`, and `ItemInventory.A_ItemAdded` / `A_ItemRemoved` in
`NetPlayer.AddItem` / `RemoveItem`. See [P1-11](#p1-11) for the queue half of those two.

### Test

Nothing to observe directly — a clean session proves only that the sweep did not break the happy
path, which is the main risk in a 27-site mechanical change. Play a full session and confirm
weapons, tomes, items, pickups, portals and interactables all still replicate.

---

<a name="p1-11"></a>
## P1-11 — netplayer-position requests can be stranded, redirecting the local player's transforms

**Status:** CONFIRMED by inspection — **FIXED, not yet verified in-game.** Found while sweeping
[P1-10](#p1-10).

> ⚠️ **The original prescription in this entry — "a `try/finally` sweep, one scoped `IDisposable`
> like `Plugin.SuppressOutbound`" — is wrong, and is kept below because it is the obvious first
> idea.** Most of these pairs are a Harmony **prefix/postfix pair around a game method**. The push
> and the pop are in different methods, with the game's own code in between: there is no block to
> wrap. See "What the sweep actually found".

`AddGetNetplayerPositionRequest(id)` pushes a connection id; the transform patches
(`UnityComponentPatches.get_transform_Prefix`, `TransformPatches.get_position_Prefix` /
`get_rotation_Prefix`) read the front of that queue via `PeakNetplayerPositionRequest()` and
**redirect the local player's transform reads to that netplayer**. `UnqueueNetplayerPositionRequest()`
pops it again.

The pattern appears at ~48 call sites across `Patches/Projectiles/`, `Patches/SpecialAttack/`,
`Scripts/NetPlayer/` and `Services/`, and **exactly one of them** — in `OnReceivedItemApplied` —
wraps the bracketed work in `try/finally`. Everywhere else, a throw between push and pop leaves the
id on the queue, and from that moment the local player's `"Player"`, `"Hips"` and `"Renderer"`
transforms resolve to a remote netplayer for the rest of the session. That is a visible,
permanent corruption from a single exception, and the one existing `finally` suggests somebody has
already been bitten by it.

Same family as [P1-9](#p1-9) and [P1-10](#p1-10), and the largest of the three. Do it as its own
commit; a scoped `IDisposable` (mirroring `Plugin.SuppressOutbound`) is the obvious shape, since
the pop is unconditional at every site.

### What the sweep actually found

Both of the last two sentences above are wrong.

**The pop is not unconditional, and it is usually not in the same method.** The dominant shape is a
Harmony prefix that pushes and a postfix that pops, around a game method — `Rocket.MyFixedUpdate`,
`ProjectileScythe.TryInit`, `WeaponAttack.SpawnProjectile`, `LaserBeamAttack.Update`, the eight
`EnemySpecialAttack*` classes, and so on. There is no block to wrap in `try/finally`; the code in
between is the game's.

**And the postfix re-derives its own condition instead of being told what the prefix did**, which
is a second, more frequent leak than an exception:

| Site | Prefix pushes when | Postfix pops when | Leaks if |
|---|---|---|---|
| `EnemySpecialAttackTargetLaser` and 7 siblings | `targetId.HasValue` | re-reads `targetId`, `HasValue` | a retarget clears `targetId` mid-call — i.e. exactly what a disconnect does |
| `WeaponAttack.SpawnProjectile` | `GetNetPlayerByWeapon` finds one | re-runs `GetNetPlayerByWeapon` | a steal/return moves the weapon (`StealWeaponWui` / `ReturnWeaponWui` exist for precisely that) |
| `LaserBeamAttack`, `ProjectileDragonBreath` | owner ≠ local | re-reads owner, compares again | `GetPlayer` returns null after that peer disconnects → NRE in the postfix, pop skipped |
| all of them | session started | re-checks `HasNetplaySessionStarted()` | teardown flips it between prefix and postfix |

A stranded id sits at the front of the queue, and `PeakNetplayerPositionRequest` is what the three
transform patches consult to decide whose transform to return — so from that moment **the local
player's `"Player"`, `"Hips"` and `"Renderer"` transforms resolve to a remote netplayer**, until
`PlayerManagerService.Reset()` clears the queue at the end of the session.

### Fix — purge by frame, in one place

Every push/pop pair in the codebase brackets a **single game call**, so a request that outlives the
frame it was pushed on is a leak by definition. The queue now carries the frame:

```csharp
private ConcurrentQueue<(uint ConnectionId, int Frame)> getNetplayerPositionRequestQueue = new();
```

and `PeakNetplayerPositionRequest()` drops entries from earlier frames before peeking. That fixes
every site at once — including the ones whose postfix condition is unreliable, and ones not written
yet — instead of 48 edits that would each have to reason about a different game method.

A throttled counter reports what it drops (`Dropped N stale netplayer position request(s)…`, at
most one line per 5s, silent when healthy), so a site that leaks on *every* call is distinguishable
from one unlucky exception. Cleared on reset like the other diagnostics.

**Per-site `try/finally` was deliberately not added on top.** For the same-method pairs it would
only narrow a leak the purge already closes on the next frame.

> ⚠️ **Correction.** This entry originally said `try/finally` was *impossible* for the
> prefix/postfix majority. That is wrong: Harmony (HarmonyX, which BepInEx 6 ships) has
> **`[HarmonyFinalizer]`**, which runs after the original even when it throws. Combined with the
> balanced prefix/postfix scope stack described in
> [`08-delirium-comparison.md`](08-delirium-comparison.md#worth-taking) — the prefix pushes a record
> even when it decides not to act, and the postfix pops unconditionally instead of re-deriving its
> condition — that is the fix for the *cause* here, not just the blast radius. The frame purge
> stands until then. The two sites that *did* get a `finally` in [P1-9](#p1-9) —
`NetPlayer.AddItem` / `RemoveItem` — needed it for the **game static** they also swap, which no
amount of queue purging can restore.

**Known and left alone:** the queue is FIFO but the usage is stack-shaped — with two requests
pushed in one frame, the pop removes the *older* one. Nothing nests today as far as this sweep
found, and changing it is a behaviour change that cannot be tested here.

### Test

Play a session with heavy remote-projectile traffic (scythes, shotguns, hero swords) and a
mid-run disconnect. **Expected:** no `Dropped N stale netplayer position request(s)` line at all;
if one appears, it names a real leaking site and the count says whether it is systematic. The
symptom it prevents — the local player's model jumping to a remote player's position — should be
absent either way.

---

<a name="p2-1"></a>
## P2-1 — Dangling transform hack is silent

**Status:** CONFIRMED — **TWO independent dangling paths, not one. Both now fixed; the second is
not yet verified in-game.** The host-side `enemy.target` path was found and fixed first. The
second, client-side path through `Plugin.GetDistanceToPlayer` was named by caller sampling on
2026-07-31 and fixed the same day — see the last two subsections of this entry.
**File:** `src/plugin/Patches/Unity/UnityComponent.cs:27-31`

```csharp
if (__instance == null) //TODO: i'm pretty sure its a netplayer dangling reference but how do i even debug this...
{
    __result = GameManager.Instance.player.transform; //Hack ¯\_(ツ)_/¯
    return false;
}
```

A known-unexplained fallback that produces no evidence. Add a **rate-limited** warning — this
path can fire per-frame per-affected-object, and an unthrottled `LogWarning` is a per-frame
string allocation plus BepInEx disk I/O:

```csharp
private static float lastDanglingWarnTime = -999f;

if (__instance == null)
{
    if (Time.unscaledTime - lastDanglingWarnTime > 5f)
    {
        lastDanglingWarnTime = Time.unscaledTime;
        Plugin.Log.LogWarning("Caught dangling transform reference (likely a destroyed NetPlayer). Falling back to local player transform.");
    }
    __result = GameManager.Instance.player.transform;
    return false;
}
```

Once you have frequency data, chase the root cause — a `NetPlayer` reference retained past
`Destroy`. Prime suspects: `PlayerManagerService.cs:466` (`//TODO: cleanup inventories at some
point`) and `TargetSwitcher.currentTarget`, which caches a `(Transform, Rigidbody)` tuple
with no invalidation on player despawn.

### Implemented — and there were three fallbacks, not one

`TransformFallbackDiagnostics` in `Patches/Unity/UnityComponent.cs` now counts all of them:

| Site | Accessor | Used by |
|---|---|---|
| `UnityComponentPatches.get_transform_Prefix` | `Component.get_transform` | DragonBreath, special attacks |
| `TransformPatches.get_position_Prefix` | `Transform.get_position` | LaserBeamGun |
| `TransformPatches.get_rotation_Prefix` | `Transform.get_rotation` | ProjectileMelee (Sword) |

Only the first carried the TODO; the other two are the same hack, equally silent. Instrumenting
one would have produced no data if the dangling reference surfaces through the others — and
knowing *which* accessor fires is the strongest narrowing signal available, since each has a
different caller.

Counts accumulate between reports (max one per 5 s) rather than logging per hit: "1,247 times in
5 s" and "3 times in 5 s" point at completely different root causes, and these sit on three of
the hottest properties in Unity. Recording happens only on the exceptional branches.

An unthrottled interpolated `LogWarning` on the netplayer-not-found path in the same method was
folded into the same throttle — it was a per-frame string allocation on a patched
`get_transform`. `NetworkHandler.ResetNetworking()` calls `Reset()` so counts do not bleed
between sessions.

**Result so far — zero fallbacks fired, across two separate two-player sessions.** Neither
produced a `Transform fallbacks fired` line. That is a real data point, not a missing feature:
the deployed DLL was confirmed to contain the type.

**But [P1-7](#p1-7) explains why these counters may never fire on the path that matters.** The
dangling `NetPlayer` reference was caught with a stack trace in `PlayerManagerService.Reset()` —
during *teardown*. These fallbacks are gated on `HasNetplaySessionStarted()`, which is already
false by then, so they cannot see it. Two clean sessions therefore do **not** mean the hack is
dead code; they may mean the leak lives outside the window being watched.

Before deleting anything, test 3+ players with mid-run disconnects — and treat P1-7 as the more
promising lead, since it names where the stale reference is held (`spawnedPlayers`).

### CONFIRMED — 3 players, mid-run disconnect. It is not dead code.

That test was run. **The hack fires ~144 times per second the moment a peer disconnects**, on
every remaining machine.

| | before the disconnect | after |
|---|---|---|
| `get_transform` | 1 per 5s | **720**, then **702** / **715** per 5s |
| `get_position` | 0 | **0** |
| `get_rotation` | 0 | **0** |
| `netplayer-not-found` | 0 | **0** |

Two-player sessions showed zero because the session *ends* the instant the only peer leaves. With
three players the session survives the disconnect and the leak becomes observable. The counters
were right to be kept.

**Two constraints this pins down, both useful:**

- **It is exclusively `get_transform`.** Zero `get_position` and zero `get_rotation` across every
  window. So it is `.transform` on a destroyed `Component`, not `TargetSwitcher.CanSwitch`, which
  reads `currentTarget.transform.position` and would show up as `get_position`.
- **~144/s is close to the enemy retarget rate** (600 enemies ÷ ~4s average switch interval
  ≈ 150/s). Suggestive, not conclusive.

The same disconnect also produced **668 unthrottled `Player not found for ConnectionId` warnings**
from `PlayerManagerService.GetPlayer`, all for the departed id — so a per-frame caller is also
still asking for the player by id, not only holding their transform. That warning is now throttled
per id and carries a suppressed count.

**Deliberately not guessing the owner.** Three entries in this document were written from
inference and did not survive contact with the code ([P1-1](#p1-1) stale, [P1-3](#p1-3) disproved,
[P1-5](#p1-5) dead). Instead, `TransformFallbackDiagnostics` now samples **one managed stack trace
per report window** and prints the MegabonkTogether frames from it.

That sampling is decisive rather than merely suggestive: under IL2CPP, native game frames do not
appear as managed frames, so

- **frames present** → our own code is dereferencing the destroyed object, and the line numbers
  name it (symbols ship beside the DLL);
- **`no MegabonkTogether frames`** → the access came from game code that we handed a stale
  reference to — a different fix entirely.

One capture per 5s, wrapped in try/catch.

### SOLVED — the host never applied its own retarget

Two further 3-player runs, 57 and 63 report windows, **every one** reading:

```
Sampled caller: no MegabonkTogether frames (called from game code)
```

So our code never touched the destroyed object. We handed it to the game. The chain:

1. A peer times out → `playerManagerService.Disconnect` destroys their `NetPlayer` GameObject.
2. `ReTargetEnemies` rewrites only the **networked** `targetId` in `DynamicData`. It never assigns
   `enemy.target`.
3. `ApplyRetargetedEnemies` — the sole assigner of `enemy.target` — was called from exactly one
   place: `OnReceivedRetargetedEnemies`, the **receive** handler. The host broadcasts that message
   and does not receive its own broadcast.
4. So host enemies kept the departed player's destroyed `Rigidbody` as `target`, and Megabonk's
   own movement code read `target.transform` every update → the fallback → `GameManager.Instance
   .player.transform`.

Everything the counters showed follows: exclusively `get_transform` (never `get_position`, which
is what `TargetSwitcher.CanSwitch` would have produced), a game-code caller, and ~144/s rather
than 600/frame because `Enemy.MyUpdate` is distance-throttled.

**It was also a live gameplay bug, not just log noise.** Those host enemies silently chased the
*host* via the fallback, while every client believed they targeted someone else.

**Fixed at all three `ReTargetEnemies` call sites** — `OnPlayerDied`, `OnReceivedPlayerDied` and
`OnReceivedPlayerDisconnected` — each of which had the identical gap. The same "guard the method,
not the call site" lesson as [P1-6](#p1-6) applies: one defect, three copies.

This also closes investigation target
[#8](../reverse-engineering/01-investigation-targets.md#8-netplayer-lifetime-and-the-dangling-transform--nice).
The dangling reference was never a `NetPlayer` field the mod held — it was a `Rigidbody` the mod
gave to the game and then failed to replace.

**Do not delete the fallback hack yet.** It should now go quiet after a disconnect; confirm that
before removing it, since it may still cover other paths.

### STILL OPEN — a second dangling path, client-side, via `Plugin.GetDistanceToPlayer`

**"Root cause found" above was premature.** It was *a* root cause, not *the* root cause. A
3-player session on 2026-07-31 — with the P0-6 fixes in and all three instances on the current
build — produced the first sampled caller that names our own code:

```
get_position: 696   Sampled caller: TransformPatches.get_position_Prefix:244
                    <- Plugin.GetDistanceToPlayer:510
                    <- ProjectileBasePatches.Update_Prefix:157

get_position: 703   Sampled caller: TransformPatches.get_position_Prefix:244
                    <- Plugin.GetDistanceToPlayer:510
                    <- DistanceThrottler.ShouldUpdate:14
                    <- EnemyPatch.MyFixedUpdate_Prefix:402
```

Two things this overturns:

1. **It is `get_position`, not `get_transform`.** Every earlier run showed `get_position: 0` in
   every window, and that zero is what the "it cannot be the distance throttler" reasoning above
   rested on. That reasoning was right about the host and wrong about the client — this path only
   surfaces on a client, and only in the first few windows after a disconnect.
2. **`Plugin.GetDistanceToPlayer` holds a destroyed `NetPlayer`** and reads `.position` off it. It
   sits on two per-frame paths: `DistanceThrottler.ShouldUpdate` (per enemy, per FixedUpdate) and
   `ProjectileBasePatches.Update_Prefix` (per projectile, per frame). ~700 hits per 5s from each.

From the fifth window on, that client reverts to `get_transform` / "no MegabonkTogether frames" —
the host-side `enemy.target` path arriving through its own enemies. So the two leaks are genuinely
independent and this run separated them for the first time.

**Related, found in the same log:** the client logged

```
Disconnected player not found in PlayerManagerService when processing OnReceivedPlayerDisconnected
```

It had already removed that player via the `ClientDisconnected` websocket path, so the host's
`PlayerDisconnected` message hit the early return at the top of the handler.

**Correction to what that early return was originally thought to cost.** It was written up as
"the client never retargets its enemies off the departed peer". That is wrong in the detail:
clients never retarget locally *by design* — `ReTargetEnemies` picks each new target with
`Random.Range`, so a client running its own would pick different targets than the host and desync.
The client's retarget only ever arrives as the host's `RetargetedEnemies` message. What the early
return actually cost is below, and the host is the peer that had the most to lose.

**Lesson, and it is the same one three times now.** "Sampled caller says game code" was taken as
"our code is not involved". It only ever meant "our code is not involved *in the frames that were
sampled*" — and the sampler captures one stack per 5s window, so a path that only fires in the
first few windows is easy to miss entirely. Read the counters per-window, not in aggregate.

### FIXED (not yet verified in-game) — the spectator camera never invalidated its target

The owner named by the sampled stack is `CameraSwitcher.targetTransform`.

`Plugin.GetDistanceToPlayer:510` only reads the camera target when the **local player is dead**,
which is exactly when `CameraSwitcher` is spectating a remote peer, holding that peer's
`NetPlayer.Model.transform`. When the spectated peer leaves,
`PlayerManagerService.RemovePlayer` destroys their `NetPlayer` GameObject and **nothing tells
`CameraSwitcher`**. The field is left dangling, and every subsequent frame reads `.position` off
it from two per-frame paths.

That also explains the run's shape without any further assumption:

- **Client-side only** — the host was alive; only a dead player spectates.
- **`get_position`, not `get_transform`** — the read is `Transform.position` on the camera
  target, not `Component.transform`.
- **Only the first few windows after the disconnect** — the read is gated on `player.IsDead()`,
  so it stops the moment that client stops spectating. A revive is the obvious candidate
  (`RestoreDeath` → `ResetToLocalPlayer` clears `targetTransform`), but the log was not checked
  for one: **LIKELY**, not confirmed. From the fifth window on, that client saw only the
  host-side `enemy.target` leak arriving through its own enemies.

Three changes, all in the same commit:

1. **`Plugin.GetDistanceToPlayer` guards the read** (`Plugin.cs`). `Instance?.CameraSwitcher` and
   the returned transform are both null-checked — Unity's overloaded `==` catches a destroyed
   object — falling back to the local player's position. That fallback is *exactly* what
   `TransformPatches.get_position_Prefix` was already substituting, so **no distance result
   changes**; the deref of the destroyed object does not happen.
2. **`CameraSwitcher.Update` recovers from a destroyed target** (`Scripts/CameraSwitcher.cs`) —
   switches to another spawned player, or returns to the local player if none is left. This is
   the user-visible half: previously `LateUpdate` early-returned on the dangling transform and the
   spectator camera simply **froze** on the departed peer. Recovery lives in `Update` rather than
   the disconnect handlers because two independent paths remove a player — the host's
   `PlayerDisconnected` and the websocket `ClientDisconnected` — and they race (see the open item
   about `OnReceivedPlayerDisconnected`'s early return). One Unity null check per frame, only
   while spectating.
3. **`CameraSwitcher.SwitchToTarget` resolves the target before touching the camera.** It called
   `SaveOriginalCamera()` and disabled `playerCamera` *first*, then dereferenced a
   `FirstOrDefault` result — so a peer who disconnected between the id being chosen and the switch
   both threw and left the camera disabled with nothing driving it. Same ordering lesson as P0-6
   and P1-7: do the important thing first, and bail before you have mutated anything.

**What verification needs:** a 3-player session where a *dead, spectating* client is watching the
peer who disconnects. Two players is not enough (the session ends), and the local player must
actually be dead at the moment of the disconnect or this path never opens. Expect: no
`get_position` hits in the fallback counters, the spectator camera moving to another player, and
one `Spectated player is gone` warning rather than a per-frame stream of them.

---

<a name="p2-2"></a>
## P2-2 — Dead `GetAllPlayers()` calls in charging paths

**Status:** CONFIRMED

Every charging method contains:

```csharp
var players = playerManagerService.GetAllPlayers();
```

`players` is never read. It appears in all 12 charging methods (start/stop × shrine/pylon/lamp
× local/received). Delete them — each is a wasted call and, depending on the implementation,
an allocation. Do this as part of P0-1/P0-2 since you are already editing those methods.

---

<a name="p2-3"></a>
## P2-3 — Charging logic triplicated across shrine / pylon / lamp

**Status:** CONFIRMED — FIXED and **VERIFIED in-game**. A 3-player session exercised both guard
branches of the extracted helpers, logging `Another player is already charging this shrine.
Preventing re trigger.` and `Another player is still charging this shrine. Preventing stop
trigger.` — so the `{label}` interpolation and both paths work, with no `KeyNotFoundException`
(P0-2 holds through the dedup).
Upstream's own note, now removed along with the duplication:
`//TODO: this is ass, pylon and lamp should be refactored to use the same logic, but i'm in holidays and lazy right now zzzz`

Six ~30-line methods differ only in which dictionary they touch and which message type they
build. `Sea-Bass-cmd`'s extraction into `HandleChargingStart` / `HandleChargingStop` is the
right shape — with two corrections:

1. **Keep them `private`.** Sea-Bass added them to the public `ISynchronizationService`
   interface taking a `ConcurrentDictionary<uint, ICollection<uint>>` parameter, which leaks
   internal state through the API surface.
2. **Use `TryGetValue`, not `FirstOrDefault`.** Sea-Bass's version keeps the O(n) LINQ scan
   with a closure allocation and a `ConcurrentDictionary` snapshot enumerator.

Do this **after** P0-1 and P0-2 land, so the fixes are verified in the duplicated form first
and the dedup is a pure refactor.

### What landed

`HandleChargingStart` / `HandleChargingStop`, both `private`, taking
`(uint netplayId, ConcurrentDictionary<uint, ICollection<uint>> chargingPlayers,
IGameNetworkMessage message, string label)`. Net **−50 lines**; the six public methods are now
~10 lines each and differ only in the message they build and which dictionary they pass.

Both corrections above were applied: the helpers stay off `ISynchronizationService`, and the
`TryGetValue` form from P0-1/P0-2 carried across rather than reverting to `FirstOrDefault`.

The caller still builds the message, because each type names its id property differently
(`ShrineNetplayId` / `PylonNetplayId` / `LampNetplayId`). Passing a factory delegate instead
would allocate a closure per call for no benefit.

**Behaviour-preserving, deliberately:**

- `IsServerMode()` now runs *after* message construction rather than before. Safe — it is
  `udpClientService.IsHost()`, a pure read with no side effects.
- `OnStoppingChargingPylon` has a `LogInfo` line the shrine and lamp paths do not. Kept at the
  call site rather than folded into the helper or quietly dropped.
- `GetLocalPlayer()` is still dereferenced unguarded, exactly as before. It is nullable
  (`Player?`) and this is the same hazard as [P0-4](#p0-4) — but fixing it here would make this
  more than a pure refactor, and the charging path only runs when a local player physically
  touches the object, so the value is set. **Left as-is deliberately; flagged, not fixed.**

### Not covered: the six `OnReceived*` methods

`OnReceivedStartingToChargingShrine` and its five siblings were **not** deduplicated. They look
similar but differ in the component type they fetch (`ChargeShrine` / `BossPylon` / `BossLamp`)
and the game method they invoke, so unifying them needs generics plus a delegate per call —
more machinery than the duplication costs.

Each of them does carry a *separate* duplication worth noting: the `isHost` and client branches
are identical apart from the host additionally broadcasting. That is a smaller, self-contained
cleanup if anyone wants it.

### Test

Pure refactor, so the test is that charging still behaves as it did after P0-1/P0-2: with two
players, one starts charging a shrine and the other cannot re-trigger it; the first stops and
the shrine unlocks. Repeat for pylon and lamp — the pylon path is the one with the extra log
line, so confirm it still appears.

---

<a name="p2-4"></a>
## P2-4 — `CheckForUpdates = false` is ignored, and the un-initialised service then NREs

**Status:** CONFIRMED — observed in a live session log, buildid 21750826. FIXED, not yet
verified in-game.
**Files:** `src/plugin/Plugin.cs:169-192`, `src/plugin/Patches/WindowManager.cs:64-66`,
`src/plugin/Services/AutoUpdaterService.cs:138`

### Symptom

With `CheckForUpdates = false` in the config, the log reports both that updates are disabled
**and** that an update check ran and failed:

```
[Info : MegabonkTogether] Auto-update is disabled in configuration.
...
[Info : MegabonkTogether] Checking for updates...
[Error: MegabonkTogether] Error checking for updates: Object reference not set to an instance of an object.
```

### Root cause

Two independent call sites, only one of which respects the config.

**`Plugin.cs` gates correctly** — and `Initialize()` lives *inside* the gate:

```csharp
if (ModConfig.CheckForUpdates.Value)
{
    autoUpdaterService.Initialize();          // the ONLY caller of Initialize()
    Task.Run(async () => { await autoUpdaterService.CheckAndUpdate(); /* ... */ });
}
else
{
    Log.LogInfo("Auto-update is disabled in configuration.");
}
```

**`WindowManager.cs` does not gate at all** — it fires on the main-menu window:

```csharp
Task.Run(async () =>
{
    await autoUpdaterService.CheckAndUpdate();   // no CheckForUpdates check
    // ...
});
```

And `CheckAndUpdate()` itself guards only on `isUpdateAvailable` and a 5-minute cooldown — never
on the config value.

So when the setting is `false`: `Initialize()` never runs, leaving `currentVersion` and
`pluginPath` **null** (it is their only assignment), and `WindowManager` then calls
`CheckAndUpdate()` anyway. The check proceeds past its guards, logs `Checking for updates...`,
reaches GitHub, and dereferences null state on the way back.

> The log records only `ex.Message`, not a stack trace, so the exact null is **not proven**.
> `currentVersion` is the strongest candidate — `IsNewerVersion(latestRelease.TagName,
> currentVersion)` is the first thing to use it after the network call. The fix does not depend
> on identifying it.

### Why it is worth fixing despite being cosmetic

The exception is caught and the game continues, so nothing breaks. But:

- **The setting does not do what it says.** A user who turns auto-update off still has the mod
  contact the GitHub API every time the main menu opens. That is a behaviour surprise, and the
  guide added in [`05-local-testing.md`](05-local-testing.md) tells people to set this flag
  precisely so their test build is left alone.
- **`pluginPath` is null on the same path**, and `LaunchUpdaterOnExit(pluginDirectory)` uses it
  (`Patches/SaveManager.cs:108`). Not reached while the check fails early, but it is the same
  un-initialised state.
- It puts a red `[Error]` in every log from a user who disabled updates, which is noise in
  exactly the file used to diagnose everything else.

### Fix

Two lines, either of which alone stops the exception; do both.

**1. Make the second call site respect the config** (`WindowManager.cs`):

```csharp
if (!ModConfig.CheckForUpdates.Value) return;
await autoUpdaterService.CheckAndUpdate();
```

**2. Make the service self-sufficient** rather than relying on every caller to gate — guard at
the top of `CheckAndUpdate()`, so a future third caller cannot reintroduce this:

```csharp
public async Task<bool> CheckAndUpdate()
{
    if (!ModConfig.CheckForUpdates.Value)
    {
        return false;
    }
    // ... unchanged
}
```

Moving `Initialize()` out of the `if` in `Plugin.cs` is a *third* option and would also stop the
NRE — but it would leave the config still being ignored, so prefer the two above.

Both landed. `Plugin.cs` was left untouched: its gate was already correct, and the service-level
guard now makes it redundant rather than wrong.

### Test

Set `CheckForUpdates = false`, launch, and open the main menu. **Expected:** the "disabled" line,
no `Checking for updates...`, and no error. Then set it back to `true` and confirm the check runs
and an available update is still offered.

---

<a name="p2-5"></a>
## P2-5 — `RemoveProjectilesByOwnerId` filters on the wrong key

**Status:** CONFIRMED by inspection — **FIXED, not yet verified in-game.** Found while fixing
[P1-8](#p1-8).
**File:** `src/plugin/Services/ProjectileManagerService.cs:175`

```csharp
var projectilesToRemove = spawnedProjectile
    .Where(kv => kv.Value != null && kv.Key == connectionId)
```

`spawnedProjectile` is keyed by **projectile id** — `AddSpawnedProjectile` allocates
`currentProjectileId++` — not by owner. So this removes, at most, the one projectile whose id
happens to equal the departed player's connection id, and normally nothing at all. The method has
one caller: the disconnect handler. **A departed peer's projectiles are therefore never cleaned
up**, and they sit on the per-frame `ProjectileBasePatches.Update_Prefix` path.

Nothing in `ProjectileManagerService` records an owner, so the fix is not a one-line predicate: it
needs an owner recorded at `AddSpawnedProjectile` time (a parallel `id → ownerId` map, cleared in
`ResetForNextLevel`), and every call site of `AddSpawnedProjectile` has to supply it — the local
player's id for locally spawned projectiles, the message's `OwnerId` for received ones.

### Fix

A `ConcurrentDictionary<uint, uint> projectileOwners` alongside `spawnedProjectile`, written by
`AddSpawnedProjectile(projectile, ownerId)` and cleared wherever the projectile is — removal, the
dead-projectile sweep, `ResetForNextLevel`. The predicted "call-site sweep" turned out to be one
line: `AddSpawnedProjectile` has a single caller, `SynchronizationService.OnSpawnedProjectile`,
which already computes the owner id for the message it sends. The destroy loop is also guarded per
projectile now, so one already-destroyed GameObject cannot abandon the rest ([P1-7](#p1-7)).

Both keys stay in one id space — the ids this peer allocated. That matters: **remote** projectiles
carry an id allocated by the *sender*, so mixing them into the same map would collide.

### The other half — interpolated remote projectiles — also fixed

`RemoveProjectilesByOwnerId` initially covered only projectiles this peer simulates. Projectiles
*received* from a peer never enter `spawnedProjectile`: `OnReceivedSpawnedProjectile` instantiates
a GameObject, stamps `netplayId`/`ownerId` on it with `DynamicData`, and registers it with
`ProjectileInterpolator` under **the sender's id**. Nothing removed those when their owner left, so
they stayed in `activeProjectiles` — walked by every `Update` — waiting for snapshots that would
never arrive.

`ProjectileInterpolator` now keeps its own `id → ownerId` map, populated by
`RegisterProjectile(id, projectile, ownerId)`, and exposes `UnregisterProjectilesByOwner`, which
the disconnect sweep calls. **Two maps rather than one, deliberately:** the interpolator's ids are
allocated by the sender and `spawnedProjectile`'s by this peer, so merging them would collide.

### Test

3 players, one peer disconnects while their weapons are firing. **Expected:** their in-flight
projectiles vanish on the remaining peers — both the ones those peers were simulating and the ones
being interpolated from that peer's messages — instead of hanging in the air.

---

<a name="p0-6"></a>
## P0-6 — Reviving a disconnected player's coffin corrupts the rest of the run

**Status:** CONFIRMED in-game — **FIXED and VERIFIED in-game** (3-player session, 2026-07-31:
one player died leaving a coffin, disconnected, and a remaining player interacted with that
coffin — enemies stayed in sync for everyone, and none of `Failed to process spawned enemy`,
the `GetFullName()` NRE or the `Destroy_Postfix` NRE appears in any of the three logs)
**Files:** `src/plugin/Scripts/Interactables/InteractableReviver.cs:71,152`,
`src/plugin/Services/SynchronizationService.cs` (`OnReceivedSpawnedEnemy`)

Found by playtesting, not by inspection. Not part of the original fork audit.

### Symptom

After a peer disconnects and someone interacts with a coffin belonging to a **departed** player:
enemies on the clients **stop moving entirely while still dealing contact damage**, and the host's
and clients' enemy state diverge completely. It does not recover for the rest of the run.

### Root cause — a four-step cascade from one unguarded string interpolation

```csharp
// InteractableReviver.SpawnEnemy(), before the fix
Plugin.Instance.CurrentReviver = reviverId;        // process-wide static
Plugin.Instance.CurrentReviverOwner = ownerId;     // process-wide static
var enemy = EnemyManager.Instance.SpawnBoss(...);
enemyManagerService.AddReviverEnemy_Name(enemy, GetFullName());  // ← THROWS
Plugin.Instance.CurrentReviver = null;             // never runs
Plugin.Instance.CurrentReviverOwner = null;        // never runs
```

1. **`GetFullName()` is `GetPlayer(ownerId).Name`, unguarded.** The owner disconnected; their
   coffin did not. `GetPlayer` returns null → NRE inside a coroutine `MoveNext`.
2. **The two statics stay latched for the rest of the run**, because the clearing lines sit after
   the throw with no `finally`.
3. **Every subsequent enemy spawn on the host** reads them in `OnSpawnedEnemy`:
   `RebalanceIfNeededReviverEnemy` stops early-returning and rewrites the HP of the next five
   unrelated enemies (×0.167, ×0.333, ×0.5, ×0.667, ×0.833), and every `SpawnedEnemy` message goes
   out stamped with a stale `ReviverId`.
4. **Every client then fails to process every enemy.** `OnReceivedSpawnedEnemy` ran its reviver
   block *before* registration, with two unguarded derefs — `GetSpawnedObject(...)` before
   `.GetComponent<>()`, and `reviver?.` on one line followed by bare `reviver.` on the next. The
   same `GetFullName()` throws, the handler aborts, and the three statements at the end of the
   method never run:

   ```csharp
   var interpolator = enemy.gameObject.AddComponent<EnemyInterpolator>();
   interpolator.Initialize(enemy);                              // ← never runs
   enemyManagerService.SetSpawnedEnemy(spawnedEnemy.Id, enemy); // ← never runs
   DynamicData.For(enemy).Set("targetId", ...);                 // ← never runs
   ```

The game had already instantiated the GameObject, so it is solid and damaging. The mod never
registered it, so no host snapshot can ever move it. **Frozen, unregistered, still lethal** —
exactly the reported symptom.

Client evidence: **581 consecutive failures**, enemy id 491 through 1071, each
`NullReferenceException at InteractableReviver.GetFullName() line 71`. Every enemy from the moment
of the failed revive onward.

### Fix

Four changes, because each layer failed independently:

1. **`GetFullName()` falls back to the raw id** instead of throwing. A cosmetic name is never worth
   an exception.
2. **`SpawnEnemy()` clears the statics in a `finally`.** Legal in an iterator as long as no
   `yield return` is inside the block, and none is. This is the important one — it converts "one
   failed revive" back into "one failed revive" instead of a run-ending fault.
3. **The client's reviver block is fully guarded** — null-checked object, null-checked component,
   and wrapped in its own `try/catch`.
4. **It moved to *after* registration.** Ordering is the structural fix: cosmetic decoration must
   never be able to cost an enemy its interpolator and registry entry.

### Lesson

Two general rules this cost a run to learn, both worth applying elsewhere:

- **Global state set around a call that can throw needs `try/finally`,** always. The blast radius
  is unbounded and outlives the operation.
- **Order operations by importance.** Anything cosmetic belongs after everything load-bearing, in
  its own `try`. Three separate handlers in this codebase have now aborted partway and skipped
  their most important statement — [P1-6](#p1-6), [P1-7](#p1-7), and this.

### Test

3 players. One dies, leaving a coffin, then disconnects. A remaining player interacts with that
coffin. **Expected:** the revive fails gracefully with one warning, and enemies keep spawning,
moving and syncing normally for everyone afterwards.

---

## Explicitly not recommended

Changes present in `Sea-Bass-cmd/optimized-netplay` that should **not** be applied. Rationale
in [`03-cherry-pick-guide.md`](03-cherry-pick-guide.md) and
[`../AUDIT_optimized-netplay.md`](../AUDIT_optimized-netplay.md).

| Change | Why not |
|---|---|
| 17 event RPCs → `Unreliable` | Permanent desync on packet loss; reverts upstream `24f5004`. See [`02-delivery-method-reference.md`](02-delivery-method-reference.md) |
| `NetEntity` replacing `DynamicData` | Broken under object pooling; GameObject-level key collapse; unbounded static dictionary; slower at most call sites |
| Final swarm cap 400 → 700/800 | **CONFIRMED wrong.** `EnemyManager.GetNumMaxEnemies` returns 550 normally and *lowers* to 400 during the final swarm, then 300 past a time threshold. The game deliberately reduces density at its heaviest moment; raising it pushes against a staged reduction. Note `NETPLAY_CHANGES.md` calling 400 "the original cap" is also wrong — and **our own 500/600 caps were chosen against that wrong baseline; 500 is below vanilla.** |
| `BaseSummoner` patch re-enabled **as written** | `giveCreditsTimer` is an accumulator reset to 0 on each grant, so multiplying it by >1 makes credits arrive *faster* — the opposite of the feared collapse, but compounding within each window to roughly 2–3× rather than the 1–5% the multiplier implies. If this balance lever is wanted, postfix `GetMultiplier()` (VA `0x46CD20`) instead: linear, non-compounding, and it composes with the game's own multiplier. The FPS reason for the original disable is still unaddressed. |
| `Task.Run` removal in `WebsocketClientService` | Moot — that file is deleted by the Steamworks migration |
| `PlayersCount` `ICollection` test | Cosmetic; fix `GetAllPlayersAlive()` instead (P1-4) |

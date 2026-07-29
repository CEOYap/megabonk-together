# Three-Fork Comparison

A synthesis of what each related project is, what it does well, and what — if anything —
belongs in this fork.

---

## The three projects

### 1. `Fcornaire/megabonk-together` — upstream

The parent project and the only complete netplay implementation for Megabonk.

- **Loader:** BepInEx IL2CPP
- **Transport:** LiteNetLib UDP, NAT punchthrough, self-hosted relay fallback
- **Discovery:** self-hosted websocket rendezvous server (`src/server`, ASP.NET Core)
- **Serialization:** MemoryPack
- **Scale:** 113 Harmony patch files; `SynchronizationService.cs` alone is 4,373 lines
- **Synced:** enemies, projectiles, pickups, chests, shrines/pylons/lamps, damage
  attribution, XP, gold, inventory (weapons/tomes/items/hats), encounters, tumbleweeds,
  storms, swarms, final boss orbs, map generation

**Relationship to this fork:** we are 2 commits ahead of `50b30a4`. Both are Fcornaire's:

- `bd9518c` "feat: more code optimizations" — genuinely good work. `string[]` → `HashSet<string>`
  for `AllowedDamageSource` (O(n) → O(1) on a per-damage-event lookup), and
  `Select().ToList().ToDictionary()` → pre-sized `Dictionary` + `foreach` in
  `GetAllEnemiesDeltaAndUpdate` (the per-tick, per-enemy hot path). Also adds a
  `DistanceThrottler`.
- `041881b` "chore: added some logs to identify a chest open issue" — diagnostic logging
  around the chest-open path, which is a known-fragile area.

Also relevant, already in the merge base: `24f5004` *"fix: update packet delivery methods to
improve performance and prevent desync issues"*. Upstream deliberately tuned the reliability
map. Treat it as intentional; do not overwrite it wholesale.

**Verdict:** this is the trunk. Stay aligned with it, and upstream your own fixes where they
are general.

---

### 2. `Sea-Bass-cmd/optimized-netplay` — the optimization fork

12 commits off `50b30a4`, 43 files, +565/−489. Standalone repo, `main` branch only, head
`8628e71`.

Advertised as netcode/GC/performance work. In practice it is a script-driven replacement of
MonoMod `DynamicData` with a custom `NetEntity` MonoBehaviour across ~40 files, plus a
blanket downgrade of 17 event RPCs to `Unreliable`, plus a handful of real logic fixes buried
in the same commits.

**What it gets right:**

| Contribution | Value |
|---|---|
| Shrine/pylon/lamp charging: check-before-write ordering | **High** — fixes a real claim-clobbering bug |
| Charging stop paths: `KeyNotFoundException` guards | **High** — fixes a live crash |
| Deduplicating the triplicated charging logic | Medium — resolves upstream's own TODO |
| Host relay for XP / gold / encounter-close | Medium — closes a real propagation hole, but the implementation echoes to the sender |
| Legendary (golden) shrine flag sync | Medium — real gap, but breaks the wire format with no version gate |
| Null guard on `GetNetPlayerByNetplayId` | Medium — prevents a live NRE |
| `LogWarning` on the dangling-transform hack | Low — but useful; needs rate limiting |

**What it gets wrong:**

| Problem | Impact |
|---|---|
| 17 non-idempotent event RPCs → `Unreliable` | **Critical** — permanent desync on any packet loss; reverts upstream `24f5004` |
| Host relay echoes `GoldChanged` to its own sender | **Critical** — gold duplication exploit (`ChangeGold` is a delta) |
| `NetEntity` keyed on GameObject, cleaned only in `OnDestroy` | **High** — broken under object pooling; stale ownership on recycled objects |
| `Object.Destroy(netEnt)` before `DespawnPickup` | **High** — deferred clear + `AddComponent`/`Destroy` churn per pickup cycle |
| Final swarm enemy cap 400 → 700/800 | **High** — contradicts `NETPLAY_CHANGES.md`, worst-case density up 75–100% |
| `BaseSummoner` patch re-enabled verbatim | **High** — was disabled for measured FPS reasons; compounding multiplier unaddressed |
| Claimed `Interlocked` concurrency fix never applied | **Medium** — only the `//TODO` markers were deleted; the race is intact |
| `__instance.target` assignment silently dropped in `Enemy.cs` | **Medium** — 2–6 s host-aggro bias on every spawn |
| `Specific.IsGoldenShrine` added with no version gate | **Medium** — silent MemoryPack wire break |

**Verdict:** **selective hand-porting only.** Do not merge, do not cherry-pick at commit
granularity — every commit with something worth having also carries something that must not
ship. See [`03-cherry-pick-guide.md`](03-cherry-pick-guide.md).

Two mechanical blockers if you try anyway:

- `45ce3f5` adds an 11.5 MB `dump.cs` that `8628e71` removes. Cherry-picking `45ce3f5`
  alone permanently adds 11.5 MB to history.
- 5 files conflict with our `bd9518c`/`041881b`: `Patches/Enemies/Enemy.cs`,
  `Patches/Unity/UnityComponent.cs`, `Plugin.cs`, `Services/EnemyManagerService.cs`,
  `Services/SynchronizationService.cs`. Fcornaire's versions of the `EnemyManagerService` and
  `Enemy.cs` changes are better; do not let a cherry-pick overwrite them.

---

### 3. `Vanlichtinstein1945/Multibonk` — the Steamworks reference

A separate, much smaller multiplayer mod. Self-described as unstable.

- **Loader:** MelonLoader (not BepInEx — code is not directly portable)
- **Transport:** `ISteamNetworkingSockets` P2P
- **Discovery:** `SteamMatchmaking` lobbies + `GameLobbyJoinRequested_t`
- **Scale:** 1,252 lines of networking total (`SteamNetworking.cs` 712, `LobbyManager.cs` 402, `Main.cs` 138)
- **Synced:** player position, rotation, animation bits. That is the entire netcode.

Side by side:

| | megabonk-together | Multibonk |
|---|---|---|
| Loader | BepInEx IL2CPP | MelonLoader |
| Transport | LiteNetLib + NAT punch + self-hosted relay | `ISteamNetworkingSockets` P2P |
| Discovery | Self-hosted websocket rendezvous | Steam lobbies |
| Netcode size | 4,373 lines in one service | 1,252 lines total |
| Enemy sync | yes | no |
| Pickup / XP / gold | yes | no |
| Shrine charging state | yes | no |
| Damage attribution | yes | no |
| Inventory sync | yes | no |
| Player replication | yes | yes |

**What to take:** its **API choices**, which are correct and worth copying —
`SteamNetworkingSockets.CreateListenSocketP2P` / `ConnectP2P`,
`SteamNetConnectionStatusChangedCallback_t` for connection lifecycle, `SteamMatchmaking`
lobbies for discovery, `GameLobbyJoinRequested_t` for friends-list joins, and
`SteamFriends.SetRichPresence` for presence.

**What not to take:** its architecture (it has almost none — everything is `static` on one
class), and specifically its send path.

`Networking/SteamNetworking.cs:580`:

```csharp
SteamNetworkingSockets.SendMessageToConnection(conn, ptr, (uint)len, 0, out long _);
//                                                                   ^ nSendFlags = 0
//                                              = k_nSteamNetworkingSend_Unreliable
```

All three of its send helpers are named `SendUnreliable`, `BroadcastUnreliable`,
`SendToHostUnreliable`. That is **correct for Multibonk** — position snapshots are continuous
state where the next packet supersedes a lost one. It is **not** a precedent for one-shot
event RPCs, and it is the same shape of mistake as the Sea-Bass downgrade. Multibonk gets
away with it because it never sends a non-idempotent event.

It also does not call `SteamNetworkingUtils.InitRelayNetworkAccess()`, which costs a
multi-second stall on the first P2P connection while the SDR ticket is fetched. Do call it.

**Verdict:** **reference, not source.** Read it to learn the Steamworks API surface in an
IL2CPP Megabonk context. Do not port code from it.

---

## Decision matrix

| Source | Action |
|---|---|
| `Fcornaire/megabonk-together` | Track. Rebase on it. Upstream general fixes back. |
| `Sea-Bass-cmd/optimized-netplay` | Hand-port ~150 lines (charging fixes + null guards). Reject the rest. |
| `Vanlichtinstein1945/Multibonk` | Read for Steamworks API patterns. Port no code. |

---

## Where the real work is

Ranked by value, after reading all three:

1. **Fix the charging state machine and the ID-allocation race** — real bugs, small patches,
   transport-independent. See [`01-critical-fixes.md`](01-critical-fixes.md).
2. **Add a protocol version gate** — currently a mismatched build silently corrupts a
   session. This blocks every future wire-format change, including the golden-shrine fix.
3. **Migrate to Steamworks.NET** — deletes the NAT-punch path, the relay fallback,
   `WebsocketClientService.cs`, and all of `src/server`. Also makes message handling
   main-thread by construction, which removes an entire race class. See
   [`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md).
4. **Attack the real GC hot paths** — `TargetSwitcher.Update` at 600 enemies, and
   `GetAllPlayersAlive()`. Neither is touched by any fork. See
   [`04-performance-and-gc.md`](04-performance-and-gc.md).
5. **Decompile the handful of game internals** that block confident fixes —
   `BaseSummoner.giveCreditsTimer` above all. See
   [`../reverse-engineering/01-investigation-targets.md`](../reverse-engineering/01-investigation-targets.md).

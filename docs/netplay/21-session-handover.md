# Session handover 21: fixing the item and weapon visual audit, tested hands-off

Written 2026-10-24 to start a fresh session on the remaining findings of
[`../reverse-engineering/02-item-visual-audit.md`](../reverse-engineering/02-item-visual-audit.md), with
every fix verified by a hands-off two-copy session rather than a person at the keyboard.

Branch `claude/omnibonk-1.0` on `CEOYap/megabonk-together`. Nothing here is released.

---

## 1. Where things stand

### Committed and pushed (latest first)

| Commit | What |
|---|---|
| `e740a01` | Exact mine explosions from the host (tag 86 `MineExploded`, protocol 6); weapon visual audit |
| `3233c81` | OB-58 revision: received mines explode through their own check; `TestWeapons`; `[explosions]` counters |
| `c0c03cc` | Hands-off sessions: Debug auto-host, `run-local-session.ps1`, `capture-game-windows.ps1`, CLAUDE.md "Testing in game" |
| `5b05839` | OB-58: received projectiles tracked by instance id |
| `6ef0458` | Merge of OB-57: teammate Shadowstep and Flex copies no longer react to the local player |
| `1f01183` | OB-25 rocket log spam, teammate Soul Harvester (OB-56), items dying at stage 2, client rocket explosions |

### Latest change: exact mine explosions (protocol 6, `e740a01`)

Tested; see OB-58 in `08-observed-bugs.md`:

- tag 86 `MineExploded` (host to all clients, `ReliableUnordered`), `Protocol.Version` 5 to 6;
- `ProjectileMinesPatches`: host sends from `Explode`; clients skip `MyFixedUpdate` for received mines
  and draw exactly what arrives; `InitReceived` copies `TryInit`'s scale-in;
- `TestLoadout` grants 5 s after run start (a grant at the start was dropped by the client);
- docs: `02-delivery-method-reference.md` (tag 86), OB-58, and the new weapon section of the audit.

Final hands-off result, aligned on the shared `[desync]` digest numbers: rockets 449/449 and mines 43/43
(host / client), exact from the start; no errors, no `Look rotation` warnings.

### Bug log status

| Entry | Status |
|---|---|
| OB-25 rocket log spam, client explosions | Fixed, confirmed |
| OB-56 item projectile visuals (family 1) | Fixed, confirmed; families 2 to 4 open (below) |
| OB-57 Shadowstep / Flex | Fixed, **not run in game** |
| OB-58 received projectiles, mines | Fixed, confirmed hands-off at 0% loss |
| OB-55 crypt-to-crypt id loss | Open, parked |

---

## 2. The work: remaining audit findings, in order

Read the audit doc first; addresses and evidence are there. Each item below names the check that
proves it, to be counted on **both** copies.

1. **Poison Flask (and Bluetooth): the mine shape.** On a client a received flask's own `HitEnemy` /
   `HitOther` calls `ExplodeFlask` (0x1804EF5D0): it plays `effect`, poisons locally, then calls a
   `ProjectileDone` the client refuses, so the flask can explode again. Bluetooth `HitTarget`
   (0x1804E6320) repeats the same way. Fix as for mines: block the local trigger and have the host
   send the explosion (a new tag, protocol bump), or at minimum hide the copy after its first
   explosion. Check: host `ExplodeFlask` count vs client flask explosions drawn.
2. **Weapon hit sparks never drawn on clients.** `WeaponAttack.ProjectileHit` (0x1804504D0) draws
   `EffectManager.EnemyHitEffect`; the base `ProjectileBase.HitEnemy` is blocked on clients, as are
   Black Hole, Cringe and Hero Sword, Dexecutioner and Bananarang hit paths, and Axe's pop list stays
   empty. Proposed: a client already receives every host hit in `EnemyDamagedBatch` (weapon source,
   enemy id), so it can draw that weapon's hit effect at the enemy, with no protocol change. Mind the
   volume: batch hits are frequent, so throttle per enemy. Check: host `ProjectileHit` count vs
   client hit effects drawn.
3. **Attack-size visuals.** Clients never run `TryInit` on a received copy, so size upgrades do not
   show (Arrow trail width; Poison Flask, Scythe and Sniper `localScale`; melee and sword zones).
   Apply `GetAttackSizeMultiplier` on the client (approximate: it reads that machine's stats) or send
   the host's size. Check: captures with `-HostItems` including size items.
4. **Item visual families 2 to 4** (OB-56, audit "Chokepoints"): Slutty Cannon item rockets,
   `EffectManager` / `EffectPlayer.Play` effects (Zooma, Mirror, gloves, ...), pooled one-shot effects
   (Bonker, Ice Cube, ...). Each needs its own design; the audit has per-item addresses.
5. **Leads, UNVERIFIED:** Lightning Orb double hit (the client replays tag 41 and may forward it back).

Do one item at a time: fix, build both configurations, run a counted hands-off session, record the
numbers in the matching OB entry, commit.

---

## 3. How to test hands-off

Full detail: CLAUDE.md "Testing in game (hands-off)" and `05-local-testing.md` "Hands-off session".

```powershell
pwsh scripts/test/run-local-session.ps1 -Minutes 9 -StageMinutes 0 -NoBuild -HostWeapons "Mine, Rockets" -ClientWeapons "Mine"
```

- Build first: `CI=true dotnet build src/plugin/OmniBonk.Plugin.csproj -c Debug` (and `-c Release`). The
  whole solution fails at the server project while the local matchmaker runs (it locks its DLL); the
  server only relays payload bytes, so it needs no rebuild for a new message.
- Launch the script with `run_in_background` and `*> file` to a file in an existing folder; wait with an
  `until grep -q "Logs and summary" file` loop, never sleeps or a chatty Monitor.
- **Add a counter on both sides** for whatever you fix, like `Helpers/ExplosionDiagnostics.cs`, and
  compare host against client. A single "it ran" line hid a 17x over-draw in OB-58.
- **Align the two logs on the `[desync]` digest numbers** (host `sent digest #N`, client
  `N digest(s) compared`): each side's counter line is throttled independently.
- Pick weapons with `-HostWeapons` (the host simulates every weapon, so its weapons are what the client
  receives) and items with `-HostItems` / `-ClientItems`. EWeapon names: `Mine`, `Rockets`, ...
- `-StageMinutes 0` keeps one stage, so counts are not split by a stage change. The default map is
  Desert (`-Map`); crypt rooms and Forest trap the bot.
- Look at the game with `pwsh scripts/test/capture-game-windows.ps1 -OutDir <dir>` and read the PNGs.
- Stop early by closing the game windows (`CloseMainWindow`); the script restores the configs.

Environment on this PC: host `C:\Program Files (x86)\Steam\steamapps\common\Megabonk`, client
`D:\Games\Megabonk`, matchmaker already listening on 5000. A stale `MegabonkTogether` plugin was moved
to the host's `BepInEx\disabled-plugins\` on 2026-10-24; a pre-rename worktree build would put one
back (build those with `CI=true`).

---

## 4. Traps from this cycle

- **Ghidra field names sit one slot (8 bytes) out** on `ItemProjectile`, `ProjectileBase` and
  `ProjectileMines`; map by offset against dump.cs before trusting a name.
- **`GetComponentsInChildren<T>(bool)` compiles but throws `MissingMethodException` in game**; use
  `Il2CppFindHelper.RuntimeGetComponentsInChildren<T>`.
- **Static `.rdata` constants** can be read straight from `GameAssembly.dll` (PE section mapping,
  image base 0x180000000); that is how the mine formula's 1 / 0.3 / 3 were found.
- **`DynamicData` tags die with their wrapper** (OB-25, OB-58); key anything a guard depends on by
  `GetInstanceID()`.
- **A client never runs `Set`/`TryInit` on a received projectile**; anything those compute is a
  prefab default on clients.
- New union tags are append-only and need a `Protocol.Version` bump and a row in
  `02-delivery-method-reference.md`.

---

## 5. Starting prompt for the next session

```
Continue on branch claude/omnibonk-1.0 in the OmniBonk repo. Read docs/netplay/21-session-handover.md
first, then CLAUDE.md "Testing in game (hands-off)" and docs/reverse-engineering/02-item-visual-audit.md
(the "Weapon projectiles with rocket- or mine-like problems" section).

Work through section 2 of the handover in order, starting with Poison Flask and Bluetooth. For each
item: decompile what you need (scripts/re/decompile_headless.py, VAs from
megabonk-re/build-21750826/dump.cs), fix it, build Debug and Release, add a host/client counter, and
verify with a hands-off session (scripts/test/run-local-session.ps1, run in the background). Report
the counts aligned on the [desync] digest numbers, record them in the matching 08-observed-bugs.md
entry, and stop to ask me before committing each fix.

Leave src/plugin/Patches/DeathStatsUi.cs out of commits if it is present. Mark anything not run in
game as UNVERIFIED.
```

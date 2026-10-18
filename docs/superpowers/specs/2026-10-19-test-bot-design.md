# Test bot — design

Written 2026-10-19. Status: **design, not built.**

## Intent

The project owner tests multiplayer with one friend, who is often unavailable. Every fix in this
mod is unverified until played with two players, so testing stalls whenever the friend is busy.

The bot replaces the friend for a test session. A second copy of Megabonk runs on the owner's PC with
the bot switched on in its config, joins the owner's game as the client, and plays the run with the
owner until the end. Success, in the owner's words: the two of them **survive together until the end**,
and **if the owner dies, the bot revives them**.

It exists to put real client-side traffic through the netcode. It is not an AI teammate for normal
play.

## Goals

1. **Joins by itself.** The owner clicks Host; the bot copy joins the owner's lobby, readies up and
   picks a character with no input.
2. **Plays the whole run.** Reaches the final boss and the end screen alongside the owner, through
   every stage transition and portal.
3. **Cannot die** (owner's choice). Its survival does not depend on build or skill.
4. **Follows the owner loosely.** Stays within a set distance, keeps moving (circling, strafing), never
   stands still for long.
5. **Revives the owner.** When the owner dies, walks to the owner's revive coffin and uses it.
6. **Uses chests and shrines**, but only ones closer to the bot than to the owner, so it does not race
   the owner.
7. **Picks rewards at random**: level-up, chest and shrine choices. More weapons and items over many
   runs means more sync paths exercised.
8. **Random character** each run.
9. **Gets unstuck**: after a few seconds without progress toward its goal, teleports next to the owner
   and logs it.
10. **Says what it is doing**: a throttled `[bot]` log line, so an unattended run that stalled can be
    diagnosed from the log.

## Non-goals

- Dodging attacks well, aiming, or playing to win. Invincibility makes this unnecessary.
- Being the host. The bot is always the client.
- Charging the final-boss pylons, using portals or starting boss fights. The owner does objectives.
  (Pylons were offered and not chosen.)
- Running on the friend's PC over Steam. Second copy on the owner's PC only.
- More than one bot.
- Shipping to players. It is a developer tool behind a config switch.

## Anti-goals (failure even if it works)

- **Any change to singleplayer, or to a session where the switch is off.** Every bot code path is
  behind the config flag *and* the existing netplay-session guard.
- **Hiding netcode bugs.** The bot must drive the game through the same paths a person does
  (input, interactables, the game's own reward buttons), not by calling sync services or sending
  messages directly. If the bot cannot do something the way a player would, that is a finding, not
  something to work around.
- **A wire-format change.** No new message, no new field. The bot is local to the client copy.
- **Per-frame cost or log spam on the owner's copy.** The bot code does nothing on a copy where the
  switch is off; on the bot copy its log is throttled.

## Constraints

- **Project rules** (`CLAUDE.md`): every Harmony patch opens with `HasNetplaySessionStarted()` and an
  ownership check; MemoryPack tags are append-only; send through `INetTransport`; nothing is verified
  until played; mark behaviour inferred from stubs **UNVERIFIED** and decompile before relying on it.
- **Two copies on one PC.** Steam refuses to launch the same appid twice, so the bot copy runs from its
  own folder, started directly (`docs/netplay/05-local-testing.md`). A copy already exists at
  `D:\Games\Megabonk - Copy`. That guide's `D:\MegabonkTest2` path does not exist.
- **Steam transport cannot be used.** `UseSteamTransport` defaults to `true`, and a directly-started
  copy runs with Steam uninitialised. Both copies must set `UseSteamTransport = false` and point at a
  local matchmaking server (`ws://127.0.0.1:5000`, `src/server`), as in the local-testing guide.
- **IL2CPP.** Stubs have no bodies; the game members below come from `scripts/re/interop_members.py`
  and need decompiling before they are relied on.
- **Loopback has no loss or latency.** A clean bot run proves a feature works, not that the netcode is
  right; `clumsy` on the bot copy's port is still needed for that (local-testing guide).

## How it works — what was decided

### Config

New `[Bot]` section in the bot copy's `MegabonkTogether.cfg`:

- `BotMode` (bool, default `false`) — the switch. Off means none of the bot runs.
- `FollowDistance` — how far it may drift from the owner.
- `StuckSeconds` — no-progress time before it teleports to the owner.

### Joining

**By a file, chosen by the owner.** When the owner's copy hosts a Friendlies lobby, it writes the
room code to a small file in a location both copies can reach. The bot copy, on the main menu with
`BotMode` on, watches for that file, joins with the code, readies up and picks a **random**
character. Writing the file is the one part of this feature that runs on the owner's copy; it is
harmless there and could sit behind its own switch.

### In the run

- **Movement**: the bot supplies movement input the way a stick would, feeding the game's input read
  (`MyInputManager.GetAxis`, read by `PlayerMovement`), not by setting positions. It aims for a point
  near the owner, within `FollowDistance`, and keeps changing that point so it circles and strafes.
- **Invincible**: the bot's own player takes no damage (`PlayerHealth.CanTakeDamage` is the candidate
  hook). Applied on the bot copy only, to its local player. HP is owner-authoritative in this mod, so
  the owner's copy just sees a teammate at full health.
- **Chests and shrines**: it detours to an interactable only if that interactable is closer to the bot
  than to the owner, walks into range, and uses it through the game's own interact path
  (`DetectInteractables.currentInteractable` / `TryInteract`), then returns to following.
- **Rewards**: when a level-up, chest or shrine window opens, it picks a **random** offer through the
  window's own controls. The game has `UpgradePicker.AutoSelectUpgrade`, and the chest window has
  `OpenButton` / `TakeButton`. Shared experience (the pause barrier) must still release, which is
  itself something worth testing.
- **Reviving**: when the owner dies, the bot walks to the owner's revive coffin
  (`InteractableDesertGrave`, spawned by `SpawnReviver`) and uses it, then resumes following.
- **Stuck**: if it makes no progress toward its target for `StuckSeconds`, it teleports next to the
  owner and logs it.
- **Status**: one `[bot]` line every 10 seconds (the diagnostics' shared interval): what it is doing,
  distance to the owner, how many rewards picked, interactables used, revives, and teleports.

## Calls made by the designer, not the owner

Settled while reviewing the design; the owner can overturn any of them.

1. **The bot copy keeps running when its window is not focused.** With `BotMode` on it sets
   `Application.runInBackground = true`. Otherwise Unity may pause the unfocused copy, and the owner is
   always focused on their own window. Must be confirmed in the first run: if the unfocused copy still
   stalls, that blocks everything else.
2. **The bot copy runs light.** With `BotMode` on it caps itself at 30 FPS (`Application.targetFrameRate`)
   and should be run windowed at a low resolution, so the owner's copy keeps most of the PC.
3. **Performance numbers from a bot session are not comparable** with a two-PC session: two game copies
   share one machine. `[perf]` lines from bot runs only compare with other bot runs.
4. **The room-code file is written on every Friendlies host**, not behind its own switch. It is a few
   bytes on the main menu, and a second switch the owner forgets to turn on would make the bot silently
   fail to join.

## Left to the builder

- Where in the plugin it lives (likely one injected `MonoBehaviour` under `Scripts/`, plus thin patches
  for the input and damage hooks), and its internal state machine.
- The exact hook for movement input and for invincibility, after decompiling the candidates above.
- The file location and format for the room code.
- How the bot reads and presses the lobby, character-select and reward controls.
- Steering: how it picks points near the owner, avoids walking into walls, and measures progress.
- Default values for `FollowDistance` and `StuckSeconds`.
- How the bot finds the owner's coffin and nearby interactables.
- Fixing `05-local-testing.md` (wrong copy path, missing `UseSteamTransport = false`) as part of the
  setup instructions this feature needs.

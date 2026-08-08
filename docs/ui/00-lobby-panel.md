# Lobby panel — design

The pre-game flow, why it is changing, and what is deliberately deferred.

Status legend per [`../README.md`](../README.md).

---

## The problem

Today there is no point at which a player can see **who is in the lobby**. Lobby state is spread
across three places and none of them is a member list:

| Where | What it shows |
|---|---|
| Top-left banner | "Friendlies — Hosting a friendly lobby" |
| Top-right block | `Role: Host`, `Code: ******`, `Shared Experience: ON`, `COPY CODE` |
| Character-selection window | the actual screen, doing double duty as the lobby |

So the character-selection screen *is* the lobby, readiness *is* character selection, and a
player who has joined is invisible until the run starts. Two people cannot tell whether a third
has arrived.

There is also no way to leave a lobby without quitting to the menu.

## The flow, before and after

**Before**

```
TOGETHER! → [name / options / Random / Friendlies]
          → Friendlies → Host, or enter code → Join
          → character select          ← this is also the lobby, and the readiness gate
          → host picks map            ← gated on every peer having selected a character
          → run starts
```

**After**

```
TOGETHER! → [name / options / Random / Friendlies]
          → Friendlies → Host, or enter code → Join
          → LOBBY PANEL (centred)     ← new: member list, and the readiness gate
              members: name, host crown, ready state
              Leave lobby · Copy lobby code · Join from clipboard
              [ Ready ]  [ Start ]  [ Back ]
          → host presses Start (enabled only when every member is ready)
          → character select          ← no longer the readiness gate
          → host picks map            ← unchanged: gated on all characters confirmed
          → run starts
```

The panel is **centred**, not docked to a side, and Ready / Start / Back sit at its base — so the
one thing a player must look at is in the one place they are already looking.

## Decisions

**Readiness here is a new concept, not `Player.IsReady`.** That field is the *level-transition*
barrier, made host-authoritative in the round-identity work, and `ReadinessService` owns it.
Lobby readiness answers a different question ("I am ready for the host to start") at a different
time. Overloading one flag for both would resurrect exactly the ambiguity that
[the readiness race](../netplay/08-observed-bugs.md) came from — a peer unable to distinguish its
own write from the host's answer.

**Lobby readiness cannot be a field on `Player`.** `Player` is serialized inside `LobbyUpdates`
(union tag 0). MemoryPack is positional, so widening it changes a shipped tag's layout and
corrupts sessions between builds — the hazard `CLAUDE.md` forbids. It therefore needs its own
appended tag, host-authoritative like everything else.

**Start is host-gated and requires every member ready.** Enabled state is computed from the same
set the host uses to decide, not from a second local copy.

## Deferred, and why

| Item | Why it waits |
|---|---|
| **Invite** | It is a Steam overlay call — [`ActivateGameOverlayInviteDialog`](https://partner.steamgames.com/doc/api/ISteamFriends) against a Steam lobby, which is Steamworks migration Phase 3. Built against the current WebSocket matchmaker it would be "copy a code and paste it somewhere", which the Copy button already does. |
| **Member avatars** | Steam avatars, same phase. Rows show a name and a host crown until then. |
| **Quickplay** | The existing **Random** queue already fills this role; renaming it is churn, not a feature. |

The panel binds to a small view-model rather than to `PlayerManagerService` directly, so Phase 3
re-points the member list at Steam lobby members and adds avatars and invite **without touching
the panel**. Same seam reasoning as `INetTransport`.

## Increments

1. **Panel and member list** — centred panel, members with host crown, Leave / Copy / Join from
   clipboard, Back. No wire change: the member list reads the roster that already replicates.
2. **Ready / Start gate** — appended union tags for lobby readiness, host authority, and moving
   the character-select step behind the host's Start.

Increment 1 is useful on its own: it makes the lobby visible and leaveable. Increment 2 changes
the flow's shape and is where the wire change lands.

## Construction notes

Buttons are cloned from the game's own `MainMenu.btnPlay` prefab and given a `CustomButton`
(`Scripts/Button/CustomButton.cs`), which routes clicks through a `System.Action` because Unity
`Action`s do not survive the BepInEx/IL2CPP boundary. That is how the panel gets the game's own
look without shipping art.

The panel is a `ModalBase` subclass — it already builds a centred blocker plus panel on the
`Canvas` and exposes `OnUICreated()`.

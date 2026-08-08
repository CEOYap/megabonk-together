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

1. ~~**Panel and member list**~~ — **done.** Centred panel, members with host crown, Copy / Join
   from clipboard / Leave. No wire change.
2. ~~**Ready / Start gate**~~ — **done.** Three appended union tags, host authority, and the
   character-select step moved behind the host's Start.

### Wire, as built

| Tag | Message | Direction |
|---|---|---|
| 73 | `LobbyReadyChanged` | client → host, "I toggled my readiness" |
| 74 | `LobbyReadyState` | host → clients, the whole authoritative set |
| 75 | `LobbyStartRequested` | host → clients, "advance to character selection" |

**A client shows its own toggle immediately, then reconciles.** The press is applied locally and
held until the host's broadcast agrees with it, with a 3-second timeout after which the host wins
and the disagreement is logged.

This is deliberately *not* what the level-load barrier does, and the difference is worth being
precise about. That barrier's retry loop used a self-written flag as proof the host had
acknowledged it, so a client could exit having sent nothing — the bug that hung the lobby. Here
nothing reads the local value to make a decision: `Start` is host-only and the host checks its own
set. So an optimistic value costs correctness nothing, and waiting a round-trip only makes the
button look broken.

The holding matters as much as the writing. `ApplyHostState` replaces the whole set, so a
broadcast the host generated *before* it processed the toggle would flip the label back and then
forward again — a visible flicker. The pending value overrides the mirrored set until confirmed.
The timeout is what stops it becoming a silent divergence.

**The set is sent whole, not as a delta.** At most six entries, changing only when somebody
presses a button, so a delta saves nothing measurable and adds the failure this project keeps
paying for: a peer that misses one update and stays wrong with nothing to correct it.

**Start is an instruction, not an inference.** A client could advance itself the moment the last
member readies — and then the host's Start would do nothing, because everyone would already have
gone. Making it explicit keeps the host in control of when the lobby ends.

**`AreAllMembersReady` is false for an empty lobby**, which is the opposite of the choice
`ReadinessService` makes for its own barrier. Deliberate: there, an empty participant set means a
round with nobody to wait for and refusing to complete it would hang. Here it means nobody has
arrived, and completing it would let a host start alone.

### Deviation from the requested layout

The request was Ready / Start / Back. **Back is merged into Leave Lobby**: the panel only exists
while you are in a lobby, so going back *is* leaving, and two buttons doing one thing is worse
than one that says what it does. A distinct Back earns its place if the flow ever gains a screen
behind this one.

## Construction notes

Buttons are cloned from the game's own `MainMenu.btnPlay` prefab and given a `CustomButton`
(`Scripts/Button/CustomButton.cs`), which routes clicks through a `System.Action` because Unity
`Action`s do not survive the BepInEx/IL2CPP boundary. That is how the panel gets the game's own
look without shipping art.

The panel is a `ModalBase` subclass — it already builds a centred blocker plus panel on the
`Canvas` and exposes `OnUICreated()`.

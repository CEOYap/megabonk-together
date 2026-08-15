# Planned: TOGETHER! goes straight to the lobby

**Status: Phase 5, steps 1–3 done, steps 4 and 5 planned. `NetworkMenuTab` is unreachable — nothing
constructs it. Random is retired rather than rehomed. Netplay Options is offline until step 4. None
of steps 2 or 3 has been run in-game.**

Today, pressing **TOGETHER!** opens `NetworkMenuTab` — a name box, Netplay Options, and a
Random / Friendlies choice, and behind Friendlies another screen with Host, a room-code box and
Join. Only then does the lobby panel appear. The plan is to delete that middle layer: TOGETHER!
creates or joins a lobby and shows the lobby panel.

Worth doing. Every one of those screens exists because the mod had nowhere else to put a control,
and the lobby panel now has somewhere.

## What has to be rehomed first

`NetworkMenuTab` is **1,268 lines** (was 1,328; step 1 took 190 out) and is not only menus. Deleting
the file means finding a home for each of these, and three of them are not UI at all.

| What it owns | Where it should go |
|---|---|
| ~~`OnJoinClicked` / `JoinWithCode` — sets `Plugin.Instance.Mode`, calls `HandleNetworking()`, starts `HandleFriendlies()`~~ | **Done — the service.** It no longer touches `Mode` or `NetworkHandler` at all; it calls `Join` and watches. |
| ~~`HandleFriendlies()` — the connect coroutine~~ | **Done — deleted.** |
| ~~`HandleConnectionStatus()` — 30s timeout, the loader, the Stop button~~ | **Partly done.** The timeout and the matchmaker polling are the service's; the loader and the Stop button are still the menu's, and still need the lobby panel's connecting state (step 3). |
| `OnHostClicked` / `OnRandomClicked` | TOGETHER! itself, and a decision about Random — see below |
| Player name box | Gone with `ModConfig.PlayerName`, which Phase 5 also drops. The Steam persona is already adopted in its place. |
| Netplay Options — save toggle, shared experience toggle | Either the lobby panel or config-file-only. Both are `ModConfig` entries with no other UI. |
| `TryJoinFromInvite` / `PrefillInvitedCode` | The lobby panel. Both were put here because it owned the code box. |

## Three decisions this forces

**What happens to Random. — Decided at step 3: retired.** The lobby panel gets **no Quickplay
button**, so nothing reaches the queue any more.

Three things pointed the same way. The Steam transport refuses quickplay outright and will keep
refusing it until there is a lobby browser to match strangers with, so on the transport everything
is moving to, the button would never work. The matchmaker transport that *can* serve it is deleted
one release after the loss test. And the button did not fit: with Join Code needing the same
alone-and-not-committed slot, a matchmaker host sitting alone in their own lobby would have shown
seven buttons against a column budgeted for about six, which puts the card past the 1080 reference.

`INetplaySessionService.Quickplay` and `NetworkModeType.Random` still exist and still work; nothing
calls them. Step 5 can take them out with the rest.

**Host or join, without a screen to ask on.** TOGETHER! cannot both create a lobby and join one.
The likely answer is that it always hosts, and joining happens through Copy Code / Join From
Clipboard on the panel, plus invites — which is already how a joiner arrives. Worth confirming that
Join From Clipboard actually works first: `LobbyPanel.OnJoinRequested` is declared and invoked but
**never assigned**, so that button is currently inert.

**Where connection failure is shown.** Today a failed join leaves you on a menu with a status line.
With no menu, the lobby panel has to open in a connecting state and be able to close itself with a
reason. That is the part most likely to strand somebody.

## Callers to update

Small, and all of them mechanical. **Three of the four are done (step 2).**

- ~~`Patches/WindowManager.cs:52` destroys `Plugin.Instance.NetworkTab` on a window change.~~ Done —
  it also destroys the lobby panel now, since `ResetNetworking` on the line above has just ended the
  session underneath it.
- `Plugin.cs:64` holds the reference; `Plugin.cs:215` registers the type. **Still to do, in step 5**
  — both are harmless while `NetworkTab` simply stays null forever.
- ~~`Scripts/Button/PlayTogetherButton.cs` creates it — this becomes the new entry point.~~ Done —
  `OpenLobby()` hosts, `JoinLobby(code)` joins, and both refuse to start a second session if the
  panel is already up.
- ~~`Scripts/SteamTicker.cs:217` checks `NetworkTab != null` … and calls `OpenNetworkTab()`.~~ Done
  — both are now `LobbyPanel.Current`, and it consumes the room code itself.

## It interacts with the button crash

`NetworkMenuTab` holds **13 of the 18** call sites in
[`04-custom-button-null-background.md`](04-custom-button-null-background.md). Deleting it removes
most of that bug's surface for free.

That is not a reason to delay the button fix — it is needed long before Phase 5, and
`PlayTogetherButton`, `WindowManager`, `ChangelogModal` and `UpdateAvailableModal` keep their
share regardless. But whoever does the button fix should know that most of the file they are
editing is scheduled for deletion, and not spend care on it accordingly.

## Two constraints found when trying to do the panel work first

Both were found by looking rather than by building, and both say the same thing: the panel
additions depend on TOGETHER! opening the panel, not the other way round.

**Quickplay, Join From Clipboard and a connecting window are unreachable until then.** ~~The panel is
only ever created *after* a session exists — `ShowLobbyPanel` runs when hosting connects or a match
is found — so it is always shown in-lobby.~~ **Step 2 resolved this.** The panel now opens *before*
the session starts, so the not-in-lobby state that `SetButtonVisible(joinFromClipboardButton,
!inLobby)` always anticipated is finally produced, and step 3's three additions have somewhere to
live. `LobbyPanel.OnJoinRequested` is still unassigned and therefore still inert — assigning it is
step 3's job.

**Netplay Options does not fit in the button column.** It reserves 520 units for five buttons at
about 96 each, and five is already the worst case — Invite, Copy Code, Ready, Start, Leave Lobby.
A sixth needs about 616, which puts the card near 1050 against a 1080 reference. So Netplay Options
has to be a **sub-view** that replaces the member list and column with the two toggles and a Back
button, the way the menu does it today, rather than another entry in the column. That is prefab
work plus a view-state in `LobbyPanel`, not a button.

## The order to do it in

The session-setup extraction is **already done** — `INetplaySessionService` and
`NetplaySessionService` exist and are registered. Nothing calls them yet, deliberately: it was
landed on its own so it changed no behaviour. Everything below is Phase 5.

**1. Put `NetworkMenuTab` onto `INetplaySessionService`.** ~~Its Host, Join and Random handlers call
the service, and its screens reflect the service's state and message instead of driving their own
coroutines.~~ **Done.** Both halves: the Steam paths landed on the Phase 4 branch, the matchmaker
paths and the deletions after it.

> `HandleFriendlies` and `HandleConnectionStatus` are gone, replaced by a single `WatchSession` that
> watches `INetplaySessionService.State`. `NetworkHandler.HandleNetworking()` now has exactly one
> caller — the service — so session setup has one implementation again.
>
> **One trap worth keeping.** `WatchSession` takes the flow as a parameter instead of reading
> `Plugin.Instance.Mode`, because a failure inside the service calls `ResetNetworking`, which does
> `Plugin.Instance.Mode = new()`, *before* the state the watcher is waiting on becomes `Failed`. Read
> back afterwards the mode is always `Random` — the zero value — so a failed Join would restore the
> quickplay screen. Anything else that later reads session state after a failure has the same
> problem.
>
> **UNVERIFIED**: not yet run in-game. Wants a two-player matchmaker session (host, join by code,
> Stop mid-connect) and a re-run of the Steam path.

That went first because it is the only step verifiable with the menu still in place, and because it
gives the connecting window a single owner before anything depends on one.

**2. TOGETHER! opens the lobby panel, always hosting.** ~~`PlayTogetherButton.OpenNetworkTab`
becomes "open the panel"; `SteamTicker`'s invite check and `WindowManager`'s teardown follow.~~
**Done.** `NetworkMenuTab` is unreachable — nothing constructs it.

> `LobbyPanel` gained a static `Current` (the replacement for `Plugin.Instance.NetworkTab != null`)
> and a static `Open` that wires the continue and leave callbacks, which moved off `NetworkMenuTab`
> because they were never menu-specific. The invite path now consumes the room code itself and
> joins with it.
>
> **Two hazards this turned up, both worth keeping:**
>
> - **A second press restarts a working session.** The session service refuses a start only while
>   it is *busy*, so once a lobby is up a second TOGETHER! passes that check and tears the live
>   session down to begin another. The entry point guards on the panel already existing. Anything
>   else that gains a "start a session" button needs the same guard — the service will not save it.
> - **`Current` must be cleared with `ReferenceEquals`, not `==`.** `Destroy` is deferred to end of
>   frame, so a panel opened right after one closes sets `Current` in `Awake` before the outgoing
>   panel's `OnDestroy` runs; a plain `Current = null` there blanks the live one. This is the
>   destroyed-object rule from the Phase 4 branch, met from the other direction.
>
> **Offline as of this step:** Netplay Options and quickplay, both reachable only through the menu.
> The two toggles stay settable in the config file. Steps 3 and 4 bring them back.
>
> **UNVERIFIED**, and this is the step that changes what the button does. The specific risk is that
> the panel now opens against a live main menu rather than after a modal closed itself, and
> `HideMainMenuChrome` has only ever run in the latter case.

**3. Quickplay and Join From Clipboard on the panel, and the connecting window.** ~~All three need
step 2 first.~~ **Done, with one of the three dropped rather than built.**

> **Join Code** (renamed from Join From Clipboard, which was the longest label on the panel).
> `OnJoinRequested` is assigned, so the button does something for the first time. It is gated on
> being **alone** rather than `!inLobby` — step 2 made the latter unreachable — and it cancels the
> current session before joining, because the service refuses a start only while *busy* and would
> otherwise have accepted a join on top of this peer's own lobby.
>
> **The connecting window** is not `LoadingModal`, and that part of the plan was wrong. `LoadingModal`
> parents to the game's `Canvas` via `GameObject.Find`, and the panel builds its own canvas at
> `sortingOrder` 1000 — its blocker would have drawn *behind* the panel and blocked nothing. The
> window is built on the panel's own canvas instead, with sibling order doing the work. Stop is on
> it, wired to `Cancel()`, and closes the panel.
>
> **Quickplay: retired instead** — see *What happens to Random* above.
>
> **The panel gained an exit.** `leaveLobbyButton` was `inLobby`-only, and after step 2 a failed
> host sits on the panel with no lobby — so hiding it left them with no route to the main menu. It
> is always shown now, relabelled **Back** when there is no lobby.
>
> **Known gap:** Stop is mouse-only. The game's `Window` registry collects `MyButton`s beneath its
> own transform and the overlay is a sibling of the panel rather than a child, so focus does not
> walk onto it. Putting the overlay under the panel root would fix that and give up the guarantee
> that the dim covers the whole canvas.
>
> **UNVERIFIED**: none of step 3 has been run in-game.

**4. Netplay Options as a panel sub-view.** Independent of the others and doable at any point: the
two toggles and a Back button, replacing the member list and column rather than adding to it.

**5. Delete `NetworkMenuTab`, `ModConfig.PlayerName`, and the name box with it.**

Steps 2 and 3 want a two-player playtest between them and step 5, because that is the point at
which the only route into a session is the new one.

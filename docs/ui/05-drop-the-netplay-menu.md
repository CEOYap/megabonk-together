# Planned: TOGETHER! goes straight to the lobby

**Status: Phase 5. The session-setup extraction is done and landed; everything else is planned.
Written down so the size of it is known before it is begun — see "The order to do it in".**

Today, pressing **TOGETHER!** opens `NetworkMenuTab` — a name box, Netplay Options, and a
Random / Friendlies choice, and behind Friendlies another screen with Host, a room-code box and
Join. Only then does the lobby panel appear. The plan is to delete that middle layer: TOGETHER!
creates or joins a lobby and shows the lobby panel.

Worth doing. Every one of those screens exists because the mod had nowhere else to put a control,
and the lobby panel now has somewhere.

## What has to be rehomed first

`NetworkMenuTab` is **1,328 lines** and is not only menus. Deleting the file means finding a home
for each of these, and three of them are not UI at all.

| What it owns | Where it should go |
|---|---|
| `OnJoinClicked` / `JoinWithCode` — sets `Plugin.Instance.Mode` (Mode, Role, RoomCode), calls `NetworkHandler.HandleNetworking()`, starts `HandleFriendlies()` | **A service.** This is session setup wearing a button's clothes, and it is the reason the invite path had to call into a menu. |
| `HandleFriendlies()` — the connect coroutine | same service |
| `HandleConnectionStatus()` — 30s timeout, the loader, the Stop button | The lobby panel needs a connecting state; today the loader belongs to the menu being deleted. |
| `OnHostClicked` / `OnRandomClicked` | TOGETHER! itself, and a decision about Random — see below |
| Player name box | Gone with `ModConfig.PlayerName`, which Phase 5 also drops. The Steam persona is already adopted in its place. |
| Netplay Options — save toggle, shared experience toggle | Either the lobby panel or config-file-only. Both are `ModConfig` entries with no other UI. |
| `TryJoinFromInvite` / `PrefillInvitedCode` | The lobby panel. Both were put here because it owned the code box. |

## Three decisions this forces

**What happens to Random.** It is the quickplay queue and the only thing that matches strangers
without a code. "TOGETHER! goes straight to the lobby" answers Friendlies and says nothing about
Random. Either the lobby panel gains a Quickplay button, or Random is dropped as a feature — and
dropping it is a product decision, not a cleanup.

**Host or join, without a screen to ask on.** TOGETHER! cannot both create a lobby and join one.
The likely answer is that it always hosts, and joining happens through Copy Code / Join From
Clipboard on the panel, plus invites — which is already how a joiner arrives. Worth confirming that
Join From Clipboard actually works first: `LobbyPanel.OnJoinRequested` is declared and invoked but
**never assigned**, so that button is currently inert.

**Where connection failure is shown.** Today a failed join leaves you on a menu with a status line.
With no menu, the lobby panel has to open in a connecting state and be able to close itself with a
reason. That is the part most likely to strand somebody.

## Callers to update

Small, and all of them mechanical:

- `Patches/WindowManager.cs:52` destroys `Plugin.Instance.NetworkTab` on a window change.
- `Plugin.cs:64` holds the reference; `Plugin.cs:215` registers the type.
- `Scripts/Button/PlayTogetherButton.cs` creates it — this becomes the new entry point.
- `Scripts/SteamTicker.cs:217` checks `NetworkTab != null` to decide whether to open the menu for
  an invite, and calls `OpenNetworkTab()`. Both become "is the lobby panel already up".

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

**Quickplay, Join From Clipboard and a connecting window are unreachable until then.** The panel is
only ever created *after* a session exists — `ShowLobbyPanel` runs when hosting connects or a match
is found — so it is always shown in-lobby. `SetButtonVisible(joinFromClipboardButton, !inLobby)`
anticipated a not-in-lobby state that nothing produces, which is also why
`LobbyPanel.OnJoinRequested` being unassigned has never been noticed. Adding Quickplay next to it
adds a second button nobody can reach, and a connecting window has nothing to report on because the
panel never starts a connection.

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

**1. Put `NetworkMenuTab` onto `INetplaySessionService`.** Its Host, Join and Random handlers call
the service, and its screens reflect the service's state and message instead of driving their own
coroutines. Until this happens there are two implementations of session setup — the service and the
menu's own copy — which is exactly the drift the service was extracted to prevent.

Do this first because it is the only step verifiable with the menu still in place, and because it
gives the connecting window a single owner before anything depends on one.

**2. TOGETHER! opens the lobby panel, always hosting.** `PlayTogetherButton.OpenNetworkTab` becomes
"open the panel"; `SteamTicker`'s invite check and `WindowManager`'s teardown follow. This is the
step that makes `NetworkMenuTab` unreachable, and it takes Netplay Options offline with it unless
step 4 lands alongside.

**3. Quickplay and Join From Clipboard on the panel, and the connecting window.** All three need
step 2 first — see the constraints above. `LobbyPanel.OnJoinRequested` needs assigning as part of
this; it is currently inert. The connecting window can be `LoadingModal.Show`, driven off
`INetplaySessionService.StateChanged`, with `Cancel()` behind its Stop button.

**4. Netplay Options as a panel sub-view.** Independent of the others and doable at any point: the
two toggles and a Back button, replacing the member list and column rather than adding to it.

**5. Delete `NetworkMenuTab`, `ModConfig.PlayerName`, and the name box with it.**

Steps 2 and 3 want a two-player playtest between them and step 5, because that is the point at
which the only route into a session is the new one.

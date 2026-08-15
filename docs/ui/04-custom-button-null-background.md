# Every mod-made button throws on hover

**Status: fixed, not yet confirmed in game.** Pre-existing, unrelated to the Steamworks work.
Done after Phase 5 rather than on its own branch, because deleting `NetworkMenuTab` took 13 of the
19 call sites with it and left a six-site change.

A `NullReferenceException` is logged every time a mod-made button is clicked or hovered, including
the main menu's own PLAY TOGETHER button. A short session produces tens of them. Nothing visibly
breaks — the buttons still work — which is why it went unnoticed for so long.

**Scale, from session 27217500 (2026-08-15):** the client logged **1716** of these in one co-op run
and the host **7**. It is the single largest source of exceptions in the mod by two orders of
magnitude. See [Measured](#measured-session-27217500) below for what that session does and does not
prove.

## The evidence

From Unity's own player log, **not** `BepInEx/LogOutput.log`:

```
NullReferenceException: Object reference not set to an instance of an object.
  at MyButtonNormal.SetColor (UnityEngine.Color c)
  at MyButtonNormal.StartHover ()          // and StopHover ()
  at Assets.Scripts.Managers.ButtonManager.StartedHoveringButton (MyButton button)
  at MyButton.OnSelect (UnityEngine.EventSystems.BaseEventData eventData)
  at UnityEngine.EventSystems.EventSystem.SetSelectedGameObject (...)
  at UnityEngine.UI.Selectable.OnPointerDown (...)
  at Rewired.Integration.UnityUI.RewiredStandaloneInputModule.ProcessMousePress (...)
```

`MyButtonNormal` (`dump.cs:343825`) holds `background` (a `MaskableGraphic`), `defaultColor`,
`hoverColor` and `colorInited`. `SetColor` dereferences `background`. Selecting a button hovers it,
hovering calls `SetColor`, and on our buttons `background` is null.

## The cause

Every mod-made button is built the same way — clone one of the game's buttons, **destroy** its
`MyButtonNormal`, then add a bare `CustomButton` (which derives from `MyButtonNormal`):

```csharp
var originalButton = buttonObj.GetComponent<MyButtonNormal>();
if (originalButton != null)
{
    UnityEngine.Object.DestroyImmediate(originalButton);   // takes background and the colours with it
}
...
closeButton = buttonObj.AddComponent<CustomButton>();      // background: null
```

So `background`, `defaultColor`, `hoverColor`, `scaleOnHover`, `hoverScale`, `button`,
`disabledOverlay` and `customSfx` are all left at type defaults. `background` being null is what
throws; the rest is why mod buttons have never had the game's hover scaling, colour states or
greyed-out look.

`PlayTogetherButton` is the same shape, and **the claim that used to be here — that it has no clone
to copy from — was wrong.** `MainMenu.Start_Postfix` instantiates `btnPlay.gameObject` and reads its
`MyButtonNormal` like every other site; it simply destroyed it without keeping anything. It needed
no special handling in the end. The related claim that it "accounts for the exceptions that fire at
menu load, before anything has been clicked" is also unsupported — see *Measured* below.

## The fix already exists, in one place

`LobbyPanel.ReplaceWithCustomButton` does it correctly and its doc comment describes this exact
defect: read the eight serialized fields off the original, `DestroyImmediate` it, add the
`CustomButton`, write the fields back. `DestroyImmediate` rather than `Destroy` matters too —
`Destroy` runs at end of frame, leaving two `MyButton`-derived components on one object for the
rest of the frame, and `Window.FindAllButtonsInWindow` collects every `MyButton` it can see.

**Done.** That method's body is now `Helpers/ButtonStyle`, and all six sites use it.

It is a **capture/apply pair** rather than one call that performs the whole swap, because the
component being added differs per site — `CustomButton` at five, `PlayTogetherButton` at the sixth.
A generic `AddComponent<T>` would resolve its IL2CPP type at runtime from the type argument;
splitting it leaves every `AddComponent` written against a concrete type, and `ApplyTo` takes the
base `MyButtonNormal` so both derived types share one path.

The eight fields were confirmed against the interop assembly rather than carried over on faith:
`background`, `defaultColor` and `hoverColor` are declared on `MyButtonNormal`; `scaleOnHover`,
`hoverScale`, `button`, `disabledOverlay` and `customSfx` on the base `MyButton`. `colorInited` and
`state` are **not** carried — they are runtime state rather than authored style.

## Call sites

Six, all converted. The other thirteen were in `NetworkMenuTab` and went with it in Phase 5 step 5.

| File | What it builds |
|---|---|
| `Patches/MainMenu.cs` | the TOGETHER! button (`PlayTogetherButton`) |
| `Patches/WindowManager.cs` | the copy-code button on the friendlies display |
| `Scripts/Modal/ChangelogModal.cs` | its close button |
| `Scripts/Modal/UpdateAvailableModal.cs` | close, and update |
| `Scripts/Modal/LobbyPanel.cs` | every lobby panel button, via `ReplaceWithCustomButton` |

`LobbyPanel` was the one site that was always correct; it now calls the shared helper instead of
holding its own copy.

## Two things this cost, worth not repeating

**Unity's player log keeps stack traces; BepInEx's does not.** `LogOutput.log` records the
exception message and drops Unity's `stackTrace` argument, so every one of these arrived as an
unattributable one-liner. The full trace was in
`%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log` the whole time. **Check that file first**
for anything logged as `[Error : Unity]`.

**Reviewing a log by grepping `Error  :MegabonkTogether` hides this entire class of bug.** Unity
logs the mod's own exceptions under `Unity`, not under the mod, whenever they are thrown inside a
Unity callback. Count `[Error  :     Unity]` too.

## Measured, session 27217500

The first two-machine session to capture both peers. **The host's Unity player log carries the full
trace and it is exactly the one at the top of this file** — all 7 of its exceptions, identical,
`MyButtonNormal.SetColor` ← `StartHover`/`StopHover` ← `ButtonManager.StartedHoveringButton` ←
`MyButton.OnSelect` ← `Selectable.OnPointerDown` ← Rewired. Confirmed, not inferred.

**The client's 1716 are not traced, and that peer's `Player.log` was not captured.** What the
BepInEx log alone supports:

| | |
|---|---|
| when | **zero** before the run starts; 1554 during level 1, 162 during level 2 |
| shape | 252 bursts of consecutive lines, mean 6.8, ranging 1 to 12+ |
| rate | ~2 /s averaged, but bursty rather than steady |

The burst shape is what a single selection change produces here: `StartedHoveringButton` calls
`SetColor` on the button being left and the one being entered, and a window holding several
mod-made buttons throws once per button. 252 bursts is 252 interaction events across a 25-minute
run, which is an ordinary amount of clicking.

So the client's storm is **consistent with this bug and not proven to be it**. One grep of that
machine's `%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log` settles it, and until someone
runs it, "the client throws 1716 of these" is a hypothesis with good evidence behind it rather than
a fact.

### One claim above that this session does not support

The note under *The cause* says `PlayTogetherButton` "accounts for the exceptions that fire at menu
load, before anything has been clicked". In this session **no peer threw a single one before the
run began** — the client's count from launch through join, lobby and character select is zero — and
every host trace has `OnPointerDown` in it, so all 7 were click-driven. Either that claim needs
re-deriving or it depends on a path this session did not take. It should not be relied on when
scoping the fix.

### It gets cheaper if Phase 5 goes first

13 of the 19 call sites are in `NetworkMenuTab`, which Phase 5 step 5 deletes outright. Doing the
button fix before that means writing the shared helper and then applying it 13 times to code with a
scheduled deletion date. After step 5 the list is six sites: `Patches/MainMenu.cs`,
`Patches/WindowManager.cs`, `ChangelogModal`, `UpdateAvailableModal` (×2), and the already-correct
`LobbyPanel`.

That is an argument about ordering, not about severity. Nothing here is user-visible — the buttons
work, they just throw and lack the game's hover polish — so the 1716 is log noise and wasted frame
time rather than a broken feature.

## Confirming the fix

The result is the absence of something, which makes it easy to believe on no evidence. Count the
lines rather than looking at the buttons:

```powershell
(Select-String -Path LogOutput.log -Pattern '\[Error  :     Unity\] NullReferenceException').Count
```

Session 27217500 gave 1716 on the client and 7 on the host. Anything near those numbers means it is
still happening; near zero means it is not. The buttons looked and worked the same either way, which
is why this went unnoticed for so long in the first place.

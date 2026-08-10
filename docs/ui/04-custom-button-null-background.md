# Every mod-made button throws on hover

**Status: diagnosed, not fixed. Pre-existing, unrelated to the Steamworks work. Deliberately left
for its own branch off `main`.**

A `NullReferenceException` is logged every time a mod-made button is clicked or hovered, including
the main menu's own PLAY TOGETHER button. A short session produces tens of them. Nothing visibly
breaks — the buttons still work — which is why it went unnoticed for so long.

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

`PlayTogetherButton` is the same shape without even a clone to copy from — it is
`AddComponent<PlayTogetherButton>()` onto a cloned object, and `PlayTogetherButton : MyButtonNormal`
declares no fields of its own. That one accounts for the exceptions that fire at menu load, before
anything has been clicked.

## The fix already exists, in one place

`LobbyPanel.ReplaceWithCustomButton` does it correctly and its doc comment describes this exact
defect: read the eight serialized fields off the original, `DestroyImmediate` it, add the
`CustomButton`, write the fields back. `DestroyImmediate` rather than `Destroy` matters too —
`Destroy` runs at end of frame, leaving two `MyButton`-derived components on one object for the
rest of the frame, and `Window.FindAllButtonsInWindow` collects every `MyButton` it can see.

**The work is to lift that method out of `LobbyPanel` into a shared helper and use it at every call
site.** It is mechanical; the reason it is a branch of its own is that it touches five files this
work does not otherwise go near.

## Call sites

Nineteen, of which `LobbyPanel.cs:831` is the one already correct — it is inside
`ReplaceWithCustomButton` and copies the fields immediately after.

| File | Lines |
|---|---|
| `Patches/MainMenu.cs` | 45 (`PlayTogetherButton`, no clone to copy from) |
| `Patches/WindowManager.cs` | 269 |
| `Scripts/Modal/ChangelogModal.cs` | 269 |
| `Scripts/Modal/UpdateAvailableModal.cs` | 99, 138 |
| `Scripts/Modal/NetworkMenuTab.cs` | 91, 199, 212, 292, 305, 385, 468, 507, 542, 577, 633, 718, 755 |

`MainMenu.cs:45` needs its own answer rather than the shared helper: there is no original
`MyButtonNormal` being replaced, so the fields have to be copied from whichever button it was
cloned from.

## Two things this cost, worth not repeating

**Unity's player log keeps stack traces; BepInEx's does not.** `LogOutput.log` records the
exception message and drops Unity's `stackTrace` argument, so every one of these arrived as an
unattributable one-liner. The full trace was in
`%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log` the whole time. **Check that file first**
for anything logged as `[Error : Unity]`.

**Reviewing a log by grepping `Error  :MegabonkTogether` hides this entire class of bug.** Unity
logs the mod's own exceptions under `Unity`, not under the mod, whenever they are thrown inside a
Unity callback. Count `[Error  :     Unity]` too.

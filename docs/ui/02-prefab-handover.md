# Handover: moving the lobby panel onto a prefab

Written at the end of the session that built the AssetBundle pipeline. Everything here was
learned the expensive way — three in-game failures and two decompiles. Read it before touching
`LobbyPanel`.

## Where things stand

Branch `claude/lobby-panel-ui`. Builds clean.

| Commit | What |
|---|---|
| `e4b006e` | AssetBundle pipeline: `unity-ui/` authoring project, `UiAssetService`, embedded resource |
| `11ea53c` | Cursor fix — `CanvasGroup` instead of `SetActive(false)` |
| `354e616` | Made the panel a game `Window` — **reverted**, but the idea was right; see below |
| `c7e2148` | The revert |

Current in-game behaviour: panel renders centred, main menu hidden behind it, cursor works,
buttons respond — **and every click also triggers the main menu's focused button (PLAY)**, so
pressing Copy Code copies the code and then advances to character selection.

A bundle **has** been built and is embedded — the DLL carries
`MegabonkTogether.Resources.megabonktogether.ui` at 8,761 bytes. Nothing consumes it yet;
`UiAssetService` has still never executed at runtime.

## The editor does not have to be opened by hand

The whole pipeline runs headlessly, which is how the first prefab and bundle were produced:

```powershell
$u = "C:\Program Files\Unity\Hub\Editor\2023.2.22f1\Editor\Unity.exe"
& $u -batchmode -quit -nographics -projectPath unity-ui `
     -executeMethod MegabonkTogether.UiAuthoring.BuildUiBundle.Build -logFile bundle.log
```

Swap the method for `ScaffoldLobbyPanel.Scaffold` to regenerate the prefab. Use
`Start-Process -Wait -PassThru` rather than the call operator if you need a reliable exit code.

This makes the bundle checkable from a terminal like everything else here — a prefab change no
longer needs a human in the editor to reach a running game.

## The one thing that has to be right

Megabonk does not drive menu buttons from Unity's EventSystem alone. Each screen is a `Window`
holding an `allButtons` registry; `WindowManager` keeps exactly one focused, and a click
activates *the focused window's* button. Decompiled bodies (`megabonk-re/decompiled/`):

```
Window.FindAllButtonsInWindow():
    allButtons = GetComponentsInChildren<MyButton>(includeInactive: true).ToList()
    allButtonsHashed = new HashSet<GameObject>(allButtons.Select(b => b.gameObject))

Window.FocusWindow():
    if (allButtons == null) FindAllButtonsInWindow()
    foreach (b in allButtons) b.SetFocus(true)
    Invoke("DelayedButtonFocus", openDelay)
```

`AddComponent<Window>()` fires `OnEnable` → `FocusWindow()` **immediately**.

**This is why `354e616` broke all input.** It added the Window before building the buttons, so
`FocusWindow` ran against an empty hierarchy, cached an empty `allButtons`, and focused nothing.
Registering the buttons later in `Refresh()` repopulated the list but never re-focused.

An earlier note in that commit blamed unserialized `savedBtn`/`startBtn`/`openDelay` on a
runtime-added Window. **That was wrong.** The reference implementation also adds `Window` at
runtime with the same all-default fields and works fine. Ordering was the whole problem.

Required sequence, which a prefab satisfies naturally and incremental building cannot:

1. Instantiate the prefab — the full hierarchy exists at once
2. `AddComponent<Window>()` on the instantiated root
3. `FindAllButtonsInWindow()`
4. Re-run step 3 after any change to which buttons are visible

The reference waits `WaitForEndOfFrame` before its first `FindAllButtonsInWindow()`. Keep that:
prefab children exist immediately but layout-group positions do not.

## The reference bundle is loadable

Its embedded bundle was extracted and its manifest read. Every `MonoBehaviour` entry points at
stock Unity/TMP package script GUIDs; a string scan finds no reference to their own assembly.
The prefabs are pure visual scaffolding — RectTransform, Image, TMP, Canvas, CanvasScaler — and
every custom component is attached at runtime.

So there are no broken script references to worry about, and the same is true of any prefab we
author: **do not put mod MonoBehaviours on the prefab.** Add them after `Instantiate`.

Permission to reuse their assets was given by the author directly. Commit messages must still
not name that mod or its author — standing instruction across this whole branch.

## What is already built

`unity-ui/` — Unity **2023.2.22f1** (must match exactly; any other major returns null from
`LoadFromMemory` with no error). Two menu items:

- **Scaffold Lobby Panel Prefab** — generates the hierarchy the runtime binds to
- **Build UI Bundle** (`Ctrl+Shift+B`) — writes `src/plugin/Resources/megabonktogether.ui`

`UiAssetService` (`IUiAssetService.TryGetPrefab`) loads it from embedded resources
synchronously, once, with `HideFlags.DontUnloadUnusedAsset` on the bundle and every prefab.

References added and stripped this session: `UnityEngine.AssetBundleModule`,
`UnityEngine.UIModule`.

Full contract and failure table: [`01-ui-asset-bundle.md`](01-ui-asset-bundle.md).

## Do not undo these

**`CanvasGroup`, never `SetActive(false)`, for hiding main-menu chrome.** `Window.OnDisable`
calls `WindowManager.WindowClosed()`; at zero open windows `RefreshCursor()` hides the mouse
cursor. `mainMenu.tabMenu` holds the active Window, so deactivating it leaves the panel drawn
over a game that has switched to its cursorless state.

**Fonts stay out of the bundle.** Re-point `TextMeshProUGUI.font` at the game's own font after
instantiating. Editor previews will not predict runtime glyph widths.

## Tasks

1. Author `LobbyPanel.prefab` in `unity-ui/` — scaffold, then style. Use layout groups for the
   member list and the button column; every hand-computed constant in the current panel has been
   wrong at least once.
2. Build the bundle, commit it.
3. Rewrite `LobbyPanel` to instantiate and bind by path instead of cloning `mainMenu.btnPlay`.
   Delete the code-built path in the same commit — do not leave both.
4. Apply the Window sequence above.
5. Re-check the things the code-built panel never got to: the READY column, a member list beyond
   two rows, and whether Escape now closes the panel via `Window.Close()`.

## Standing constraints

- Two players is the testing maximum. Do not design anything needing more.
- Nothing is verified until run in-game. There is no test suite; "builds clean" means very
  little. Five changes this session compiled and failed on first load.
- One logical change per commit, with what is unverified stated in the body.
- MemoryPack union tags are append-only. None of this work should need a new one.


## RESOLVED: this project's bundles did not load anywhere

**Cause: `Packages/manifest.json` was missing `com.unity.modules.assetbundle`.** The
hand-written manifest listed only the modules the editor scripts needed to compile.
`BuildPipeline.BuildAssetBundles` still ran, still reported success, and still wrote a file with a
correct-looking `UnityFS` header — the file simply was not loadable. No error at any point.

Found by building the same empty prefab in a project created by Unity itself
(`-createProject`), which produced a bundle that loaded. Diffing the two manifests showed the
missing module. `unity-ui/Packages/manifest.json` now carries Unity's full default module set
plus `com.unity.ugui` and `com.unity.textmeshpro`.

**Do not trim that manifest.** Nothing in the build reports a missing module, and the failure
surfaces four layers away as a runtime load error blaming the Unity version.

Everything below is the record of narrowing it down, kept because the symptom is so misleading.

**The editor that builds our bundles cannot load them back.** `MegabonkTogether/Verify UI Bundle`
runs `AssetBundle.LoadFromFile` on the built bundle inside the editor and fails with:

> could not be loaded because it is not compatible with this newer version of the Unity runtime

The game rejects it with the identical message, so this is one fault, not two.

### What has been ruled out

| Hypothesis | Test | Result |
|---|---|---|
| Wrong Unity version | Editor and game are both build `2023.2.22.7018943`; bundle header revision is `2023.2.22f1` | Identical — **a newer Unity is not the fix** |
| Bundle container malformed | Parsed both headers: format 8, `5.x.x`, flags `0x243`, declared size = file length | Identical to a bundle that loads |
| Compression | Rebuilt uncompressed | Fails identically |
| Wrong build target | Rebuilt with `-buildTarget Win64` | Byte-for-byte identical output |
| Headless rendering | Rebuilt without `-nographics` | Byte-for-byte identical output |
| Stale import artifacts | Deleted `Library/`, full reimport | Byte-for-byte identical output |
| Prefab contents | `MegabonkTogether/Bisect Bundle` builds an **empty** prefab, an Image-only prefab and a TMP-only prefab | All three fail, including the empty one at 1094 bytes |
| The verify tool giving false negatives | Pointed it at a known-good third-party bundle via `MT_VERIFY_BUNDLE` | Loads fine, 21 assets — **the tool is sound** |

So: this editor loads someone else's 2023.2.22f1 bundle, and refuses every bundle it builds
itself, down to an empty prefab. The fault is in how this project builds bundles, not in the
prefab, the load code, or the runtime.

### Where to look next

Untested, roughly in order of promise:

1. Build the same empty prefab from a project created by Unity itself (`-createProject`) rather
   than this hand-assembled one. If that bundle loads, the difference is in `ProjectSettings`.
2. Compare the inner `SerializedFile` version of an uncompressed bundle of ours against a
   known-good one. The container headers match; the payload version has not been checked.
3. Try the Scriptable Build Pipeline / Addressables instead of `BuildPipeline.BuildAssetBundles`.

### Also learned

Asset names inside a bundle are **lowercased** — a known-good bundle lists
`assets/prefabs/customuiroot.prefab`. `LoadAsset` is case-insensitive so the current call should
survive, but do not rely on the authoring capitalisation when listing or matching names.

Bundles can and do ship fonts and textures; the known-good one carries a TMP font asset, its
`.ttf`, and 18 textures. Our "no fonts in the bundle" rule is a size choice, not a constraint.

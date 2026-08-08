# The UI AssetBundle

Mod UI was built in code: clone a game button, hand-place a `RectTransform`, guess at sizes.
Every dimension was unverifiable until it was on screen, and each correction cost a full
build-and-launch cycle. The panel's first three iterations each shipped a layout defect that an
editor preview would have shown in a second.

This replaces that with prefabs authored in Unity, shipped inside the plugin DLL.

**Status: pipeline only.** `UiAssetService` loads the bundle; nothing consumes it yet.
`LobbyPanel` is still the code-built version. The swap is a separate change, deliberately, so the
mod keeps a working panel while the bundle is being authored.

## The version trap

| | |
|---|---|
| Game's Unity version | **2023.2.22f1** |
| Editor you must author in | **2023.2.22f1** |

A bundle built by any other Unity major returns **null** from `AssetBundle.LoadFromMemory` — no
exception, no reason, just null. `UiAssetService` calls this out in its error text because it is
by far the most likely cause. Verify with:

```bash
head -c 200 "$MegabonkPath/Megabonk_Data/globalgamemanagers" | strings | head -3
```

## Workflow

1. Open `unity-ui/` in Unity Hub with 2023.2.22f1.
2. **MegabonkTogether → Scaffold Lobby Panel Prefab** — writes `Assets/Prefabs/LobbyPanel.prefab`
   with the exact hierarchy the runtime binds to. One time only; it prompts before overwriting.
3. Style it. Move, resize, recolour, add decoration freely.
4. **MegabonkTogether → Build UI Bundle** (`Ctrl+Shift+B`) — writes
   `src/plugin/Resources/megabonktogether.ui` directly. No copy step.
5. `dotnet build MegabonkTogether.sln -c Debug`.

The built bundle **is committed**. It is our own asset, it is small, and CI cannot build the
plugin without it.

## The prefab contract

The runtime resolves children **by path**, so these names are API. Renaming or reparenting one is
a silent null at runtime, not a build error — which is why the scaffold generator exists rather
than a written-out list to reproduce by hand.

```
LobbyPanel                 RectTransform, CanvasGroup   (root, full-screen)
├── Blocker                Image                        full-screen scrim
└── Panel                  Image                        the card itself
    ├── Title              TextMeshProUGUI
    ├── Subtitle           TextMeshProUGUI              lobby code / status line
    ├── Members            VerticalLayoutGroup
    │   └── MemberRow      Image                        template, starts inactive
    │       ├── Name       TextMeshProUGUI
    │       └── Ready      TextMeshProUGUI
    └── Buttons            VerticalLayoutGroup
        ├── Ready          Image + Button
        │   └── Label      TextMeshProUGUI
        ├── Start          (same shape)
        ├── CopyCode       (same shape)
        ├── JoinCode       (same shape)
        └── Leave          (same shape)
```

Free to change without touching code: every position, size, colour, sprite, font size, anchor,
and any purely decorative object you add.

`Members` and `Buttons` are layout groups on purpose. The code-built panel hand-computed row
positions from a button-height constant, and got it wrong twice — once by assuming 48px buttons
where the game uses 70px, and once by spacing rows tighter than their own height. A layout group
cannot make either mistake.

## Fonts are not in the bundle

The prefab's TMP components carry whatever font the editor project has. At instantiation the
runtime re-points every `TextMeshProUGUI.font` at the game's own font asset, taken from a live
menu label.

Two reasons. A TMP font asset is the single largest thing that could go in a bundle — atlas
textures dwarf everything else. And a font we ship would not be the font the game uses, so the
panel would read as foreign no matter how well the layout matched.

Consequence: **do not** rely on how text looks in the editor preview. Glyph widths change at
runtime. Size text boxes with margin.

## Loading

Synchronous, once, on first `TryGetPrefab`. The bundle is prefabs with no textures, so
`LoadFromMemory` costs a few milliseconds at menu time.

The asynchronous alternative needs an `AsyncOperation` completion delegate held alive across the
native boundary for the duration — a managed field whose only job is to stop the GC collecting a
callback the native side still holds. That is a real class of lifetime bug, and it buys nothing
against a load this size.

Both the bundle and every prefab get `HideFlags.DontUnloadUnusedAsset`. Without it,
`Resources.UnloadUnusedAssets` on a scene transition may collect them out from under live
instances.

## Failure modes

| Symptom | Cause |
|---|---|
| `Embedded resource ... is missing` | Built without running Build UI Bundle. The csproj `Exists()` condition skips the resource silently. |
| `LoadFromMemory returned null` | Unity version mismatch. See above. |
| `'<path>' is not in the bundle` | Asset paths are the authoring path (`Assets/Prefabs/X.prefab`) and case-sensitive. |
| Prefab instantiates, a child is null | Renamed or reparented against the contract. |

None of this is verified in-game — nothing in this repo is until it has been run. The bundle has
not yet been built, so the load path above has never executed.

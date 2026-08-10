# The UI AssetBundle

Mod UI was built in code: clone a game button, hand-place a `RectTransform`, guess at sizes.
Every dimension was unverifiable until it was on screen, and each correction cost a full
build-and-launch cycle. The panel's first three iterations each shipped a layout defect that an
editor preview would have shown in a second.

This replaces that with prefabs authored in Unity, shipped inside the plugin DLL.

**Status:** `LobbyPanel` is prefab-driven. The bundle is built, committed and embedded, and the
prefab is styled.

## The version trap

| | |
|---|---|
| Game's Unity version | **2023.2.22f1** |
| Editor you must author in | **2023.2.22f1** |

A bundle built by any other Unity major returns **null** from the load call — no exception, no
reason, just null. `UiAssetService` calls this out in its error text because it is
by far the most likely cause. Verify with:

```bash
head -c 200 "$MegabonkPath/Megabonk_Data/globalgamemanagers" | strings | head -3
```

## Workflow

The generator is the prefab's source of truth. Edit
`unity-ui/Assets/Editor/ScaffoldLobbyPanel.cs`, regenerate, rebuild the bundle:

```powershell
$u = "C:\Program Files\Unity\Hub\Editor\2023.2.22f1\Editor\Unity.exe"
foreach ($m in "ScaffoldLobbyPanel.Scaffold", "BuildUiBundle.Build", "VerifyUiBundle.Verify") {
    Start-Process $u -Wait -PassThru -ArgumentList `
        "-batchmode","-quit","-nographics","-projectPath","unity-ui",`
        "-executeMethod","MegabonkTogether.UiAuthoring.$m","-logFile","$env:TEMP\mt-$m.log"
}
```

Then `dotnet build MegabonkTogether.sln -c Debug` to embed it.

Editing the prefab by hand in the editor also works — open `unity-ui/` in Unity Hub with
2023.2.22f1, style it, `Ctrl+Shift+B` — but **fold the result back into the generator**, because
the next regeneration discards it and nothing warns you. That is why the styling lives in the
generator at all: this panel has only ever been driven headlessly, and hand-authored colours
would have been silently lost the first time somebody ran the scaffold.

The built bundle **is committed**. It is our own asset, it is small, and CI cannot build the
plugin without it.

## The prefab contract

The runtime resolves children **by path**, so these names are API. Renaming or reparenting one is
a silent null at runtime, not a build error — which is why the scaffold generator exists rather
than a written-out list to reproduce by hand.

```
LobbyPanel                 RectTransform, CanvasGroup   (root, full-screen)
├── Blocker                Image                        full-screen scrim
└── Panel                  Image                        the card's border
    ├── Fill               Image                        decoration: interior, inset 3px
    ├── Title              TextMeshProUGUI
    ├── Subtitle           TextMeshProUGUI              lobby code
    ├── HeaderRule         Image                        decoration
    ├── Members            Image + VerticalLayoutGroup  the list well
    │   └── MemberRow      Image                        template, starts inactive
    │       ├── Name       TextMeshProUGUI
    │       └── Ready      TextMeshProUGUI
    ├── Status             TextMeshProUGUI              transient messages
    ├── FooterRule         Image                        decoration
    └── Buttons            VerticalLayoutGroup          children replaced at runtime
        ├── Ready          Image + Button               placeholder
        │   └── Label      TextMeshProUGUI
        ├── Start          (same shape)                 placeholder
        ├── CopyCode       (same shape)                 placeholder
        ├── JoinCode       (same shape)                 placeholder
        └── Leave          (same shape)                 placeholder
```

`Fill`, `HeaderRule` and `FooterRule` are decoration — the runtime never looks for them. `Panel`
is the border and `Fill` the interior because a child cannot draw behind its parent, so a framed
card needs the frame on the outside; the reward is a two-tone border that follows the card if it
is resized, with no sprite and no texture in the bundle.

`Status` is separate from `Subtitle` because `Subtitle` is rewritten on every refresh tick, so a
message shown there would be erased within half a second. It sits below the member list and
directly above the buttons, since every message it carries is the result of pressing one.

**The buttons under `Buttons` are placeholders and get destroyed at runtime.** They exist so the
column's shape is visible in the editor. Megabonk's `Window` registry collects `MyButton`
components and cannot see a plain uGUI Button, so the runtime clears the container and fills it
with clones of the game's own button. Style the container and its layout group; styling the
placeholder buttons themselves changes nothing in game.

Free to change without touching code: every position, size, colour, sprite, font size, anchor,
and any purely decorative object you add.

**Two colours in the prefab are overwritten at runtime and are editor previews only:** the member
row's `Name` (tinted for the local player) and `Ready` (green) labels, both recoloured in
`LobbyPanel.CreateMemberRow`.

`Members` and `Buttons` are layout groups on purpose. The code-built panel hand-computed row
positions from a button-height constant, and got it wrong twice — once by assuming 48px buttons
where the game uses 70px, and once by spacing rows tighter than their own height. A layout group
cannot make either mistake.

**`Buttons` must leave width uncontrolled** (`childControlWidth` and `childForceExpandWidth` both
false). A Megabonk button sizes itself from its label — `ButtonTextWrapper.Refresh` writes
`rect.sizeDelta` from the text's size plus `paddingX`/`paddingY` — so a layout group that also
drove width would fight the game for it on every label change. Buttons of varying width, centred,
is the game's own look. `Members` is the opposite case and drives width, so rows fill the well
whatever it is set to.

The button column reserves **410px**, which is measured rather than chosen: four real cloned
buttons plus spacing just fill it, and four is the true worst case because Copy Code and Join
From Clipboard are mutually exclusive. Nothing clips a fifth — it would simply draw past the
bottom of the card.

The members well reserves six rows permanently, even with two filled. The alternative is a button
column that moves under the cursor as people join, and empty rows inside a framed well read as
free slots rather than as the void the unstyled panel showed there.

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

Synchronous, once, on first `TryGetPrefab`, via `LoadFromFile` — see "Why the bundle is staged
on disk". The bundle is prefabs with no textures, so it costs a few milliseconds at menu time.

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
| `LoadFromStream returned null`, or "not compatible with this newer version of the Unity runtime" | Almost certainly **not** the Unity version. First check `Packages/manifest.json` still lists `com.unity.modules.assetbundle` — without it the build silently produces an unloadable file. Confirm with `MegabonkTogether/Verify UI Bundle`. |
| `No GameObject named '<x>' in the bundle` | The prefab file name does not match. Matching is on the file name, not the full path. |
| Prefab instantiates, a child is null | Renamed or reparented against the contract. |
| `MissingMethodException: LoadFromMemory(Byte[])` | The reference resolved to `unity-libs` instead of `interop`. See below. |
| `ObjectCollectedException` inside `LoadFromMemory_Internal` | Do not use `LoadFromMemory`. See below. |
| `MissingMethodException: ReadOnlySpan\`1.GetPinnableReference` | A string was passed to an AssetBundle API. See below. |

## Loading an asset: use LoadAssetAsync, then rewrap the result

**Synchronous loads do not work.** `LoadAsset` and `LoadAllAssets` marshal their name through
`Il2CppSystem.ReadOnlySpan<char>.GetPinnableReference`, which Il2CppInterop cannot bind here.
`LoadAssetAsync` does work — verified in-game — even though every variant has the same
`_Injected(IntPtr, ManagedSpanWrapper&, Type)` native form. The difference is in the managed
wrapper Il2CppInterop generated, not the native entry point, so **signatures cannot tell you
which of these is usable**; only running it can.

The async result then arrives typed as the base `UnityEngine.Object`, and `as GameObject` yields
null. `UiAssetService.RewrapAsGameObject` rebuilds the wrapper reflectively via
`Il2CppObjectBase.Pointer` and `GameObject(IntPtr)`. Both exist at runtime and neither is visible
at compile time, because `UnityEngine.CoreModule` is referenced from `unity-libs`.

Two dead ends, both tested rather than argued: BepInEx **be.755** behaves identically to
**be.785**, and deleting `BepInEx/interop` to force proxy regeneration changed nothing.

### Historic: the earlier blocker



The bundle loads. Getting a prefab out of it does not, and there is currently no way around it
by choosing a different API.

Every asset-loading entry point funnels into an `_Internal` method that takes a `String`, and all
of them die the same way:

```
MissingMethodException: '!0 ByRef Il2CppSystem.ReadOnlySpan`1.GetPinnableReference()'
   at AssetBundle.LoadAsset_Internal(String name, Type type)
   at AssetBundle.LoadAssetWithSubAssets_Internal(String name, Type type)   // LoadAllAssets<T>()
```

`LoadAllAssets<T>()` looks argument-free and is not — it passes a string internally.

**This is not the game stripping the method.** `ReadOnlySpan<char>.GetPinnableReference` is
present in the local dump (`build-21750826/dump.cs`). Il2CppInterop is failing to bind it.

**Most likely cause: the BepInEx version.** The interop assemblies are generated per BepInEx
build, and the third-party mod that demonstrably loads bundle assets by string in this same game
declares `BepInEx-BepInExPack_IL2CPP-6.0.755`. This install runs **be.785**.

Untested. Trying be.755 is the next step, and it is an environment change rather than a code one.
The plugin's own `PackageReference` is `6.0.0-be.*`, so it compiles against either.

## No AssetBundle API that takes a string can be used

`Il2CppSystem.ReadOnlySpan<T>.GetPinnableReference` does not exist in the interop assemblies, and
Unity's string-taking AssetBundle entry points all marshal through it. Both of these die with
`MissingMethodException` raised inside the `_Internal` method, not at the call site:

```
AssetBundle.LoadFromFile(path)
AssetBundle.LoadAsset<T>(name)      // and LoadAsset(name, type)
```

Use the argument-free forms instead. `UiAssetService` loads with `LoadFromStream` and resolves
prefabs with `LoadAllAssets<GameObject>()`, matching on `name` — nothing crosses the boundary as
a string.

This is a property of the interop assemblies for this BepInEx build, not of the bundle. Reading
strings back out (`Object.name`) is fine; passing them in is not.

## Only one of the three load entry points works

Use **`AssetBundle.LoadFromStream`** over an `Il2CppSystem.IO.MemoryStream`, with both the stream
and the `Il2CppStructArray<byte>` held in fields. All three were tried in-game; the other two
fail, each for its own reason.

| Entry point | Result |
|---|---|
| `LoadFromMemory(Il2CppStructArray<byte>)` | `ObjectCollectedException` — the array is collected in the IL2CPP domain *during* the call. A managed field does not help: it roots the wrapper, not the IL2CPP object behind it. |
| `LoadFromFile(string)` | `MissingMethodException` — marshals its path through `Il2CppSystem.ReadOnlySpan<T>.GetPinnableReference`, which the interop assemblies do not define. |
| `LoadFromStream(Stream)` | Works. |

A `MemoryStream` is itself an IL2CPP object holding an IL2CPP-side reference to the array, which
is what actually keeps it alive. **Keep both fields.** Dropping either reintroduces the
collection.

The failure is not that the array is unreachable from managed code — it is that IL2CPP's GC
cannot see managed references at all. Only another IL2CPP object counts.

## interop vs unity-libs

`UnityEngine.AssetBundleModule` and `UnityEngine.UIModule` are referenced from **`$(AssemblyPath)`
(interop)**, not `$(UnityLibPath)`. This is not interchangeable.

`unity-libs` holds stock Unity assemblies, where array-taking APIs are `byte[]` and `T[]`. The
Il2CppInterop proxies that actually load at runtime take `Il2CppStructArray<T>`. Compiling
`AssetBundle.LoadFromMemory` against `unity-libs` binds to an overload the game does not have, and
the failure arrives as `MissingMethodException` at the first call — nothing catches it earlier,
because both signatures are perfectly valid at compile time.

The same trap applies to `GetComponentsInChildren<T>`, which is why `Helpers/Helper.cs` carries
`RuntimeGetComponentsInChildren<T>`. Use the wrapper rather than the direct call.

**Rule of thumb: any module whose API passes arrays must be referenced from `interop`.**

None of this is verified in-game — nothing in this repo is until it has been run. The bundle has
not yet been built, so the load path above has never executed.

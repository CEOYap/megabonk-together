using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OmniBonk.Services
{
    public interface IUiAssetService
    {
        bool IsAvailable { get; }

        /// <summary>By authoring path, e.g. "Assets/Prefabs/LobbyPanel.prefab". Main thread only.</summary>
        bool TryGetPrefab(string assetPath, out GameObject prefab);

        /// <summary>
        /// Calls <paramref name="onLoaded"/> exactly once on the main thread, with null on failure.
        /// Exists because the sync string entry points cannot marshal their name under IL2CPP.
        /// </summary>
        void RequestPrefab(string assetPath, Action<GameObject> onLoaded);
    }

    /// <summary>
    /// Loads the embedded UI AssetBundle. Prefab contract: docs/ui/01-ui-asset-bundle.md.
    /// </summary>
    internal class UiAssetService : IUiAssetService
    {
        /// <summary>Must match the csproj's LogicalName.</summary>
        private const string BundleResourceName = "OmniBonk.Resources.megabonktogether.ui";

        /// <summary>Otherwise the game's UnloadUnusedAssets on scene change collects our prefabs.</summary>
        private const HideFlags KeepLoaded = HideFlags.DontUnloadUnusedAsset;

        private readonly Dictionary<string, GameObject> prefabCache = [];

        private AssetBundle bundle;
        private bool loadAttempted;

        /// <summary>
        /// Held for the process lifetime: the IL2CPP stream roots the array; dropping either lets
        /// it be collected. See <see cref="EnsureBundleLoaded"/>.
        /// </summary>
        private Il2CppStructArray<byte> bundleBytes;

        private Il2CppSystem.IO.MemoryStream bundleStream;


        public bool IsAvailable => bundle != null;

        public bool TryGetPrefab(string assetPath, out GameObject prefab)
        {
            prefab = null;

            if (prefabCache.TryGetValue(assetPath, out var cached) && cached != null)
            {
                prefab = cached;
                return true;
            }

            if (!EnsureBundleLoaded())
            {
                return false;
            }

            // Name match over LoadAllAssets: string-taking entry points throw MissingMethodException
            // (ReadOnlySpan.GetPinnableReference is not in the interop assemblies).
            var wanted = Path.GetFileNameWithoutExtension(assetPath);

            try
            {
                // Generic overload: no cast needed (Object has no TryCast here).
                foreach (var candidate in bundle.LoadAllAssets<GameObject>())
                {
                    if (candidate != null && candidate.name == wanted)
                    {
                        prefab = candidate;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[UiAssets] LoadAllAssets for '{assetPath}' threw: {ex}");
                return false;
            }

            if (prefab == null)
            {
                Plugin.Log.LogError(
                    $"[UiAssets] No GameObject named '{wanted}' in the bundle. Check the prefab name "
                    + "in unity-ui/Assets/Prefabs; matching is on the file name, not the full path.");
                return false;
            }

            prefab.hideFlags = KeepLoaded;
            prefabCache[assetPath] = prefab;
            return true;
        }

        public void RequestPrefab(string assetPath, Action<GameObject> onLoaded)
        {
            if (onLoaded == null)
            {
                return;
            }

            if (prefabCache.TryGetValue(assetPath, out var cached) && cached != null)
            {
                onLoaded(cached);
                return;
            }

            if (!EnsureBundleLoaded())
            {
                onLoaded(null);
                return;
            }

            AssetBundleRequest request;
            try
            {
                request = bundle.LoadAssetAsync<GameObject>(assetPath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[UiAssets] LoadAssetAsync('{assetPath}') threw: {ex}");
                onLoaded(null);
                return;
            }

            if (request == null)
            {
                Plugin.Log.LogError($"[UiAssets] LoadAssetAsync('{assetPath}') returned null.");
                onLoaded(null);
                return;
            }

            Helpers.CoroutineRunner.Instance.Run(AwaitRequest(assetPath, request, onLoaded));
        }

        /// <summary>Polls <c>isDone</c>: <c>m_completeCallback</c> is not reachable from unity-libs.</summary>
        private IEnumerator AwaitRequest(string assetPath, AssetBundleRequest request, Action<GameObject> onLoaded)
        {
            while (!request.isDone)
            {
                yield return null;
            }

            GameObject prefab = null;
            try
            {
                prefab = ExtractPrefab(request);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[UiAssets] Reading the result of '{assetPath}' threw: {ex}");
            }

            if (prefab == null)
            {
                Plugin.Log.LogError($"[UiAssets] '{assetPath}' resolved to nothing.");
                onLoaded(null);
                yield break;
            }

            prefab.hideFlags = KeepLoaded;
            prefabCache[assetPath] = prefab;
            onLoaded(prefab);
        }

        /// <summary>
        /// <c>as</c>, not <c>TryCast</c> (unavailable here); a base-typed wrapper is rewrapped.
        /// </summary>
        private static GameObject ExtractPrefab(AssetBundleRequest request)
        {
            var asset = request.asset;
            if (asset == null)
            {
                Plugin.Log.LogError("[UiAssets] The request completed but its asset is null.");
                return null;
            }

            var direct = asset as GameObject;
            if (direct != null)
            {
                return direct;
            }

            return RewrapAsGameObject(asset);
        }

        /// <summary>
        /// Rewraps a base-typed asset as GameObject via reflection: at runtime the interop
        /// assembly provides <c>Pointer</c> and the <c>IntPtr</c> constructor.
        /// </summary>
        private static GameObject RewrapAsGameObject(UnityEngine.Object asset)
        {
            var pointerProperty = asset.GetType().GetProperty(
                "Pointer", BindingFlags.Instance | BindingFlags.Public);

            if (pointerProperty?.GetValue(asset) is not IntPtr pointer || pointer == IntPtr.Zero)
            {
                Plugin.Log.LogError(
                    $"[UiAssets] Asset arrived as '{asset.GetType().FullName}' with no usable "
                    + "Il2CppObjectBase.Pointer, so it cannot be rewrapped as a GameObject.");
                return null;
            }

            var constructor = typeof(GameObject).GetConstructor([typeof(IntPtr)]);
            if (constructor == null)
            {
                Plugin.Log.LogError(
                    "[UiAssets] GameObject has no IntPtr constructor at runtime. That means "
                    + "UnityEngine.CoreModule resolved to the stock assembly rather than interop.");
                return null;
            }

            var prefab = constructor.Invoke([pointer]) as GameObject;
            if (prefab == null)
            {
                Plugin.Log.LogError("[UiAssets] Rewrapping the asset as a GameObject produced null.");
            }

            return prefab;
        }

        private bool EnsureBundleLoaded()
        {
            if (bundle != null)
            {
                return true;
            }

            // One attempt per process: a missing bundle is a packaging fault.
            if (loadAttempted)
            {
                return false;
            }

            loadAttempted = true;

            byte[] bytes;
            try
            {
                bytes = ReadEmbeddedBundle();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[UiAssets] Could not read the embedded bundle: {ex}");
                return false;
            }

            if (bytes == null)
            {
                Plugin.Log.LogError(
                    $"[UiAssets] Embedded resource '{BundleResourceName}' is missing. The plugin was "
                    + "built without a UI bundle - run OmniBonk/Build UI Bundle in unity-ui, "
                    + "then rebuild.");
                return false;
            }

            try
            {
                // LoadFromStream is the only entry point that works: LoadFromMemory's array is
                // collected IL2CPP-side mid-call, and LoadFromFile throws MissingMethodException.
                // The IL2CPP stream roots the array; the bundle reads from it lazily.
                bundleBytes = bytes;
                bundleStream = new Il2CppSystem.IO.MemoryStream(bundleBytes);

                bundle = AssetBundle.LoadFromStream(bundleStream);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[UiAssets] LoadFromStream threw: {ex}");
                return false;
            }

            if (bundle == null)
            {
                Plugin.Log.LogError(
                    $"[UiAssets] LoadFromStream returned null for {bytes.Length} bytes. This is "
                    + "almost always a Unity version mismatch - the bundle must be built with the "
                    + "editor version the game ships (see docs/ui/01-ui-asset-bundle.md).");
                return false;
            }

            bundle.hideFlags = KeepLoaded;
            Plugin.Log.LogInfo($"[UiAssets] Loaded UI bundle ({bytes.Length} bytes).");
            return true;
        }

        private static byte[] ReadEmbeddedBundle()
        {
            using var stream = Assembly
                .GetExecutingAssembly()
                .GetManifestResourceStream(BundleResourceName);

            if (stream == null)
            {
                return null;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }
}

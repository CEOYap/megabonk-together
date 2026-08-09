using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MegabonkTogether.Services
{
    public interface IUiAssetService
    {
        /// <summary>
        /// True once the bundle has been loaded successfully. False both before the first
        /// <see cref="TryGetPrefab"/> and permanently if the bundle is missing or unloadable.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Resolves a prefab from the UI bundle by its authoring path, e.g.
        /// "Assets/Prefabs/LobbyPanel.prefab". Loads the bundle on first call.
        /// Must be called on the Unity main thread.
        /// </summary>
        bool TryGetPrefab(string assetPath, out GameObject prefab);

        /// <summary>
        /// Asynchronous counterpart to <see cref="TryGetPrefab"/>. <paramref name="onLoaded"/> is
        /// invoked on the main thread with the prefab, or with null if it could not be resolved —
        /// always exactly once, so the caller never has to time out.
        ///
        /// <para>This exists because the synchronous path is unusable on this install:
        /// <c>LoadAsset</c> and <c>LoadAllAssets</c> both marshal their name through
        /// <c>Il2CppSystem.ReadOnlySpan&lt;char&gt;.GetPinnableReference</c>, which Il2CppInterop
        /// cannot bind. Whether the async wrapper avoids that is the open question this is here to
        /// answer.</para>
        /// </summary>
        void RequestPrefab(string assetPath, Action<GameObject> onLoaded);
    }

    /// <summary>
    /// Loads the mod's UI AssetBundle out of the plugin assembly's embedded resources.
    /// The bundle is authored in unity-ui/ and built by MegabonkTogether/Build UI Bundle;
    /// see docs/ui/01-ui-asset-bundle.md for the prefab contract.
    /// </summary>
    internal class UiAssetService : IUiAssetService
    {
        /// <summary>
        /// Must match the LogicalName the csproj gives the embedded bundle.
        /// </summary>
        private const string BundleResourceName = "MegabonkTogether.Resources.megabonktogether.ui";

        /// <summary>
        /// HideFlags.DontUnloadUnusedAsset. Without it Resources.UnloadUnusedAssets — which the
        /// game runs on scene transitions — is free to collect the bundle and every prefab in it
        /// while our still-live instances reference them.
        /// </summary>
        private const HideFlags KeepLoaded = HideFlags.DontUnloadUnusedAsset;

        private readonly Dictionary<string, GameObject> prefabCache = [];

        private AssetBundle bundle;
        private bool loadAttempted;

        /// <summary>
        /// The bundle bytes and the IL2CPP stream over them, both held for the process lifetime.
        ///
        /// <para>Not tidiness — lifetime. See the comment in <see cref="EnsureBundleLoaded"/>: the
        /// stream is what roots the array inside the IL2CPP domain, and dropping either reference
        /// reintroduces the collection that made <c>LoadFromMemory</c> unusable.</para>
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

            // Matched by name against everything in the bundle, rather than LoadAsset(path).
            //
            // Every AssetBundle entry point that takes a string marshals it through
            // Il2CppSystem.ReadOnlySpan<T>.GetPinnableReference, which the interop assemblies do
            // not define — LoadFromFile and LoadAsset both die on it with MissingMethodException.
            // LoadAllAssets takes no arguments, so nothing crosses the boundary as a string.
            var wanted = Path.GetFileNameWithoutExtension(assetPath);

            try
            {
                // The generic overload returns GameObjects directly, so there is no cast to get
                // wrong — UnityEngine.Object compiles from unity-libs here and has no TryCast.
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

        /// <summary>
        /// Polls the request rather than assigning <c>AsyncOperation.m_completeCallback</c>.
        ///
        /// <para>That field is how the design being followed does it, and it is not reachable
        /// here: <c>AsyncOperation</c> compiles from <c>unity-libs</c>, which exposes only Unity's
        /// public surface. Polling <c>isDone</c> needs nothing but that public surface and cannot
        /// leak a native callback.</para>
        /// </summary>
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
        /// Pulls the GameObject out of a finished request.
        ///
        /// <para><c>as</c> rather than <c>TryCast</c>, because <c>UnityEngine.Object</c> compiles
        /// from <c>unity-libs</c> here and has no <c>TryCast</c>. That is a real hazard: if
        /// Il2CppInterop hands back a base <c>Object</c> wrapper rather than a <c>GameObject</c>
        /// one, the cast yields null and looks identical to a failed load. The mismatch is logged
        /// explicitly so the next run can tell those two apart.</para>
        /// </summary>
        private static GameObject ExtractPrefab(AssetBundleRequest request)
        {
            var asset = request.asset;
            if (asset == null)
            {
                Plugin.Log.LogError("[UiAssets] The request completed but its asset is null.");
                return null;
            }

            var prefab = asset as GameObject;
            if (prefab == null)
            {
                Plugin.Log.LogError(
                    $"[UiAssets] Asset loaded but is not a GameObject to this build — runtime type "
                    + $"'{asset.GetType().FullName}'. The load worked; the cast did not.");
            }

            return prefab;
        }

        private bool EnsureBundleLoaded()
        {
            if (bundle != null)
            {
                return true;
            }

            // One attempt per process. A missing bundle is a packaging fault, not a transient
            // one, and retrying it on every panel open would just repeat the same error log.
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
                    + "built without a UI bundle — run MegabonkTogether/Build UI Bundle in unity-ui, "
                    + "then rebuild.");
                return false;
            }

            try
            {
                // LoadFromStream, and both objects held in fields. This is the only one of the
                // three entry points that works here, and each of the other two failed for its
                // own reason:
                //
                //   LoadFromMemory(Il2CppStructArray<byte>) — the array is collected in the
                //     IL2CPP domain during the call. A managed field does not prevent it: that
                //     roots the wrapper, not the IL2CPP object behind it.
                //   LoadFromFile(string)                    — marshals its path through
                //     Il2CppSystem.ReadOnlySpan<T>.GetPinnableReference, which the interop
                //     assemblies do not define. MissingMethodException inside
                //     LoadFromFile_Internal.
                //
                // A MemoryStream is an IL2CPP object that holds the array as an IL2CPP-side
                // reference, which is what actually keeps it alive across the call. The stream is
                // kept in a field for the same reason, and because the bundle reads from it
                // lazily.
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
                    + "almost always a Unity version mismatch — the bundle must be built with the "
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

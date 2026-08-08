using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

            try
            {
                prefab = bundle.LoadAsset<GameObject>(assetPath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[UiAssets] LoadAsset('{assetPath}') threw: {ex}");
                return false;
            }

            if (prefab == null)
            {
                Plugin.Log.LogError(
                    $"[UiAssets] '{assetPath}' is not in the bundle. Asset paths are the authoring "
                    + "paths and are case-sensitive — check the name in unity-ui/Assets/Prefabs.");
                return false;
            }

            prefab.hideFlags = KeepLoaded;
            prefabCache[assetPath] = prefab;
            return true;
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

            string bundlePath;
            try
            {
                bundlePath = StageBundleOnDisk(bytes);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[UiAssets] Could not stage the bundle on disk: {ex}");
                return false;
            }

            try
            {
                // LoadFromFile, not LoadFromMemory. The in-memory path takes an
                // Il2CppStructArray<byte>, and the array was collected in the IL2CPP domain
                // *during* the call — ObjectCollectedException raised inside
                // LoadFromMemory_Internal. Holding the wrapper in a managed field did not help,
                // because that roots the wrapper rather than the object it points at.
                //
                // LoadFromFile takes a string, so no IL2CPP-domain object has to survive the call
                // at all. It also memory-maps rather than holding a second copy of the bytes.
                bundle = AssetBundle.LoadFromFile(bundlePath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[UiAssets] LoadFromFile threw: {ex}");
                return false;
            }

            if (bundle == null)
            {
                Plugin.Log.LogError(
                    $"[UiAssets] LoadFromFile returned null for {bundlePath} ({bytes.Length} bytes). "
                    + "This is almost always a Unity version mismatch — the bundle must be built "
                    + "with the editor version the game ships (see docs/ui/01-ui-asset-bundle.md).");
                return false;
            }

            bundle.hideFlags = KeepLoaded;
            Plugin.Log.LogInfo($"[UiAssets] Loaded UI bundle ({bytes.Length} bytes).");
            return true;
        }

        /// <summary>
        /// Writes the embedded bundle into BepInEx's cache directory and returns its path.
        ///
        /// <para>Skipped when a file of the same length is already there. Unity keeps a loaded
        /// bundle's file open, so a second game instance on the same machine — which is how this
        /// mod gets tested — would otherwise fail to overwrite it. Length is a weak check, but it
        /// changes whenever the prefab does, and the cost of being wrong is a stale panel rather
        /// than a corrupt one.</para>
        /// </summary>
        private static string StageBundleOnDisk(byte[] bytes)
        {
            var directory = Path.Combine(BepInEx.Paths.CachePath, "MegabonkTogether");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "megabonktogether.ui");

            var existing = new FileInfo(path);
            if (existing.Exists && existing.Length == bytes.Length)
            {
                return path;
            }

            File.WriteAllBytes(path, bytes);
            return path;
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

using System.IO;
using UnityEditor;
using UnityEngine;

namespace MegabonkTogether.UiAuthoring
{
    /// <summary>
    /// Builds every prefab under Assets/Prefabs into a single AssetBundle and drops it straight
    /// into the plugin's Resources folder, where the csproj embeds it. Build here, then
    /// `dotnet build` there — no copy step in between.
    /// </summary>
    public static class BuildUiBundle
    {
        /// <summary>
        /// Must match UiAssetService.BundleResourceName's file part.
        /// </summary>
        public const string BundleName = "megabonktogether.ui";

        private const string PrefabFolder = "Assets/Prefabs";

        /// <summary>
        /// ChunkBasedCompression (LZ4) stays compressed in memory and decompresses per chunk on
        /// demand, rather than LZMA's full decompress on load.
        /// </summary>
        private static BuildAssetBundleOptions BuildOptions =>
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode;

        /// <summary>
        /// Relative to the Unity project root (the parent of Assets/).
        /// </summary>
        private const string PluginResourcesRelativePath = "../src/plugin/Resources";

        [MenuItem("MegabonkTogether/Build UI Bundle %#b")]
        public static void Build()
        {
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder });
            if (prefabGuids.Length == 0)
            {
                Debug.LogError($"[MT] No prefabs found under {PrefabFolder}. Nothing to build.");
                return;
            }

            var assetNames = new string[prefabGuids.Length];
            for (var i = 0; i < prefabGuids.Length; i++)
            {
                assetNames[i] = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
                Debug.Log($"[MT] Including {assetNames[i]}");
            }

            // Staging directory, because BuildAssetBundles also emits .manifest files and a
            // per-directory manifest bundle. A single self-contained bundle needs none of them:
            // manifests only exist to resolve dependencies *between* bundles.
            var staging = Path.Combine(Path.GetTempPath(), "mt-ui-bundle");
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);

            var build = new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = assetNames,
            };

            var manifest = BuildPipeline.BuildAssetBundles(
                staging,
                new[] { build },
                BuildOptions,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                Debug.LogError("[MT] BuildAssetBundles returned no manifest — the build failed.");
                return;
            }

            var built = Path.Combine(staging, BundleName);
            if (!File.Exists(built))
            {
                Debug.LogError($"[MT] Expected bundle at {built} but it does not exist.");
                return;
            }

            var destinationFolder = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", PluginResourcesRelativePath));
            Directory.CreateDirectory(destinationFolder);

            var destination = Path.Combine(destinationFolder, BundleName);
            File.Copy(built, destination, overwrite: true);

            var kb = new FileInfo(destination).Length / 1024f;
            Debug.Log($"[MT] Wrote {destination} ({kb:F1} KB). Rebuild the plugin to embed it.");
        }
    }
}

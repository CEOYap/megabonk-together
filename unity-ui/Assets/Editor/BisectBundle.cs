using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace MegabonkTogether.UiAuthoring
{
    /// <summary>
    /// Builds a bundle from a minimal prefab and loads it back, in one editor run.
    ///
    /// <para>Exists to split "the build pipeline or project is broken" from "something in the
    /// lobby prefab is". Temporary scaffolding for that question; delete once it is answered.</para>
    /// </summary>
    public static class BisectBundle
    {
        [MenuItem("MegabonkTogether/Bisect Bundle")]
        public static void Run()
        {
            Directory.CreateDirectory("Assets/Bisect");

            Case("Empty", root => { });
            Case("ImageOnly", root => root.AddComponent<Image>());
            Case("TmpOnly", root => root.AddComponent<TextMeshProUGUI>());
        }

        private static void Case(string name, System.Action<GameObject> build)
        {
            var path = $"Assets/Bisect/{name}.prefab";
            var go = new GameObject(name, typeof(RectTransform));
            build(go);
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            AssetDatabase.Refresh();

            var outDir = Path.Combine(Path.GetTempPath(), $"mt-bisect-{name}");
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, true);
            }
            Directory.CreateDirectory(outDir);

            var bundleName = $"bisect{name.ToLowerInvariant()}.bundle";
            var manifest = BuildPipeline.BuildAssetBundles(
                outDir,
                new[] { new AssetBundleBuild { assetBundleName = bundleName, assetNames = new[] { path } } },
                BuildAssetBundleOptions.ChunkBasedCompression,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                Debug.LogError($"[MT-BISECT] {name}: build returned no manifest");
                return;
            }

            var file = Path.Combine(outDir, bundleName);
            var bundle = AssetBundle.LoadFromFile(file);
            if (bundle == null)
            {
                Debug.LogError($"[MT-BISECT] {name}: FAILED to load back ({new FileInfo(file).Length} bytes)");
                return;
            }

            Debug.Log($"[MT-BISECT] {name}: OK, assets = [{string.Join(", ", bundle.GetAllAssetNames())}]");
            bundle.Unload(true);
        }
    }
}

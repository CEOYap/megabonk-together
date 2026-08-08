using System.IO;
using UnityEditor;
using UnityEngine;

namespace MegabonkTogether.UiAuthoring
{
    /// <summary>
    /// Loads the built bundle back and reports what is in it.
    ///
    /// <para>The editor is a Unity runtime, so this answers "is the bundle itself loadable" without
    /// launching the game — which matters because Unity reports a malformed bundle and a
    /// mis-delivered buffer with the same "not compatible with this newer version of the Unity
    /// runtime" message, and the two have opposite fixes.</para>
    /// </summary>
    public static class VerifyUiBundle
    {
        private const string PluginResourcesRelativePath = "../src/plugin/Resources";

        [MenuItem("MegabonkTogether/Verify UI Bundle")]
        public static void Verify()
        {
            // MT_VERIFY_BUNDLE lets this be pointed at a known-good bundle as a control. Without
            // one, a failure here cannot be told apart from "the editor refuses player-target
            // bundles in general", which would make this whole check a false negative.
            var path = System.Environment.GetEnvironmentVariable("MT_VERIFY_BUNDLE");
            if (string.IsNullOrEmpty(path))
            {
                path = Path.GetFullPath(Path.Combine(
                    Application.dataPath, "..", PluginResourcesRelativePath, BuildUiBundle.BundleName));
            }

            if (!File.Exists(path))
            {
                Debug.LogError($"[MT] No bundle at {path}. Run Build UI Bundle first.");
                return;
            }

            Debug.Log($"[MT] Verifying {path} ({new FileInfo(path).Length} bytes)");

            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                Debug.LogError(
                    "[MT] VERIFY FAILED: the editor could not load this bundle either. "
                    + "The bundle is bad, not the runtime delivering it.");
                return;
            }

            var names = bundle.GetAllAssetNames();
            Debug.Log($"[MT] VERIFY OK: bundle opened, {names.Length} asset(s).");
            foreach (var name in names)
            {
                Debug.Log($"[MT]   asset: {name}");
            }

            var prefab = bundle.LoadAsset<GameObject>("Assets/Prefabs/LobbyPanel.prefab");
            Debug.Log(prefab == null
                ? "[MT] VERIFY FAILED: LobbyPanel.prefab is not in the bundle under that path."
                : $"[MT] VERIFY OK: LobbyPanel.prefab loaded, {prefab.transform.childCount} root children.");

            bundle.Unload(true);
        }
    }
}

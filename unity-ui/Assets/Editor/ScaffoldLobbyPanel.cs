using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace MegabonkTogether.UiAuthoring
{
    /// <summary>
    /// Generates Assets/Prefabs/LobbyPanel.prefab with exactly the hierarchy the runtime binds
    /// to. Run this once, then style it in the editor — move, resize and recolour freely, but do
    /// not rename or reparent the objects listed in docs/ui/01-ui-asset-bundle.md, because the
    /// runtime resolves them by path. A renamed child is a silent null at runtime, not a build
    /// error, which is exactly why this scaffold exists rather than a "build it by hand" note.
    /// </summary>
    public static class ScaffoldLobbyPanel
    {
        private const string PrefabPath = "Assets/Prefabs/LobbyPanel.prefab";

        [MenuItem("MegabonkTogether/Scaffold Lobby Panel Prefab")]
        public static void Scaffold()
        {
            if (File.Exists(PrefabPath)
                && !EditorUtility.DisplayDialog(
                    "Overwrite prefab?",
                    $"{PrefabPath} already exists. Regenerating discards every edit made in the editor.",
                    "Overwrite",
                    "Cancel"))
            {
                return;
            }

            var root = new GameObject("LobbyPanel", typeof(RectTransform), typeof(CanvasGroup));
            var rootRect = root.GetComponent<RectTransform>();
            Stretch(rootRect);

            // Blocker: full-screen scrim that both dims the menu and eats clicks aimed at it.
            var blocker = CreateImage("Blocker", rootRect, new Color(0f, 0f, 0f, 0.85f));
            Stretch(blocker.rectTransform);

            var panel = CreateImage("Panel", rootRect, new Color(0.08f, 0.08f, 0.10f, 0.98f));
            Centre(panel.rectTransform, new Vector2(620f, 760f));

            CreateText("Title", panel.rectTransform, "LOBBY", 44f,
                new Vector2(0f, -46f), new Vector2(560f, 60f));
            CreateText("Subtitle", panel.rectTransform, "", 24f,
                new Vector2(0f, -100f), new Vector2(560f, 40f));

            // Members: a VerticalLayoutGroup fed at runtime by cloning MemberRow. Letting Unity
            // lay the rows out is the whole point — the code-built version hand-computed row Y
            // positions and got them wrong every time a font size changed.
            var members = new GameObject("Members", typeof(RectTransform), typeof(VerticalLayoutGroup));
            members.transform.SetParent(panel.transform, worldPositionStays: false);
            var membersRect = members.GetComponent<RectTransform>();
            Anchor(membersRect, new Vector2(0f, -150f), new Vector2(560f, 300f));
            var layout = members.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperCenter;

            // MemberRow is a template: the runtime clones it and leaves the original inactive.
            var row = CreateImage("MemberRow", membersRect, new Color(1f, 1f, 1f, 0.05f));
            Anchor(row.rectTransform, Vector2.zero, new Vector2(540f, 44f));
            var rowName = CreateText("Name", row.rectTransform, "Player", 26f, Vector2.zero, new Vector2(380f, 44f));
            rowName.alignment = TextAlignmentOptions.MidlineLeft;
            Anchor(rowName.rectTransform, new Vector2(-70f, 0f), new Vector2(380f, 44f));
            var rowReady = CreateText("Ready", row.rectTransform, "", 24f, Vector2.zero, new Vector2(140f, 44f));
            rowReady.alignment = TextAlignmentOptions.MidlineRight;
            Anchor(rowReady.rectTransform, new Vector2(190f, 0f), new Vector2(140f, 44f));
            row.gameObject.SetActive(false);

            var buttons = new GameObject("Buttons", typeof(RectTransform), typeof(VerticalLayoutGroup));
            buttons.transform.SetParent(panel.transform, worldPositionStays: false);
            var buttonsRect = buttons.GetComponent<RectTransform>();
            Anchor(buttonsRect, new Vector2(0f, -480f), new Vector2(560f, 250f));
            var buttonLayout = buttons.GetComponent<VerticalLayoutGroup>();
            buttonLayout.spacing = 8f;
            buttonLayout.childControlHeight = false;
            buttonLayout.childForceExpandHeight = false;
            buttonLayout.childAlignment = TextAnchor.UpperCenter;

            foreach (var name in new[] { "Ready", "Start", "CopyCode", "JoinCode", "Leave" })
            {
                CreateButton(name, buttonsRect);
            }

            Directory.CreateDirectory("Assets/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.Refresh();

            Debug.Log($"[MT] Wrote {PrefabPath}. Style it, then run MegabonkTogether/Build UI Bundle.");
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        }

        private static Image CreateImage(string name, RectTransform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);
            var image = go.GetComponent<Image>();
            image.color = colour;
            return image;
        }

        private static TextMeshProUGUI CreateText(
            string name, RectTransform parent, string text, float size, Vector2 position, Vector2 sizeDelta)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, worldPositionStays: false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = size;
            tmp.alignment = TextAlignmentOptions.Center;
            Anchor(tmp.rectTransform, position, sizeDelta);
            return tmp;
        }

        private static void CreateButton(string name, RectTransform parent)
        {
            var image = CreateImage(name, parent, new Color(1f, 1f, 1f, 0.12f));
            image.gameObject.AddComponent<Button>();
            Anchor(image.rectTransform, Vector2.zero, new Vector2(300f, 44f));
            var label = CreateText("Label", image.rectTransform, name, 26f, Vector2.zero, new Vector2(300f, 44f));
            Stretch(label.rectTransform);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void Centre(RectTransform rect, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }

        /// <summary>
        /// Top-centre anchoring, so a taller panel grows downward and every child keeps its
        /// offset from the title rather than drifting.
        /// </summary>
        private static void Anchor(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }
    }
}

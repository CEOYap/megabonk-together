using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace MegabonkTogether.UiAuthoring
{
    /// <summary>
    /// Generates Assets/Prefabs/LobbyPanel.prefab: the hierarchy the runtime binds to, styled.
    ///
    /// <para><b>This generator is the prefab's source of truth, not a one-time starting point.</b>
    /// It began as a scaffold to be styled by hand in the editor, and that never happened, because
    /// every session working on this panel drives the editor headlessly — there is no human in the
    /// Inspector. Colours and geometry authored by hand would be lost the next time somebody
    /// regenerated, and nothing would warn them. Keeping the styling here instead means the panel
    /// is diffable, reviewable and reproducible from a terminal like everything else in the repo.
    /// Editing the prefab in the editor still works; just fold the result back into this file, or
    /// the next run discards it.</para>
    ///
    /// <para>Names and paths are API — the runtime resolves children with
    /// <c>transform.Find("Panel/Members")</c> and friends, so a rename is a silent null at runtime
    /// rather than a build error. See docs/ui/01-ui-asset-bundle.md for the contract. Anything
    /// <i>not</i> in that contract (Fill, HeaderRule, FooterRule) is decoration the runtime never
    /// looks for.</para>
    /// </summary>
    public static class ScaffoldLobbyPanel
    {
        private const string PrefabPath = "Assets/Prefabs/LobbyPanel.prefab";

        #region Palette

        /// <summary>
        /// Warm near-black through parchment, taken to sit with Megabonk's own menus rather than to
        /// stand out from them. The game's UI is dark, warm-grey and gothic with gold accents; a
        /// neutral or cool panel reads as a mod overlay sitting on top of the game instead of a
        /// screen belonging to it.
        ///
        /// <para><b>Two of these are overwritten at runtime and are only editor previews:</b> the
        /// member row's Name (tinted for the local player) and Ready (green) labels are recoloured
        /// in <c>LobbyPanel.CreateMemberRow</c>. Changing them here changes nothing in game.</para>
        /// </summary>
        private static readonly Color Scrim = new Color(0f, 0f, 0f, 0.78f);
        private static readonly Color PanelBorder = Hex("#0A0908", 1f);
        private static readonly Color PanelFill = Hex("#1B1714", 0.98f);
        private static readonly Color Rule = Hex("#5A4A34", 0.85f);
        private static readonly Color TitleInk = Hex("#F2DFA6");
        private static readonly Color SubtitleInk = Hex("#A99C86");
        private static readonly Color StatusInk = Hex("#E8B94F");
        private static readonly Color MembersWell = Hex("#0E0C0A", 0.55f);
        private static readonly Color MemberRowFill = Hex("#2E2721", 0.60f);
        private static readonly Color PlaceholderButton = Hex("#3A322B", 0.55f);

        #endregion

        #region Geometry

        // The card. Top-anchored children measure downward from its top edge, so every offset below
        // reads in the order the panel is drawn.
        //
        // ---------------------------------------------------------------------------------------
        // SIZE PASS, Phase 5 step 3. The card was 620x950 against a 1080-unit reference — 88% of
        // the screen's height — and it did not fit. Two separate reasons, and both had to go:
        //
        //   1. 950 of 1080 leaves no room for a sixth button, let alone the Netplay Options button
        //      step 4 still has to place. The column overflowed and Leave Lobby drew off the
        //      bottom of the screen.
        //   2. The canvas matched width and height equally, so on a 21:9 window the scale was
        //      driven up by the width and a card already at 88% of a 16:9 screen was taller than
        //      the screen. Fixed on the other side, in LobbyPanel.CreateCanvas.
        //
        // Everything below is one proportional pass: the card is 500x765, which is 71% of the
        // height it was, and the column now has room for seven buttons rather than overflowing at
        // six. Heights are derived downward from the top edge, so changing one offset moves
        // everything under it — recompute PanelHeight if you touch any of them.
        // ---------------------------------------------------------------------------------------
        private const float PanelWidth = 500f;

        /// <summary>
        /// <see cref="ButtonsY"/> plus <see cref="ButtonColumnHeight"/> plus a bottom margin equal
        /// to the top one. Derived by hand because every offset above it is a constant; if this
        /// disagrees with the content the card either clips or carries dead space.
        /// </summary>
        private const float PanelHeight = 785f;

        /// <summary>Thickness of the border, drawn by insetting <c>Fill</c> inside <c>Panel</c>.</summary>
        private const float BorderThickness = 3f;

        private const float ContentWidth = 450f;
        private const float RuleWidth = 430f;

        /// <summary>
        /// Room for the button column, for a worst case of <b>seven</b>: Invite, Copy Code, Join
        /// Code, Ready, Start, Back and the Netplay Options button step 4 has yet to add. Six is
        /// what actually renders today.
        ///
        /// <para>The height is measured rather than chosen. <b>This constant has now been wrong
        /// three times</b> — 44 when the buttons were assumed small, 410 when a fifth button was
        /// added without revisiting it, and 520 when a sixth was. Each time the last button drew
        /// off the bottom of the card, and nothing clips an overflow, so it was invisible until
        /// somebody screenshotted it.</para>
        ///
        /// <para>The fix this time is on both sides: the buttons themselves are now built at about
        /// 52 units rather than 96 (<c>LobbyPanel.ResizeButtonToLabel</c>), and this reserves
        /// 7 x 52 + 6 x 10 = 424 with six units to spare. <c>LobbyPanel</c> measures the built
        /// column every time and now logs what it measured whether or not it fits, so the next
        /// person does not have to guess.</para>
        /// </summary>
        private const float ButtonColumnHeight = 430f;

        private const float ButtonSpacing = 10f;

        /// <summary>
        /// Placeholder size, for the editor preview only. It should stay in the neighbourhood of a
        /// real cloned button or the preview lies about how much room the column needs.
        /// </summary>
        private const float PlaceholderButtonWidth = 260f;
        private const float PlaceholderButtonHeight = 52f;

        // The members well is sized for a full lobby and never resizes. Six rows are always
        // reserved even when two are filled: the alternative is a button column that moves under
        // the cursor as people join, and empty rows inside a framed well read as free slots rather
        // than as a void — which is what the unstyled panel's blank middle looked like.
        private const int MaxMembers = 6;
        private const float MemberRowHeight = 28f;
        private const float MemberRowSpacing = 3f;
        private const float WellPadding = 6f;
        private const float MembersWidth = ContentWidth - 4f;

        private const float MembersHeight =
            (MaxMembers * MemberRowHeight) + ((MaxMembers - 1) * MemberRowSpacing) + (2f * WellPadding);

        private const float TitleY = -16f;
        private const float TitleHeight = 34f;
        private const float SubtitleY = -54f;
        private const float SubtitleHeight = 22f;
        private const float HeaderRuleY = -82f;
        private const float MembersY = -90f;
        private const float StatusY = MembersY - MembersHeight - 8f;
        private const float StatusHeight = 22f;
        private const float FooterRuleY = StatusY - StatusHeight - 8f;
        private const float ButtonsY = FooterRuleY - 10f;

        #endregion

        [MenuItem("MegabonkTogether/Scaffold Lobby Panel Prefab")]
        public static void Scaffold()
        {
            // Application.isBatchMode first: EditorUtility.DisplayDialog cannot be answered from a
            // headless run, and this is the entry point -executeMethod calls.
            if (!Application.isBatchMode
                && File.Exists(PrefabPath)
                && !EditorUtility.DisplayDialog(
                    "Regenerate prefab?",
                    $"{PrefabPath} already exists. Regenerating discards every edit made in the editor.",
                    "Regenerate",
                    "Cancel"))
            {
                return;
            }

            var root = new GameObject("LobbyPanel", typeof(RectTransform), typeof(CanvasGroup));
            var rootRect = root.GetComponent<RectTransform>();
            Stretch(rootRect);

            // Blocker: full-screen scrim that both dims the menu and eats clicks aimed at it.
            var blocker = CreateImage("Blocker", rootRect, Scrim);
            Stretch(blocker.rectTransform);

            // Panel is the border; Fill is the interior, inset by BorderThickness. A child cannot
            // draw behind its parent, so the frame has to be the outer object — which also means it
            // follows automatically if the card is ever resized. No sprite, no texture in the
            // bundle, and no 9-slice to get wrong.
            var panel = CreateImage("Panel", rootRect, PanelBorder);
            Centre(panel.rectTransform, new Vector2(PanelWidth, PanelHeight));

            var fill = CreateImage("Fill", panel.rectTransform, PanelFill);
            Inset(fill.rectTransform, BorderThickness);

            var title = CreateText("Title", panel.rectTransform, "Your Lobby", 30f,
                new Vector2(0f, TitleY), new Vector2(ContentWidth, TitleHeight));
            title.color = TitleInk;

            var subtitle = CreateText("Subtitle", panel.rectTransform, "Code: ABC123", 18f,
                new Vector2(0f, SubtitleY), new Vector2(ContentWidth, SubtitleHeight));
            subtitle.color = SubtitleInk;

            CreateRule("HeaderRule", panel.rectTransform, HeaderRuleY);

            CreateMembers(panel.rectTransform);

            // Below the list and directly above the buttons, because every message it carries is
            // the result of pressing one of them ("Lobby code copied", "Clipboard is empty").
            //
            // Separate from Subtitle on purpose, wherever it sits: Subtitle carries the lobby code
            // and is rewritten on every refresh tick, so a transient message shown there would be
            // erased within half a second — too fast to read.
            var status = CreateText("Status", panel.rectTransform, "", 18f,
                new Vector2(0f, StatusY), new Vector2(ContentWidth, StatusHeight));
            status.color = StatusInk;

            CreateRule("FooterRule", panel.rectTransform, FooterRuleY);

            CreateButtons(panel.rectTransform);

            Directory.CreateDirectory("Assets/Prefabs");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.Refresh();

            Debug.Log($"[MT] Wrote {PrefabPath}. Run MegabonkTogether/Build UI Bundle, then rebuild the plugin.");

            if (!Application.isBatchMode)
            {
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            }
        }

        /// <summary>
        /// The member list: a framed well with a VerticalLayoutGroup, fed at runtime by cloning
        /// MemberRow. Letting Unity lay the rows out is the whole point of the prefab — the
        /// code-built version hand-computed row Y positions from a pitch constant and got them
        /// wrong every time a font size changed.
        /// </summary>
        private static void CreateMembers(RectTransform panel)
        {
            var members = CreateImage("Members", panel, MembersWell);
            var membersRect = members.rectTransform;
            Anchor(membersRect, new Vector2(0f, MembersY), new Vector2(MembersWidth, MembersHeight));

            var layout = members.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset((int)WellPadding, (int)WellPadding, (int)WellPadding, (int)WellPadding);
            layout.spacing = MemberRowSpacing;
            layout.childAlignment = TextAnchor.UpperCenter;

            // Rows are stretched to the well's inner width and keep their own height. Set all four
            // explicitly rather than inheriting whatever the component's defaults are this Unity
            // version — the flags that matter here are not the ones that matter for the buttons.
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            // MemberRow is a template: the runtime clones it and leaves the original inactive.
            var row = CreateImage("MemberRow", membersRect, MemberRowFill);
            Anchor(row.rectTransform, Vector2.zero, new Vector2(MembersWidth - (2f * WellPadding), MemberRowHeight));

            // Anchored to the row's own edges rather than offset from its centre, so the columns
            // stay where they belong if the well is ever made wider.
            var rowName = CreateText("Name", row.rectTransform, "Player", 19f, Vector2.zero, Vector2.zero);
            rowName.alignment = TextAlignmentOptions.MidlineLeft;
            rowName.color = Color.white;
            EdgeAnchor(rowName.rectTransform, left: 14f, right: 150f);

            var rowReady = CreateText("Ready", row.rectTransform, "READY", 17f, Vector2.zero, Vector2.zero);
            rowReady.alignment = TextAlignmentOptions.MidlineRight;
            rowReady.color = new Color(0.55f, 0.95f, 0.55f);
            EdgeAnchor(rowReady.rectTransform, left: MembersWidth - (2f * WellPadding) - 144f, right: 14f);

            row.gameObject.SetActive(false);
        }

        /// <summary>
        /// The button column. Its children are placeholders and are destroyed at runtime: Megabonk's
        /// <c>Window</c> registry collects <c>MyButton</c> components and cannot see a plain uGUI
        /// Button, so the runtime clears this container and fills it with clones of the game's own
        /// button. Style the container; styling the placeholders changes nothing in game.
        /// </summary>
        private static void CreateButtons(RectTransform panel)
        {
            var buttons = new GameObject("Buttons", typeof(RectTransform), typeof(VerticalLayoutGroup));
            buttons.transform.SetParent(panel, worldPositionStays: false);
            Anchor(buttons.GetComponent<RectTransform>(), new Vector2(0f, ButtonsY), new Vector2(ContentWidth, ButtonColumnHeight));

            var layout = buttons.GetComponent<VerticalLayoutGroup>();
            layout.spacing = ButtonSpacing;

            // Centred, not top-anchored. The column reserves room for seven buttons and usually
            // shows six, so with UpperCenter the leftover pooled into one visible hole under the
            // last button. Centring splits it above and below, where it reads as margin.
            layout.childAlignment = TextAnchor.MiddleCenter;

            // Width must stay uncontrolled. A Megabonk button sizes itself from its label —
            // ButtonTextWrapper writes rect.sizeDelta from the text's size plus padding — so a
            // layout group that also drives width would be fighting the game for it every time a
            // label changed. Centred varying widths is the game's own look, not a defect.
            layout.childControlWidth = false;
            layout.childForceExpandWidth = false;
            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;

            foreach (var name in new[] { "Ready", "Start", "CopyCode", "JoinCode", "Leave" })
            {
                CreatePlaceholderButton(name, buttons.GetComponent<RectTransform>());
            }
        }

        private static Image CreateImage(string name, RectTransform parent, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, worldPositionStays: false);
            var image = go.GetComponent<Image>();
            image.color = colour;
            return image;
        }

        /// <summary>A hairline divider, full content width, centred at the given offset.</summary>
        private static void CreateRule(string name, RectTransform parent, float y)
        {
            var rule = CreateImage(name, parent, Rule);
            Anchor(rule.rectTransform, new Vector2(0f, y), new Vector2(RuleWidth, 2f));
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

            // The bundle ships no font — the runtime re-points every TMP component at the game's own
            // font after instantiating. So the editor preview's glyph widths are not the ones that
            // will be drawn, and every text box here is deliberately wider than it looks like it
            // needs to be. Do not tighten them against the preview.
            Anchor(tmp.rectTransform, position, sizeDelta);
            return tmp;
        }

        private static void CreatePlaceholderButton(string name, RectTransform parent)
        {
            var image = CreateImage(name, parent, PlaceholderButton);
            image.gameObject.AddComponent<Button>();
            Anchor(image.rectTransform, Vector2.zero, new Vector2(PlaceholderButtonWidth, PlaceholderButtonHeight));
            var label = CreateText("Label", image.rectTransform, name, 24f, Vector2.zero,
                new Vector2(PlaceholderButtonWidth, PlaceholderButtonHeight));
            Stretch(label.rectTransform);
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>Stretched to the parent and pulled in by <paramref name="amount"/> on all sides.</summary>
        private static void Inset(RectTransform rect, float amount)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(amount, amount);
            rect.offsetMax = new Vector2(-amount, -amount);
        }

        /// <summary>
        /// Stretched to the parent vertically and pinned to both of its horizontal edges, so the
        /// object keeps its margins whatever the parent's width becomes.
        /// </summary>
        private static void EdgeAnchor(RectTransform rect, float left, float right)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, 0f);
            rect.offsetMax = new Vector2(-right, 0f);
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

        private static Color Hex(string hex, float alpha = 1f)
        {
            if (!ColorUtility.TryParseHtmlString(hex, out var colour))
            {
                Debug.LogError($"[MT] Bad colour literal '{hex}'.");
                return Color.magenta;
            }

            colour.a = alpha;
            return colour;
        }
    }
}

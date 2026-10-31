using OmniBonk.Configuration;
using OmniBonk.Helpers;
using OmniBonk.Scripts.Button;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using TextCopy;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace OmniBonk.Scripts.Modal
{
    /// <summary>
    /// The centred lobby panel. Layout comes from <c>LobbyPanel.prefab</c> in the embedded bundle;
    /// buttons are clones of the game's PLAY button because <c>Window</c> only focuses
    /// <c>MyButton</c>s (a plain uGUI button lets clicks fall through). See <c>docs/ui/</c>.
    /// </summary>
    internal class LobbyPanel : MonoBehaviour
    {
        private const string PrefabPath = "Assets/Prefabs/LobbyPanel.prefab";

        /// <summary>
        /// Resolved in <see cref="Awake"/>, never statically: IL2CPP type registration runs the
        /// static constructor before the DI host exists, which stopped the plugin loading.
        /// </summary>
        private ILobbyViewService lobbyViewService;

        private IUiAssetService uiAssetService;

        private INetplaySessionService sessionService;

        /// <summary>Null when Steam is unavailable.</summary>
        private ISteamAvatarService avatarService;

        /// <summary>So a status repeated every refresh does not reset its own hold timer.</summary>
        private string shownSessionMessage = "";

        /// <summary>
        /// Swallows clicks while a session starts. Not <see cref="LoadingModal"/>: that parents to
        /// the game's canvas, which sorts below this one and would block nothing.
        /// </summary>
        private GameObject busyOverlay;

        private TextMeshProUGUI busyText;

        private string shownBusyMessage = "";

        /// <summary>Rebuilt on a timer, not per frame: each rebuild allocates and touches TMP.</summary>
        private const float RefreshIntervalSeconds = 0.5f;

        private float refreshAccumulator;

        /// <summary>
        /// Button height is label height + 2 x padding = 52 units exactly, because
        /// <c>ButtonTextWrapper.Refresh</c> sizes the background from the label rect. Fixed rather
        /// than measured so the column's height is predictable.
        /// </summary>
        private const float ButtonLabelFontSize = 30f;

        private const float ButtonLabelHeight = 40f;

        private const int ButtonLabelPaddingY = 6;

        private const float StatusHoldSeconds = 4f;

        private float statusClearAt;

        private MainMenu mainMenu;

        private GameObject root;

        /// <summary>Teardown destroys this, not the prefab, or an empty canvas leaks per open.</summary>
        private GameObject canvasObject;

        /// <summary>Groups, not SetActive: see <see cref="HideMainMenuChrome"/>.</summary>
        private readonly List<CanvasGroup> hiddenMenuGroups = [];

        private TextMeshProUGUI titleText;
        private TextMeshProUGUI codeText;
        private TextMeshProUGUI statusTextField;
        private Transform memberListRoot;
        private GameObject memberRowTemplate;
        private Transform buttonContainer;

        private readonly List<GameObject> memberRows = [];

        private bool warnedAboutButtonOverflow;

        private CustomButton inviteButton;
        private CustomButton copyCodeButton;
        private CustomButton joinFromClipboardButton;
        private CustomButton stopButton;

        private CustomButton optionsButton;
        private CustomButton saveToggleButton;
        private CustomButton sharedExpToggleButton;
        private CustomButton projectileCapToggleButton;
        private CustomButton enemyCapToggleButton;
        private CustomButton chestSkipToggleButton;
        private CustomButton duoShrineToggleButton;
        private CustomButton endlessToggleButton;
        private CustomButton optionsBackButton;

        /// <summary>The options view replaces the member list and button column.</summary>
        private bool showingOptions;

        // Prefab column offsets, saved so the lobby view can restore them exactly.
        private bool columnLayoutSaved;
        private bool columnInOptionsLayout;
        private Vector2 columnOffsetMin;
        private Vector2 columnOffsetMax;
        private CustomButton leaveLobbyButton;
        private CustomButton readyButton;
        private CustomButton startButton;

        /// <summary>Added <b>after</b> the buttons exist; see <see cref="Build"/>.</summary>
        private Window lobbyWindow;

        internal Action OnClosed { get; set; }

        internal Action OnContinueRequested { get; set; }

        internal Action OnLeaveRequested { get; set; }

        /// <summary>Receives the trimmed clipboard text.</summary>
        internal Action<string> OnJoinRequested { get; set; }

        internal Action OnCancelRequested { get; set; }

        /// <summary>
        /// The panel on screen, or null. Unity null on purpose: a destroyed panel reads as null.
        /// </summary>
        internal static LobbyPanel Current { get; private set; }

        /// <summary>
        /// Opens the panel, or returns the one already open. Does not start a session; the caller does.
        /// </summary>
        internal static LobbyPanel Open(MainMenu menu)
        {
            if (Current != null)
            {
                return Current;
            }

            var panelObj = new GameObject("LobbyPanel");
            var panel = panelObj.AddComponent<LobbyPanel>();

            // Before the component builds itself in Start.
            panel.Initialize(menu);

            panel.OnContinueRequested = () => GoToCharacterSelection(menu);
            panel.OnLeaveRequested = () => Plugin.Instance.NetworkHandler.ResetNetworking();

            panel.OnJoinRequested = code => SwitchSession(service => service.Join(code));

            panel.OnCancelRequested = () =>
                Plugin.Services.GetRequiredService<INetplaySessionService>().Cancel();

            return panel;
        }

        /// <summary>
        /// Ends the current session, then starts another. The Cancel is required: this peer is
        /// already hosting, and a second start would orphan its Steam lobby.
        /// <para><b>UNVERIFIED:</b> whether Steam has released the first lobby before the join searches.</para>
        /// </summary>
        private static void SwitchSession(Action<INetplaySessionService> start)
        {
            var service = Plugin.Services.GetRequiredService<INetplaySessionService>();

            service.Cancel();
            start(service);
        }

        private static void GoToCharacterSelection(MainMenu menu)
        {
            menu.GoToCharacterSelection();

            var characterMenu = Helpers.WindowCast.AsCharacterMenu(WindowManager.activeWindow);
            if (characterMenu == null || characterMenu.characterButtons == null)
            {
                return;
            }

            if (characterMenu.characterButtons.Count > 0)
            {
                characterMenu.selectedButton = characterMenu.characterButtons[0];
            }

            if (characterMenu.b_confirm != null)
            {
                characterMenu.b_confirm.SetInteractable(false);
            }
        }

        /// <summary>Call before the component's Start.</summary>
        internal void Initialize(MainMenu menu)
        {
            mainMenu = menu;
        }

        public void Awake()
        {
            Current = this;

            lobbyViewService = Plugin.Services.GetService<ILobbyViewService>();
            uiAssetService = Plugin.Services.GetService<IUiAssetService>();
            sessionService = Plugin.Services.GetService<INetplaySessionService>();
            avatarService = Plugin.Services.GetService<ISteamAvatarService>();

            // Unconditional on purpose: tells "never ran" from "ran but invisible".
            Plugin.Log.LogInfo(
                $"[lobby] LobbyPanel.Awake; lobby service: {lobbyViewService != null}, "
                + $"asset service: {uiAssetService != null}");
        }

        public void Start()
        {
            Build();
        }

        /// <summary>
        /// Order is load-bearing: adding <c>Window</c> caches <c>allButtons</c> immediately, so
        /// buttons must exist first or nothing is clickable.
        /// </summary>
        private void Build()
        {
            if (uiAssetService == null)
            {
                Plugin.Log.LogError("[lobby] No UI asset service; the lobby panel cannot be shown.");
                return;
            }

            // Async: the sync entry points cannot marshal the asset name. Callback fires exactly once.
            uiAssetService.RequestPrefab(PrefabPath, BuildFromPrefab);
        }

        private void BuildFromPrefab(GameObject prefab)
        {
            // The panel may be destroyed while the load is in flight.
            if (this == null || prefab == null)
            {
                if (prefab == null)
                {
                    Plugin.Log.LogError("[lobby] UI bundle unavailable; the lobby panel cannot be shown.");
                }

                return;
            }

            root = Instantiate(prefab);
            root.name = "MTLobbyPanel";
            canvasObject = CreateCanvas();
            root.transform.SetParent(canvasObject.transform, false);

            if (!BindPrefabChildren())
            {
                return;
            }

            ApplyGameFont();
            HideMainMenuChrome();

            EventManager.SubscribeLobbyStartRequestedEvents(OnLobbyStartRequested);

            CreateButtons();

            lobbyWindow = root.AddComponent<Window>();

            // Last sibling, so it draws over the panel.
            CreateBusyOverlay();

            Refresh();

            Plugin.Log.LogInfo("[lobby] LobbyPanel built from prefab and refreshed.");
        }

        /// <summary>
        /// Own canvas with a high sortingOrder, so the game's canvas cannot reorder us; scaler
        /// matches the game's reference resolution.
        /// </summary>
        private GameObject CreateCanvas()
        {
            var canvasObj = new GameObject("MTLobbyCanvas");

            var canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Well clear of the game's canvases, not just one above.
            canvas.sortingOrder = 1000;

            var scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;

            // Height only: a width blend overflows the card vertically on ultrawide screens.
            scaler.matchWidthOrHeight = 1f;

            canvasObj.AddComponent<GraphicRaycaster>();

            return canvasObj;
        }

        /// <summary>Child names are the prefab contract; a missing one fails loudly.</summary>
        private bool BindPrefabChildren()
        {
            titleText = FindText("Panel/Title");
            codeText = FindText("Panel/Subtitle");
            statusTextField = FindText("Panel/Status");
            memberListRoot = root.transform.Find("Panel/Members");
            buttonContainer = root.transform.Find("Panel/Buttons");

            memberRowTemplate = memberListRoot == null
                ? null
                : memberListRoot.Find("MemberRow")?.gameObject;

            if (titleText == null || memberListRoot == null || buttonContainer == null || memberRowTemplate == null)
            {
                Plugin.Log.LogError(
                    "[lobby] Prefab contract broken - missing Title, Members, Buttons or MemberRow. "
                    + "See docs/ui/01-ui-asset-bundle.md; child names are API.");
                return false;
            }

            memberRowTemplate.SetActive(false);
            return true;
        }

        private TextMeshProUGUI FindText(string path)
        {
            var child = root.transform.Find(path);
            return child == null ? null : child.GetComponent<TextMeshProUGUI>();
        }

        /// <summary>The bundle ships no font; every TMP label uses the game's.</summary>
        private void ApplyGameFont()
        {
            var source = GameFontSource();
            if (source == null)
            {
                Plugin.Log.LogWarning("[lobby] No game font found to apply; the panel will use the bundle's default.");
                return;
            }

            // IL2CPP: the generic GetComponentsInChildren<T> does not bind; use the wrapper.
            foreach (var label in root.RuntimeGetComponentsInChildren<TextMeshProUGUI>(true))
            {
                label.font = source.font;
                label.fontSharedMaterial = source.fontSharedMaterial;
            }
        }

        private TextMeshProUGUI GameFontSource()
        {
            var source = mainMenu == null || mainMenu.btnPlay == null
                ? null
                : mainMenu.btnPlay.GetComponentInChildren<TextMeshProUGUI>();

            return source == null || source.font == null ? null : source;
        }

        /// <summary>For labels outside <see cref="root"/>, such as the busy overlay.</summary>
        private void ApplyGameFontTo(TextMeshProUGUI label)
        {
            var source = GameFontSource();
            if (source == null || label == null)
            {
                return;
            }

            label.font = source.font;
            label.fontSharedMaterial = source.fontSharedMaterial;
        }

        /// <summary>
        /// Hides the main menu with a CanvasGroup, never SetActive(false): disabling a Window drops
        /// the open-window count to zero and the game hides the cursor.
        /// </summary>
        private void HideMainMenuChrome()
        {
            if (mainMenu == null)
            {
                return;
            }

            foreach (var menuRoot in new[] { mainMenu.tabMenu, mainMenu.leaderboards, mainMenu.quickQuests })
            {
                if (menuRoot == null || !menuRoot.activeSelf)
                {
                    continue;
                }

                var group = menuRoot.GetComponent<CanvasGroup>() ?? menuRoot.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                group.interactable = false;
                group.blocksRaycasts = false;
                hiddenMenuGroups.Add(group);
            }
        }

        private void RestoreMainMenuChrome()
        {
            foreach (var group in hiddenMenuGroups)
            {
                if (group != null)
                {
                    group.alpha = 1f;
                    group.interactable = true;
                    group.blocksRaycasts = true;
                }
            }

            hiddenMenuGroups.Clear();
        }

        private void CreateButtons()
        {
            // Drop the prefab's placeholder uGUI buttons. DestroyImmediate: Destroy waits until
            // frame end, and the column is measured later this frame.
            for (var i = buttonContainer.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(buttonContainer.GetChild(i).gameObject);
            }

            inviteButton = CreateButton("InviteButton", "Invite", OnInviteClicked);

            copyCodeButton = CreateButton("CopyCodeButton", "Copy Code", OnCopyCodeClicked);
            joinFromClipboardButton = CreateButton("JoinClipboardButton", "Join Code", OnJoinFromClipboardClicked);

            // No Quickplay button on purpose: docs/ui/05-drop-the-netplay-menu.md.
            readyButton = CreateButton("LobbyReadyButton", "Ready", OnReadyClicked);
            startButton = CreateButton("LobbyStartButton", "Start", OnStartClicked);

            leaveLobbyButton = CreateButton("LeaveLobbyButton", "Leave Lobby", OnLeaveLobbyClicked);

            // Options view buttons share the column; only one view's set is visible at a time.
            optionsButton = CreateButton("NetplayOptionsButton", "Options", OnOptionsClicked);
            saveToggleButton = CreateButton("SaveToggleButton", "Saves: OFF", OnSaveToggleClicked);
            sharedExpToggleButton = CreateButton("SharedExpToggleButton", "Shared XP: OFF", OnSharedExpToggleClicked);
            projectileCapToggleButton = CreateButton("ProjectileCapToggleButton", "No Proj. Cap: OFF", OnProjectileCapToggleClicked);
            enemyCapToggleButton = CreateButton("EnemyCapToggleButton", "No Enemy Cap: OFF", OnEnemyCapToggleClicked);
            chestSkipToggleButton = CreateButton("ChestSkipToggleButton", "Skip Chest Anim: OFF", OnChestSkipToggleClicked);
            duoShrineToggleButton = CreateButton("DuoShrineToggleButton", "Duo Shrines: OFF", OnDuoShrineToggleClicked);
            endlessToggleButton = CreateButton("EndlessToggleButton", "Endless: OFF", OnEndlessToggleClicked);
            optionsBackButton = CreateButton("OptionsBackButton", "Back", OnOptionsBackClicked);
        }

        /// <summary>Recreates rows rather than diffing: at most six, twice a second.</summary>
        private void Refresh()
        {
            if (lobbyViewService == null || memberListRoot == null)
            {
                return;
            }

            var isHost = lobbyViewService.IsLocalPlayerHost;
            var code = lobbyViewService.LobbyCode;

            if (showingOptions)
            {
                RefreshOptionsView();
                return;
            }

            if (titleText != null)
            {
                titleText.text = isHost ? "Your Lobby" : "Lobby";
            }

            if (codeText != null)
            {
                codeText.text = string.IsNullOrEmpty(code) ? "" : $"Code: {code}";
            }

            SetButtonVisible(saveToggleButton, false);
            SetButtonVisible(sharedExpToggleButton, false);
            SetButtonVisible(projectileCapToggleButton, false);
            SetButtonVisible(enemyCapToggleButton, false);
            SetButtonVisible(chestSkipToggleButton, false);
            SetButtonVisible(duoShrineToggleButton, false);
            SetButtonVisible(endlessToggleButton, false);
            SetButtonVisible(optionsBackButton, false);

            if (memberListRoot != null && !memberListRoot.gameObject.activeSelf)
            {
                memberListRoot.gameObject.SetActive(true);
            }

            SetOptionsColumnLayout(false);

            var inLobby = lobbyViewService.IsInLobby;

            // Still connecting or failed: show why.
            if (!inLobby)
            {
                ShowSessionStatus();
            }

            var members = lobbyViewService.GetMembers();

            // Joining elsewhere is offered while alone: the panel hosts immediately, so !inLobby is rare.
            var alone = !inLobby || members.Count <= 1;

            SetButtonVisible(inviteButton, inLobby && lobbyViewService.CanInvite);
            SetButtonVisible(copyCodeButton, inLobby && !string.IsNullOrEmpty(code));
            SetButtonVisible(joinFromClipboardButton, alone);

            // Always shown: after a failed host it is the only way back to the menu.
            SetButtonVisible(leaveLobbyButton, true);
            SetButtonLabel(leaveLobbyButton, inLobby ? "Leave Lobby" : "Back");

            SetButtonVisible(readyButton, inLobby);
            SetButtonVisible(optionsButton, true);

            // Host only; greyed until everyone is ready.
            SetButtonVisible(startButton, inLobby && isHost);
            SetButtonInteractable(startButton, lobbyViewService.AreAllMembersReady);

            SetButtonLabel(readyButton, lobbyViewService.IsLocalPlayerReady ? "Not Ready" : "Ready");

            foreach (var row in memberRows)
            {
                if (row != null)
                {
                    Destroy(row);
                }
            }
            memberRows.Clear();

            // Reuse the list above: GetMembers allocates.
            for (var i = 0; i < members.Count; i++)
            {
                memberRows.Add(CreateMemberRow(members[i]));
            }

            // Last: the registry is a snapshot, so hidden buttons would stay focusable.
            lobbyWindow?.FindAllButtonsInWindow();

            WarnIfButtonColumnOverflows();
        }

        /// <summary>Netplay settings as label-carrying toggle buttons, replacing the member list.</summary>
        private void RefreshOptionsView()
        {
            if (titleText != null)
            {
                titleText.text = "Netplay Options";
            }

            if (codeText != null)
            {
                codeText.text = "";
            }

            if (memberListRoot != null && memberListRoot.gameObject.activeSelf)
            {
                memberListRoot.gameObject.SetActive(false);
            }

            SetOptionsColumnLayout(true);

            SetButtonVisible(inviteButton, false);
            SetButtonVisible(copyCodeButton, false);
            SetButtonVisible(joinFromClipboardButton, false);
            SetButtonVisible(leaveLobbyButton, false);
            SetButtonVisible(readyButton, false);
            SetButtonVisible(startButton, false);
            SetButtonVisible(optionsButton, false);

            SetButtonVisible(saveToggleButton, true);
            SetButtonVisible(sharedExpToggleButton, true);
            SetButtonVisible(projectileCapToggleButton, true);
            SetButtonVisible(enemyCapToggleButton, true);
            SetButtonVisible(chestSkipToggleButton, true);
            SetButtonVisible(duoShrineToggleButton, true);
            SetButtonVisible(endlessToggleButton, true);
            SetButtonVisible(optionsBackButton, true);

            SetButtonLabel(saveToggleButton, ModConfig.AllowSavesDuringNetplay.Value ? "Saves: ON" : "Saves: OFF");
            SetButtonLabel(sharedExpToggleButton, ModConfig.EnabledSharedExperience.Value ? "Shared XP: ON" : "Shared XP: OFF");
            SetButtonLabel(projectileCapToggleButton, ModConfig.NoProjectileCap.Value ? "No Proj. Cap: ON" : "No Proj. Cap: OFF");
            SetButtonLabel(enemyCapToggleButton, ModConfig.NoEnemyCap.Value ? "No Enemy Cap: ON" : "No Enemy Cap: OFF");
            SetButtonLabel(chestSkipToggleButton, ModConfig.SkipChestAnimation.Value ? "Skip Chest Anim: ON" : "Skip Chest Anim: OFF");
            SetButtonLabel(duoShrineToggleButton, ModConfig.ShrineDuoBoost.Value ? "Duo Shrines: ON" : "Duo Shrines: OFF");
            SetButtonLabel(endlessToggleButton, ModConfig.EndlessRun.Value ? "Endless: ON" : "Endless: OFF");

            lobbyWindow?.FindAllButtonsInWindow();

            WarnIfButtonColumnOverflows();
        }

        /// <summary>
        /// In the options view, moves the button column up to the member list's top edge. Moved,
        /// not re-aligned: that needs <c>TextAnchor</c> from an unreferenced Unity module.
        /// </summary>
        private void SetOptionsColumnLayout(bool options)
        {
            if (columnInOptionsLayout == options || buttonContainer == null || memberListRoot == null)
            {
                return;
            }

            // Marked first so a failure below does not repeat on every refresh.
            columnInOptionsLayout = options;
            try
            {
                ApplyOptionsColumnLayout(options);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[lobby] Could not move the options button column: {ex.Message}");
            }
        }

        private void ApplyOptionsColumnLayout(bool options)
        {
            // The rule would cut through the moved column. Optional in the prefab.
            buttonContainer.parent?.Find("FooterRule")?.gameObject.SetActive(!options);


            // GetComponent, not a cast: see WarnIfButtonColumnOverflows.
            var column = buttonContainer.GetComponent<RectTransform>();
            var members = memberListRoot.GetComponent<RectTransform>();
            if (column == null || members == null)
            {
                return;
            }

            if (!columnLayoutSaved)
            {
                columnOffsetMin = column.offsetMin;
                columnOffsetMax = column.offsetMax;
                columnLayoutSaved = true;
            }

            if (options)
            {
                // Sibling-local space. Not GetWorldCorners: MissingMethodException at runtime.
                var memberTop = members.localPosition.y + members.rect.yMax;
                var columnTop = column.localPosition.y + column.rect.yMax;

                // Only the top moves: the bottom stays where it sits in the main view, so the column
                // grows by the shift. The options view holds more buttons than the prefab's column
                // height fits (8 need 486 units of 430), and this avoids rebuilding the bundle.
                var shift = new Vector2(0f, Mathf.Max(0f, memberTop - columnTop));
                column.offsetMin = columnOffsetMin;
                column.offsetMax = columnOffsetMax + shift;
            }
            else
            {
                column.offsetMin = columnOffsetMin;
                column.offsetMax = columnOffsetMax;
            }
        }

        private void OnOptionsClicked()
        {
            PlaySelectSfx();
            SetShowingOptions(true);
        }

        private void OnOptionsBackClicked()
        {
            PlaySelectSfx();
            SetShowingOptions(false);
        }

        /// <summary>
        /// Re-arms the column measurement: the two views hold different buttons, and measuring only the
        /// first one shown never reported the options view, the taller of the two.
        /// </summary>
        private void SetShowingOptions(bool show)
        {
            showingOptions = show;
            warnedAboutButtonOverflow = false;
            Refresh();
        }

#if DEBUG
        /// <summary>Test only (TestShowLobbyOptions): the auto-host's way into and out of the options view.</summary>
        internal void TestSetShowingOptions(bool show) => SetShowingOptions(show);
#endif

        /// <summary>Saved on every press, or quitting before a run would revert it.</summary>
        private void OnSaveToggleClicked()
        {
            PlaySelectSfx();

            ModConfig.AllowSavesDuringNetplay.Value = !ModConfig.AllowSavesDuringNetplay.Value;
            ModConfig.Save();

            Refresh();
        }

        private void OnSharedExpToggleClicked()
        {
            PlaySelectSfx();

            ModConfig.EnabledSharedExperience.Value = !ModConfig.EnabledSharedExperience.Value;
            ModConfig.Save();

            Refresh();
        }

        /// <summary>Lobby setting: only the host's value applies (sent by LobbySettingsService).</summary>
        private void OnProjectileCapToggleClicked()
        {
            PlaySelectSfx();

            ModConfig.NoProjectileCap.Value = !ModConfig.NoProjectileCap.Value;
            ModConfig.Save();

            Refresh();
        }

        /// <summary>Lobby setting, like the projectile cap: the host's value applies to the session.</summary>
        private void OnEnemyCapToggleClicked()
        {
            PlaySelectSfx();

            ModConfig.NoEnemyCap.Value = !ModConfig.NoEnemyCap.Value;
            ModConfig.Save();

            Refresh();
        }

        /// <summary>Lobby setting, like the projectile cap: the host's value applies to the session.</summary>
        private void OnChestSkipToggleClicked()
        {
            PlaySelectSfx();

            ModConfig.SkipChestAnimation.Value = !ModConfig.SkipChestAnimation.Value;
            ModConfig.Save();

            Refresh();
        }

        /// <summary>Lobby setting, like the projectile cap: the host's value applies to the session.</summary>
        private void OnDuoShrineToggleClicked()
        {
            PlaySelectSfx();

            ModConfig.ShrineDuoBoost.Value = !ModConfig.ShrineDuoBoost.Value;
            ModConfig.Save();

            Refresh();
        }

        /// <summary>Lobby setting, like the projectile cap: the host's value applies to the session.</summary>
        private void OnEndlessToggleClicked()
        {
            PlaySelectSfx();

            ModConfig.EndlessRun.Value = !ModConfig.EndlessRun.Value;
            ModConfig.Save();

            Refresh();
        }

        /// <summary>
        /// Warns once if the visible buttons overflow the column: nothing clips a layout group, so
        /// overflow otherwise only shows in screenshots.
        /// </summary>
        private void WarnIfButtonColumnOverflows()
        {
            if (warnedAboutButtonOverflow || buttonContainer == null)
            {
                return;
            }

            // IL2CPP: GetComponent, not `as RectTransform`; `as` tests the Transform-typed wrapper
            // and always returns null.
            var container = buttonContainer.GetComponent<RectTransform>();
            var layout = buttonContainer.GetComponent<VerticalLayoutGroup>();
            if (container == null || layout == null)
            {
                Plugin.Log.LogWarning(
                    "[lobby] Cannot measure the button column: "
                    + $"RectTransform {(container == null ? "missing" : "found")}, "
                    + $"VerticalLayoutGroup {(layout == null ? "missing" : "found")}.");
                return;
            }

            var needed = 0f;
            var visible = 0;
            for (var i = 0; i < buttonContainer.childCount; i++)
            {
                var child = buttonContainer.GetChild(i);
                if (!child.gameObject.activeSelf)
                {
                    continue;
                }

                // GetComponent again: `is RectTransform` has the same wrapper defect.
                var childRect = child.GetComponent<RectTransform>();
                if (childRect == null)
                {
                    continue;
                }

                needed += childRect.rect.height;
                visible++;
            }

            if (visible == 0)
            {
                Plugin.Log.LogWarning("[lobby] Cannot measure the button column: no visible buttons found.");
                return;
            }

            needed += layout.spacing * (visible - 1) + layout.padding.top + layout.padding.bottom;

            warnedAboutButtonOverflow = true;

            if (needed <= container.rect.height)
            {
                // Logged when it fits too, so resizing the prefab is arithmetic, not guessing.
                Plugin.Log.LogInfo(
                    $"[lobby] Button column: {visible} buttons need {needed:F0} of the "
                    + $"{container.rect.height:F0} units reserved "
                    + $"({container.rect.height - needed:F0} spare).");
                return;
            }

            Plugin.Log.LogWarning(
                $"[lobby] The button column needs {needed:F0} units for {visible} buttons but the "
                + $"prefab reserves {container.rect.height:F0}. The last one is drawing past the "
                + "bottom of the card. Raise ButtonColumnHeight in unity-ui's ScaffoldLobbyPanel "
                + "and rebuild the bundle.");
        }

        /// <summary>Clones the prefab's MemberRow; the layout group positions it.</summary>
        private GameObject CreateMemberRow(LobbyMemberView member)
        {
            var row = Instantiate(memberRowTemplate, memberListRoot, false);
            row.name = $"Member_{member.ConnectionId}";
            row.SetActive(true);

            // Text, not glyphs: the game font has no crown character.
            var role = member.IsHost ? " (host)" : "";
            var you = member.IsLocal ? " (you)" : "";

            BindAvatar(row, member);

            var nameLabel = row.transform.Find("Name")?.GetComponent<TextMeshProUGUI>();
            if (nameLabel != null)
            {
                nameLabel.text = $"{member.Name}{role}{you}";
                nameLabel.color = member.IsLocal ? new Color(1f, 0.95f, 0.6f) : Color.white;
            }

            var readyLabel = row.transform.Find("Ready")?.GetComponent<TextMeshProUGUI>();
            if (readyLabel != null)
            {
                // The version warning replaces the ready state.
                readyLabel.text = member.IsIncompatible ? "WRONG VERSION" : member.IsReady ? "READY" : "";
                readyLabel.color = member.IsIncompatible ? new Color(1f, 0.45f, 0.45f) : new Color(0.55f, 0.95f, 0.55f);
            }

            return row;
        }

        /// <summary>
        /// Shows the Steam avatar, or hides the slot: a missing avatar is normal (no Steam lobby,
        /// not yet downloaded) and appears on a later refresh.
        /// </summary>
        private void BindAvatar(GameObject row, LobbyMemberView member)
        {
            var slot = row.transform.Find("Avatar")?.GetComponent<RawImage>();
            if (slot == null)
            {
                return;
            }

            var avatar = avatarService?.TryGetAvatar(member.SteamId);
            if (avatar == null)
            {
                slot.gameObject.SetActive(false);
                return;
            }

            slot.texture = avatar;

            // Clear the prefab's placeholder tint.
            slot.color = Color.white;

            slot.gameObject.SetActive(true);
        }

        public void Update()
        {
            // Per frame: it gates input.
            UpdateBusyOverlay();

            if (statusClearAt > 0f && Time.unscaledTime >= statusClearAt)
            {
                statusClearAt = 0f;
                SetStatusText("");
            }

            refreshAccumulator += Time.unscaledDeltaTime;
            if (refreshAccumulator < RefreshIntervalSeconds)
            {
                return;
            }
            refreshAccumulator = 0f;

            Refresh();
        }

        /// <summary>
        /// The "starting a session" window. The full-canvas dim blocks clicks as a raycast target,
        /// so the buttons underneath never need disabling.
        /// </summary>
        private void CreateBusyOverlay()
        {
            busyOverlay = new GameObject("BusyOverlay");
            busyOverlay.transform.SetParent(canvasObject.transform, false);

            var overlayRect = busyOverlay.AddComponent<RectTransform>();
            overlayRect.anchorMin = Vector2.zero;
            overlayRect.anchorMax = Vector2.one;
            overlayRect.sizeDelta = Vector2.zero;
            overlayRect.anchoredPosition = Vector2.zero;

            var dim = busyOverlay.AddComponent<Image>();
            dim.color = new Color(0f, 0f, 0f, 0.75f);
            dim.raycastTarget = true;

            var card = new GameObject("BusyCard");
            card.transform.SetParent(busyOverlay.transform, false);

            var cardRect = card.AddComponent<RectTransform>();
            cardRect.anchorMin = new Vector2(0.5f, 0.5f);
            cardRect.anchorMax = new Vector2(0.5f, 0.5f);
            cardRect.pivot = new Vector2(0.5f, 0.5f);
            cardRect.sizeDelta = new Vector2(620f, 260f);
            cardRect.anchoredPosition = Vector2.zero;

            var cardImage = card.AddComponent<Image>();
            cardImage.color = new Color(0.10f, 0.08f, 0.07f, 0.98f);
            cardImage.raycastTarget = true;

            var textObj = new GameObject("BusyText");
            textObj.transform.SetParent(card.transform, false);

            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 0.45f);
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            textRect.anchoredPosition = Vector2.zero;

            busyText = textObj.AddComponent<TextMeshProUGUI>();
            busyText.text = "";
            busyText.fontSize = 40f;
            busyText.alignment = TextAlignmentOptions.Center;
            busyText.enableWordWrapping = true;
            busyText.raycastTarget = false;

            ApplyGameFontTo(busyText);

            CreateStopButton(card.transform);

            busyOverlay.SetActive(false);
        }

        /// <summary>
        /// Stop on the connecting window. Mouse only (known gap): the overlay is outside the
        /// panel's <c>Window</c>, so keyboard/controller focus cannot reach it.
        /// </summary>
        private void CreateStopButton(Transform card)
        {
            stopButton = CreateButton("LobbyStopButton", "Stop", OnStopClicked, card);
            if (stopButton == null)
            {
                return;
            }

            var rect = stopButton.GetComponent<RectTransform>();
            if (rect == null)
            {
                return;
            }

            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 28f);
        }

        /// <summary>Per frame, not on the refresh timer, so a double-click cannot slip through.</summary>
        private void UpdateBusyOverlay()
        {
            if (busyOverlay == null)
            {
                return;
            }

            var busy = sessionService != null && sessionService.IsBusy;

            if (busyOverlay.activeSelf != busy)
            {
                busyOverlay.SetActive(busy);

                // Re-snapshot focus so controllers cannot reach buttons behind the dim.
                lobbyWindow?.FindAllButtonsInWindow();
            }

            if (!busy)
            {
                return;
            }

            var message = sessionService.StatusMessage;
            if (string.IsNullOrEmpty(message))
            {
                message = "Working...";
            }

            if (message != shownBusyMessage)
            {
                shownBusyMessage = message;

                if (busyText != null)
                {
                    busyText.text = message;
                }
            }
        }

        private void ShowSessionStatus()
        {
            // While busy the overlay already shows it; this line is for failures.
            if (sessionService != null && sessionService.IsBusy)
            {
                return;
            }

            var message = sessionService?.StatusMessage ?? "";
            if (message == shownSessionMessage)
            {
                return;
            }

            shownSessionMessage = message;

            if (!string.IsNullOrEmpty(message))
            {
                SetStatusText(message);
            }
        }

        private void SetStatusText(string text)
        {
            if (statusTextField == null)
            {
                return;
            }

            statusTextField.text = text;
            statusClearAt = string.IsNullOrEmpty(text) ? 0f : Time.unscaledTime + StatusHoldSeconds;
        }

        private void OnInviteClicked()
        {
            PlaySelectSfx();

            // No status message: Steam's overlay is the feedback.
            lobbyViewService?.InviteFriends();
        }

        private void OnCopyCodeClicked()
        {
            PlaySelectSfx();

            var code = lobbyViewService?.LobbyCode;
            if (string.IsNullOrEmpty(code))
            {
                SetStatusText("No lobby code to copy");
                return;
            }

            try
            {
                ClipboardService.SetText(code);
                SetStatusText("Lobby code copied");
            }
            catch (Exception ex)
            {
                // Another process can hold the clipboard.
                Plugin.Log.LogWarning($"[lobby] Could not copy the lobby code: {ex.GetType().Name}: {ex.Message}");
                SetStatusText("Could not access the clipboard");
            }
        }

        private void OnJoinFromClipboardClicked()
        {
            PlaySelectSfx();

            string clipboard;
            try
            {
                clipboard = ClipboardService.GetText();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[lobby] Could not read the clipboard: {ex.GetType().Name}: {ex.Message}");
                SetStatusText("Could not access the clipboard");
                return;
            }

            var code = clipboard?.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(code))
            {
                SetStatusText("Clipboard is empty");
                return;
            }

            OnJoinRequested?.Invoke(code);
        }

        /// <summary>Cancels the pending session and closes back to the main menu.</summary>
        private void OnStopClicked()
        {
            PlaySelectSfx();

            OnCancelRequested?.Invoke();
            Close();
        }

        private void OnLeaveLobbyClicked()
        {
            PlaySelectSfx();

            // Also on teardown: a stale ready set would carry into the next lobby.
            lobbyViewService?.ResetReadyState();

            OnLeaveRequested?.Invoke();
            Close();
        }

        private void OnReadyClicked()
        {
            PlaySelectSfx();
            lobbyViewService?.ToggleLocalReady();

            Refresh();
        }

        private void OnStartClicked()
        {
            PlaySelectSfx();

            // Host-gated inside the service too.
            lobbyViewService?.RequestStart();
        }

        /// <summary>Lobby ended: local Start or the host's advance, one path for both.</summary>
        private void OnLobbyStartRequested()
        {
            var advance = OnContinueRequested;
            Close();
            advance?.Invoke();
        }

        private void Close()
        {
            var closed = OnClosed;

            // Cleared first: a callback that reopens the panel must not re-enter this one.
            OnClosed = null;

            DestroyUi();
            Destroy(gameObject);
            closed?.Invoke();
        }

        public void OnDestroy()
        {
            // ReferenceEquals: destroy is deferred, so a new panel may already be Current.
            if (ReferenceEquals(Current, this))
            {
                Current = null;
            }

            // Here too, for teardown by scene change.
            RestoreMainMenuChrome();

            // Otherwise a second lobby would advance twice.
            EventManager.UnsubscribeLobbyStartRequestedEvents(OnLobbyStartRequested);

            memberRows.Clear();

            DestroyUi();
        }

        /// <summary>Window.OnDisable then returns focus and cursor to the game.</summary>
        private void DestroyUi()
        {
            if (canvasObject != null)
            {
                Destroy(canvasObject);
            }

            canvasObject = null;
            root = null;
        }

        #region Button helpers

        /// <summary>
        /// Clones the game's PLAY button. No position or size: the layout group owns both.
        /// </summary>
        /// <param name="parent">Defaults to the button column.</param>
        private CustomButton CreateButton(string name, string label, Action onClick, Transform parent = null)
        {
            if (mainMenu == null || mainMenu.btnPlay == null)
            {
                Plugin.Log.LogWarning($"[lobby] MainMenu.btnPlay unavailable; '{label}' not created.");
                return null;
            }

            var buttonObj = Instantiate(mainMenu.btnPlay.gameObject);
            buttonObj.name = name;
            buttonObj.transform.SetParent(parent ?? buttonContainer, false);

            var unityButton = buttonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (unityButton != null)
            {
                StripPlayButtonHandlers(unityButton);
            }

            // The localiser would overwrite our label text.
            var localize = buttonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localize != null)
            {
                Destroy(localize);
            }

            var button = ReplaceWithCustomButton(buttonObj);
            if (button == null)
            {
                Plugin.Log.LogWarning($"[lobby] Cloned button '{label}' had no MyButtonNormal to replace.");
                return null;
            }

            button.SetOnClickAction(onClick);
            SetButtonLabel(button, label);
            return button;
        }

        /// <summary>
        /// <c>RemoveAllListeners</c> misses persistent (serialized) listeners, and PLAY's starts
        /// the game, so those are switched off too.
        /// </summary>
        private static void StripPlayButtonHandlers(UnityEngine.UI.Button button)
        {
            button.onClick.RemoveAllListeners();

            for (var i = button.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
            {
                button.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            }
        }

        /// <summary>
        /// Swaps <c>MyButtonNormal</c> for <c>CustomButton</c>, copying its serialized state (hover,
        /// colours, disabled overlay). Appending an onClick listener is not possible: our
        /// <c>UnityAction</c> is a managed delegate with no IL2CPP bridge.
        /// </summary>
        private static CustomButton ReplaceWithCustomButton(GameObject buttonObj)
        {
            var style = ButtonStyle.CaptureAndRemove(buttonObj);
            if (!style.Captured)
            {
                return null;
            }

            var button = buttonObj.AddComponent<CustomButton>();
            style.ApplyTo(button);

            return button;
        }

        private static void SetButtonLabel(MyButtonNormal button, string label)
        {
            if (button == null)
            {
                return;
            }

            var wrapper = button.gameObject.GetComponent<ButtonTextWrapper>();
            if (wrapper != null && wrapper.t_text != null && wrapper.t_text.text != label)
            {
                wrapper.t_text.text = label;
                ResizeButtonToLabel(wrapper, label);
            }
        }

        /// <summary>
        /// Re-fits the background to a new label. Decompiled: <c>ButtonTextWrapper.Refresh</c> sizes
        /// it from <c>t_text.rectTransform.sizeDelta</c> + padding, and TMP never widens that rect
        /// itself, so the preferred width is written onto it first.
        /// </summary>
        private static void ResizeButtonToLabel(ButtonTextWrapper wrapper, string label)
        {
            var textRect = wrapper.t_text.rectTransform;
            if (textRect == null)
            {
                return;
            }

            // Before measuring: GetPreferredValues uses the current font size.
            wrapper.t_text.fontSize = ButtonLabelFontSize;
            wrapper.paddingY = ButtonLabelPaddingY;

            var preferred = wrapper.t_text.GetPreferredValues(label);
            if (preferred.x > 0f)
            {
                textRect.sizeDelta = new Vector2(preferred.x, ButtonLabelHeight);
            }

            wrapper.t_text.ForceMeshUpdate(false, false);
            LayoutRebuilder.ForceRebuildLayoutImmediate(textRect);
            wrapper.Refresh();
        }

        private static void SetButtonInteractable(MyButtonNormal button, bool interactable)
        {
            if (button == null)
            {
                return;
            }

            button.SetInteractable(interactable);
        }

        private static void SetButtonVisible(MyButtonNormal button, bool visible)
        {
            if (button != null && button.gameObject.activeSelf != visible)
            {
                button.gameObject.SetActive(visible);
            }
        }

        private static void PlaySelectSfx()
        {
            var audio = AudioManager.Instance;
            if (audio != null && audio.uiSelect != null && audio.uiSelect.sounds.Count > 0)
            {
                audio.PlaySfx(audio.uiSelect.sounds[0]);
            }
        }

        #endregion
    }
}

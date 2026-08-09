using MegabonkTogether.Helpers;
using MegabonkTogether.Scripts.Button;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using TextCopy;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace MegabonkTogether.Scripts.Modal
{
    /// <summary>
    /// The centred lobby panel: who is here, and what you can do about it.
    ///
    /// <para><b>Prefab-driven.</b> The hierarchy, sizes and spacing come from
    /// <c>Assets/Prefabs/LobbyPanel.prefab</c> in <c>unity-ui/</c>, loaded out of the embedded
    /// AssetBundle. This class binds to named children and drives them; it does not lay anything
    /// out. The version that did was wrong about button height, row pitch and font coverage in
    /// three successive builds, because none of it was visible until the game was running.</para>
    ///
    /// <para><b>Buttons are still clones of the game's PLAY button.</b> Not an oversight and not
    /// leftovers: Megabonk's <c>Window</c> collects <c>MyButton</c> components, and a plain uGUI
    /// Button in the prefab is invisible to that registry — which is what let clicks fall through
    /// to the main menu. The prefab supplies the container and its layout group; the runtime
    /// supplies buttons the game can actually focus.</para>
    ///
    /// <para>Contract and failure modes: <c>docs/ui/01-ui-asset-bundle.md</c>. Why the ordering in
    /// <see cref="Build"/> is not negotiable: <c>docs/ui/02-prefab-handover.md</c>.</para>
    /// </summary>
    internal class LobbyPanel : MonoBehaviour
    {
        private const string PrefabPath = "Assets/Prefabs/LobbyPanel.prefab";

        /// <summary>
        /// Resolved in <see cref="Awake"/>, never in a static initialiser.
        ///
        /// <para>This was a <c>static readonly</c> field assigned from <c>Plugin.Services</c>, and
        /// it stopped the entire plugin loading. <c>ClassInjector.RegisterTypeInIl2Cpp&lt;T&gt;</c>
        /// runs the type's static constructor, and registration happens in <c>Plugin.Load</c> ~50
        /// lines before the DI host is built.</para>
        /// </summary>
        private ILobbyViewService lobbyViewService;

        private IUiAssetService uiAssetService;

        /// <summary>
        /// Rebuilt on a timer, never per frame. <see cref="ILobbyViewService.GetMembers"/> allocates
        /// a list and each rebuild touches TMP text, so at 60 Hz this would be exactly the kind of
        /// idle allocation <c>docs/netplay/04-performance-and-gc.md</c> exists to prevent.
        /// </summary>
        private const float RefreshIntervalSeconds = 0.5f;

        private float refreshAccumulator;

        /// <summary>How long a transient status message stays up before the panel clears it.</summary>
        private const float StatusHoldSeconds = 4f;

        private float statusClearAt;

        /// <summary>
        /// Handed in rather than found — the panel needs the menu's button to clone and its roots to
        /// hide, and a scene search per use is both wasteful and fragile.
        /// </summary>
        private MainMenu mainMenu;

        /// <summary>The instantiated prefab. Everything else is found underneath it.</summary>
        private GameObject root;

        /// <summary>
        /// The canvas built for this panel. Destroying it takes <see cref="root"/> with it, so
        /// teardown targets this and not the prefab — destroying only the prefab would leave an
        /// empty canvas behind on every open/close cycle.
        /// </summary>
        private GameObject canvasObject;

        /// <summary>
        /// CanvasGroups added to the main-menu roots to hide them while the lobby is open, kept so
        /// they can be turned back on. Groups, not SetActive — see <see cref="HideMainMenuChrome"/>.
        /// </summary>
        private readonly List<CanvasGroup> hiddenMenuGroups = [];

        private TextMeshProUGUI titleText;
        private TextMeshProUGUI codeText;
        private TextMeshProUGUI statusTextField;
        private Transform memberListRoot;
        private GameObject memberRowTemplate;
        private Transform buttonContainer;

        private readonly List<GameObject> memberRows = [];

        private CustomButton copyCodeButton;
        private CustomButton joinFromClipboardButton;
        private CustomButton leaveLobbyButton;
        private CustomButton readyButton;
        private CustomButton startButton;

        /// <summary>
        /// The game's own Window component. Added <b>after</b> the buttons exist — see
        /// <see cref="Build"/>.
        /// </summary>
        private Window lobbyWindow;

        /// <summary>Set by the caller that opened the panel; runs after the panel closes.</summary>
        internal Action OnClosed { get; set; }

        /// <summary>Runs when the lobby ends and this peer should advance to character selection.</summary>
        internal Action OnContinueRequested { get; set; }

        /// <summary>Runs when Leave lobby is pressed, before the panel closes.</summary>
        internal Action OnLeaveRequested { get; set; }

        /// <summary>Runs when Join from clipboard is pressed, with the trimmed clipboard text.</summary>
        internal Action<string> OnJoinRequested { get; set; }

        /// <summary>Call before the panel builds itself — i.e. before the component is enabled.</summary>
        internal void Initialize(MainMenu menu)
        {
            mainMenu = menu;
        }

        public void Awake()
        {
            lobbyViewService = Plugin.Services.GetService<ILobbyViewService>();
            uiAssetService = Plugin.Services.GetService<IUiAssetService>();

            // Unconditional lifecycle logging, deliberately. Two rounds were spent unable to tell
            // "the panel never ran" from "the panel ran and rendered invisibly", because every log
            // line in here was on a failure branch.
            Plugin.Log.LogInfo(
                $"[lobby] LobbyPanel.Awake; lobby service: {lobbyViewService != null}, "
                + $"asset service: {uiAssetService != null}");
        }

        public void Start()
        {
            Build();
        }

        /// <summary>
        /// <para><b>The order here is load-bearing.</b> <c>AddComponent&lt;Window&gt;()</c> fires
        /// <c>OnEnable</c> immediately, which calls <c>FocusWindow</c>, which caches
        /// <c>allButtons</c> from whatever children exist at that instant. Adding the Window before
        /// the buttons cached an empty list and left nothing focusable — no click anywhere on the
        /// panel did anything. Buttons first, Window second.</para>
        /// </summary>
        private void Build()
        {
            if (uiAssetService == null)
            {
                Plugin.Log.LogError("[lobby] No UI asset service; the lobby panel cannot be shown.");
                return;
            }

            // Asynchronous because the synchronous entry points cannot marshal their asset name on
            // this install. The callback always fires exactly once, so there is no timeout to run.
            uiAssetService.RequestPrefab(PrefabPath, BuildFromPrefab);
        }

        private void BuildFromPrefab(GameObject prefab)
        {
            // The panel can be torn down while the load is in flight — leaving a lobby, or a scene
            // change. Building onto a destroyed component would strand a canvas with no owner.
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

            // Only now, with every button parented and present.
            lobbyWindow = root.AddComponent<Window>();

            Refresh();

            Plugin.Log.LogInfo("[lobby] LobbyPanel built from prefab and refreshed.");
        }

        /// <summary>
        /// Builds a canvas of our own for the panel to live on.
        ///
        /// <para>The panel used to be parented under the game's <c>GameObject.Find("Canvas")</c>,
        /// which meant competing for sibling order with the menu it sits on top of — the source of
        /// the <c>SetAsFirstSibling</c>/<c>SetAsLastSibling</c> shuffling that earlier builds
        /// needed, and of the menu showing through the blocker. A separate canvas with a high
        /// <c>sortingOrder</c> is in front by construction, and nothing the game does to its own
        /// canvas can reorder us.</para>
        ///
        /// <para>The scaler matches the reference resolution the game's own UI is authored
        /// against, so the prefab's pixel sizes mean the same thing at any window size.</para>
        /// </summary>
        private GameObject CreateCanvas()
        {
            var canvasObj = new GameObject("MTLobbyCanvas");

            var canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Well clear of the game's own canvases rather than one above them, so this does not
            // become a race the next time the game adds a layer.
            canvas.sortingOrder = 1000;

            var scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // Without a raycaster the panel draws and nothing on it can be clicked.
            canvasObj.AddComponent<GraphicRaycaster>();

            return canvasObj;
        }

        /// <summary>
        /// Resolves the named children the prefab contract guarantees. Missing ones are a contract
        /// break — the prefab was renamed or reparented — so they are reported loudly rather than
        /// tolerated into a half-drawn panel.
        /// </summary>
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
                    "[lobby] Prefab contract broken — missing Title, Members, Buttons or MemberRow. "
                    + "See docs/ui/01-ui-asset-bundle.md; child names are API.");
                return false;
            }

            // The template is a shape to clone, never a row itself.
            memberRowTemplate.SetActive(false);
            return true;
        }

        private TextMeshProUGUI FindText(string path)
        {
            var child = root.transform.Find(path);
            return child == null ? null : child.GetComponent<TextMeshProUGUI>();
        }

        /// <summary>
        /// Re-points every TMP component at the font the game draws with.
        ///
        /// <para>The bundle ships no font: a TMP atlas would dominate its size, and a font we
        /// shipped would not be the game's, so the panel would read as foreign however well the
        /// layout matched. The cost is that editor previews do not predict runtime glyph widths.</para>
        /// </summary>
        private void ApplyGameFont()
        {
            var source = mainMenu == null || mainMenu.btnPlay == null
                ? null
                : mainMenu.btnPlay.GetComponentInChildren<TextMeshProUGUI>();

            if (source == null || source.font == null)
            {
                Plugin.Log.LogWarning("[lobby] No game font found to apply; the panel will use the bundle's default.");
                return;
            }

            // The repo's reflection wrapper, not the direct call. Under IL2CPP the generic
            // GetComponentsInChildren<T> does not bind the way the stock signature suggests, which
            // is exactly why Helpers/Helper.cs carries this.
            foreach (var label in root.RuntimeGetComponentsInChildren<TextMeshProUGUI>(true))
            {
                label.font = source.font;
                label.fontSharedMaterial = source.fontSharedMaterial;
            }
        }

        /// <summary>
        /// Clears the main menu so the lobby sits on the empty scene.
        ///
        /// <para><b>A CanvasGroup, never SetActive(false).</b> The game tracks open menus itself:
        /// <c>Window.OnDisable</c> calls <c>WindowManager.WindowClosed()</c>, and when the open-window
        /// count reaches zero <c>WindowManager.RefreshCursor()</c> hides the mouse cursor. tabMenu
        /// holds the active Window, so deactivating it left the panel drawn but the game convinced
        /// no menu was open — cursor gone, nothing clickable.</para>
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

        /// <summary>Puts back exactly what <see cref="HideMainMenuChrome"/> took away.</summary>
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
            // The prefab's placeholder buttons exist so the column's shape is visible in the editor.
            // They are uGUI Buttons, which Megabonk's Window registry does not collect, so they are
            // cleared and replaced with clones of the game's own button.
            for (var i = buttonContainer.childCount - 1; i >= 0; i--)
            {
                Destroy(buttonContainer.GetChild(i).gameObject);
            }

            // Copy and Join-from-clipboard are mutually exclusive — you either have a lobby or you
            // do not — so hiding one must not leave a gap. The layout group closes it for free,
            // which the hand-placed version could not do.
            copyCodeButton = CreateButton("CopyCodeButton", "Copy Code", OnCopyCodeClicked);
            joinFromClipboardButton = CreateButton("JoinClipboardButton", "Join From Clipboard", OnJoinFromClipboardClicked);
            readyButton = CreateButton("LobbyReadyButton", "Ready", OnReadyClicked);
            startButton = CreateButton("LobbyStartButton", "Start", OnStartClicked);

            // "Back" and "Leave Lobby" would be the same action here — the panel only exists while
            // you are in a lobby, so going back IS leaving.
            leaveLobbyButton = CreateButton("LeaveLobbyButton", "Leave Lobby", OnLeaveLobbyClicked);
        }

        /// <summary>
        /// Rebuilds the member rows. Destroys and recreates rather than diffing: the list is at most
        /// six entries and only redraws twice a second.
        /// </summary>
        private void Refresh()
        {
            if (lobbyViewService == null || memberListRoot == null)
            {
                return;
            }

            var isHost = lobbyViewService.IsLocalPlayerHost;
            var code = lobbyViewService.LobbyCode;

            if (titleText != null)
            {
                titleText.text = isHost ? "Your Lobby" : "Lobby";
            }

            if (codeText != null)
            {
                codeText.text = string.IsNullOrEmpty(code) ? "" : $"Code: {code}";
            }

            // Hide rather than grey out, so the panel never offers an action that cannot work.
            var inLobby = lobbyViewService.IsInLobby;
            SetButtonVisible(copyCodeButton, inLobby && !string.IsNullOrEmpty(code));
            SetButtonVisible(leaveLobbyButton, inLobby);
            SetButtonVisible(joinFromClipboardButton, !inLobby);
            SetButtonVisible(readyButton, inLobby);

            // Start is the host's alone. Greyed rather than hidden for the host, so the reason the
            // run has not begun is visible; hidden entirely for clients, for whom it is not theirs.
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

            var members = lobbyViewService.GetMembers();
            for (var i = 0; i < members.Count; i++)
            {
                memberRows.Add(CreateMemberRow(members[i]));
            }

            // Last, after every show/hide above. The registry is a snapshot, so a button that was
            // just hidden would otherwise stay focusable.
            lobbyWindow?.FindAllButtonsInWindow();
        }

        /// <summary>
        /// Clones the prefab's MemberRow. No positioning here — the Members layout group owns that,
        /// which is the point of the prefab. The hand-placed version computed row Y from a pitch
        /// constant and spaced rows tighter than their own height.
        /// </summary>
        private GameObject CreateMemberRow(LobbyMemberView member)
        {
            var row = Instantiate(memberRowTemplate, memberListRoot, false);
            row.name = $"Member_{member.ConnectionId}";
            row.SetActive(true);

            // Text, not glyphs. The first version used a crown character the game's font does not
            // carry, so every host row rendered as a tofu box.
            var role = member.IsHost ? " (host)" : "";
            var you = member.IsLocal ? " (you)" : "";

            var nameLabel = row.transform.Find("Name")?.GetComponent<TextMeshProUGUI>();
            if (nameLabel != null)
            {
                nameLabel.text = $"{member.Name}{role}{you}";
                nameLabel.color = member.IsLocal ? new Color(1f, 0.95f, 0.6f) : Color.white;
            }

            // Its own object in the prefab, right-aligned. The previous version faked this column
            // with a <pos=76%> tag inside a single label, which never got tested.
            var readyLabel = row.transform.Find("Ready")?.GetComponent<TextMeshProUGUI>();
            if (readyLabel != null)
            {
                readyLabel.text = member.IsReady ? "READY" : "";
                readyLabel.color = new Color(0.55f, 0.95f, 0.55f);
            }

            return row;
        }

        public void Update()
        {
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

        private void SetStatusText(string text)
        {
            if (statusTextField == null)
            {
                return;
            }

            statusTextField.text = text;
            statusClearAt = string.IsNullOrEmpty(text) ? 0f : Time.unscaledTime + StatusHoldSeconds;
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
                // The clipboard is an OS resource another process can hold. Failing to copy must
                // not take the panel down with it.
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

        private void OnLeaveLobbyClicked()
        {
            PlaySelectSfx();

            // Dropped here as well as on teardown: a stale ready set would make the next lobby
            // start out believing people it has never met are already ready.
            lobbyViewService?.ResetReadyState();

            OnLeaveRequested?.Invoke();
            Close();
        }

        private void OnReadyClicked()
        {
            PlaySelectSfx();
            lobbyViewService?.ToggleLocalReady();

            // Redrawn immediately rather than waiting for the refresh tick: the label still needs to
            // stop saying the thing that was just pressed. The host's broadcast settles it.
            Refresh();
        }

        private void OnStartClicked()
        {
            PlaySelectSfx();

            // Host-gated inside the service too, not just by this button's enabled state.
            lobbyViewService?.RequestStart();
        }

        /// <summary>
        /// The lobby has ended — either this peer's host pressed Start, or the host told us to
        /// advance. One path for both.
        /// </summary>
        private void OnLobbyStartRequested()
        {
            var advance = OnContinueRequested;
            Close();
            advance?.Invoke();
        }

        private void Close()
        {
            var closed = OnClosed;

            // Cleared before invoking: this destroys the component, and a callback that reopens the
            // panel would otherwise re-enter a half-destroyed object.
            OnClosed = null;

            DestroyUi();
            Destroy(gameObject);
            closed?.Invoke();
        }

        public void OnDestroy()
        {
            // Restored here rather than only in Close(), so a panel torn down by a scene change
            // still gives the menu back instead of leaving the player on a blank screen.
            RestoreMainMenuChrome();

            // Unsubscribed or the delegate keeps this destroyed panel alive and a second lobby
            // would advance twice.
            EventManager.UnsubscribeLobbyStartRequestedEvents(OnLobbyStartRequested);

            memberRows.Clear();

            DestroyUi();
        }

        /// <summary>
        /// Tears down the canvas and everything on it.
        ///
        /// <para>Destroying the root fires <c>Window.OnDisable</c>, which hands menu focus and the
        /// cursor back to the game. Nothing else needs to undo that.</para>
        /// </summary>
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
        /// Clones the game's PLAY button into the prefab's button container.
        ///
        /// <para><b>The game's own <c>MyButtonNormal</c> is kept, not replaced.</b> The previous
        /// version destroyed it and added a <c>CustomButton : MyButtonNormal</c> subclass that
        /// overrode <c>OnClick</c>. That injects a managed type into the IL2CPP vtable and throws
        /// away everything the component does besides clicking — hover scaling, colour states,
        /// SFX, and the focus handling that <c>Window</c>'s registry drives. Appending a listener
        /// keeps all of it and adds ours on top.</para>
        ///
        /// <para>No position or size is set: the container's VerticalLayoutGroup owns both. Every
        /// hand-computed button constant in the previous version was wrong at least once — 420x48
        /// against real 300x70 clones, then a pitch tighter than the button height.</para>
        /// </summary>
        private CustomButton CreateButton(string name, string label, Action onClick)
        {
            if (mainMenu == null || mainMenu.btnPlay == null)
            {
                Plugin.Log.LogWarning($"[lobby] MainMenu.btnPlay unavailable; '{label}' not created.");
                return null;
            }

            var buttonObj = Instantiate(mainMenu.btnPlay.gameObject);
            buttonObj.name = name;
            buttonObj.transform.SetParent(buttonContainer, false);

            var unityButton = buttonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (unityButton != null)
            {
                StripPlayButtonHandlers(unityButton);
            }

            // The localiser has to go — these labels are not table entries, and it would overwrite
            // the text on its next refresh.
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
        /// Silences everything the cloned PLAY button was wired to do.
        ///
        /// <para><c>RemoveAllListeners</c> alone is not enough, and this cost several rounds of
        /// "the button works and also starts the game". It removes only listeners added at
        /// <b>runtime</b>; the ones serialized on the prefab in the Inspector — <b>persistent</b>
        /// listeners — survive it untouched. PLAY's persistent listener is what advances to
        /// character selection, so every clone carried that behaviour no matter what we bound on
        /// top of it.</para>
        ///
        /// <para>Persistent listeners cannot be removed at runtime, only switched off, which is
        /// what <c>SetPersistentListenerState(i, Off)</c> does.</para>
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
        /// Swaps the clone's <c>MyButtonNormal</c> for a <c>CustomButton</c>, <b>carrying its
        /// serialized state across</b>.
        ///
        /// <para>The previous version destroyed the original and added a bare replacement, which
        /// left <c>background</c>, <c>scaleOnHover</c>, <c>button</c> and <c>disabledOverlay</c>
        /// null and the colours at type defaults. Those fields are what drive hover scaling,
        /// colour states and the greyed-out look, so the buttons rendered but behaved wrongly —
        /// and <see cref="SetButtonInteractable"/> in particular had nothing to grey out.</para>
        ///
        /// <para>Ideally the game's own component would be kept and a listener appended to
        /// <c>Button.onClick</c>, which is how the design being followed does it. That is not
        /// available here: <c>UnityEngine.CoreModule</c> is referenced from <c>unity-libs</c>, so
        /// <c>UnityAction</c> compiles as a managed delegate with no bridge to the IL2CPP one.
        /// Copying the fields gets the same behaviour without moving the whole codebase onto the
        /// interop CoreModule.</para>
        /// </summary>
        private static CustomButton ReplaceWithCustomButton(GameObject buttonObj)
        {
            var original = buttonObj.GetComponent<MyButtonNormal>();
            if (original == null)
            {
                return null;
            }

            var background = original.background;
            var defaultColor = original.defaultColor;
            var hoverColor = original.hoverColor;
            var scaleOnHover = original.scaleOnHover;
            var hoverScale = original.hoverScale;
            var unityButton = original.button;
            var disabledOverlay = original.disabledOverlay;
            var customSfx = original.customSfx;

            // Immediate, not deferred. Destroy() runs at end of frame, which would leave two
            // MyButton-derived components on this object for the rest of the frame — and
            // Window.FindAllButtonsInWindow collects every MyButton it can see.
            DestroyImmediate(original);

            var button = buttonObj.AddComponent<CustomButton>();
            button.background = background;
            button.defaultColor = defaultColor;
            button.hoverColor = hoverColor;
            button.scaleOnHover = scaleOnHover;
            button.hoverScale = hoverScale;
            button.button = unityButton;
            button.disabledOverlay = disabledOverlay;
            button.customSfx = customSfx;

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
                ResizeButtonToLabel(wrapper);
            }
        }

        /// <summary>
        /// Re-fits the button's background to the label that was just written into it.
        ///
        /// <para>The game sizes its buttons from their text rather than authoring a width per
        /// button, which is why five clones of the same PLAY button come out five different widths.
        /// <c>ButtonTextWrapper.Refresh</c> is what does it — decompiled, it sets
        /// <c>rect.sizeDelta = t_text.rectTransform.sizeDelta + 2 * (paddingX, paddingY)</c> and
        /// re-anchors the label. Something in the game's own lifecycle calls it once when a button
        /// comes up, which is why the first label always fits.</para>
        ///
        /// <para><b>Nothing calls it again.</b> The Ready button is the only label here that changes
        /// after creation, and "Not Ready" was drawn at the width computed for "Ready" — the text
        /// spilling out past both ends of the background.</para>
        ///
        /// <para>The two forcing calls before it are not decoration. <c>Refresh</c> reads the label's
        /// <b>current</b> <c>sizeDelta</c>, and a TMP component does not resize on assignment: with
        /// <c>autoSizeTextContainer</c> its rect follows the mesh, which regenerates on the next
        /// canvas update, and under a <c>ContentSizeFitter</c> it follows the next layout pass.
        /// Calling <c>Refresh</c> without forcing both would fit the background to the previous
        /// label — the same defect one frame earlier. Which of the two mechanisms this button
        /// actually uses is unknown; both are covered because neither costs anything on a label
        /// change that happens when somebody presses a button.</para>
        /// </summary>
        private static void ResizeButtonToLabel(ButtonTextWrapper wrapper)
        {
            var textRect = wrapper.t_text.rectTransform;
            if (textRect == null)
            {
                return;
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

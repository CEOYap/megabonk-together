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
            if (uiAssetService == null || !uiAssetService.TryGetPrefab(PrefabPath, out var prefab))
            {
                Plugin.Log.LogError("[lobby] UI bundle unavailable; the lobby panel cannot be shown.");
                return;
            }

            var canvasObj = GameObject.Find("Canvas");
            if (canvasObj == null)
            {
                Plugin.Log.LogError("[lobby] Canvas not found; the lobby panel cannot be shown.");
                return;
            }

            root = Instantiate(prefab);
            root.name = "MTLobbyPanel";
            root.transform.SetParent(canvasObj.transform, false);

            // In front of the game's menu. The prefab's own Blocker only dims what is behind it if
            // it is actually in front of it.
            root.transform.SetAsLastSibling();

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

            foreach (var label in root.GetComponentsInChildren<TextMeshProUGUI>(true))
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

            if (root != null)
            {
                Destroy(root);
            }

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

            // Destroying the root fires Window.OnDisable, which hands menu focus and the cursor
            // back. Nothing else needs to undo that.
            if (root != null)
            {
                Destroy(root);
                root = null;
            }
        }

        #region Button helpers

        /// <summary>
        /// Clones the game's PLAY button into the prefab's button container.
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

            // The clone carries the menu's own click handler and localisation binding. Both have to
            // go, or pressing this button also does whatever PLAY does and the label is overwritten
            // by the localiser on the next refresh.
            var original = buttonObj.GetComponent<MyButtonNormal>();
            if (original != null)
            {
                Destroy(original);
            }

            var unityButton = buttonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (unityButton != null)
            {
                unityButton.onClick.RemoveAllListeners();
            }

            var localize = buttonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localize != null)
            {
                Destroy(localize);
            }

            var textWrapper = buttonObj.GetComponent<ButtonTextWrapper>();
            if (textWrapper != null && textWrapper.t_text != null)
            {
                textWrapper.t_text.text = label;
            }
            else
            {
                var tmp = buttonObj.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.text = label;
                }
            }

            var button = buttonObj.AddComponent<CustomButton>();
            button.SetOnClickAction(onClick);

            return button;
        }

        private static void SetButtonLabel(CustomButton button, string label)
        {
            if (button == null)
            {
                return;
            }

            var wrapper = button.gameObject.GetComponent<ButtonTextWrapper>();
            if (wrapper != null && wrapper.t_text != null && wrapper.t_text.text != label)
            {
                wrapper.t_text.text = label;
            }
        }

        private static void SetButtonInteractable(CustomButton button, bool interactable)
        {
            if (button == null)
            {
                return;
            }

            button.SetInteractable(interactable);
        }

        private static void SetButtonVisible(CustomButton button, bool visible)
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

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
    /// <para><b>What it replaces.</b> Lobby state used to live in three places at once — a
    /// top-left "Friendlies" banner, a top-right role/code block, and the character-selection
    /// window doing double duty as the lobby — and none of them listed the members. A player who
    /// joined was invisible until the run started. See
    /// <c>docs/ui/00-lobby-panel.md</c>.</para>
    ///
    /// <para><b>Increment 1 of two.</b> This is the panel and the member list: no wire change, the
    /// rows come from the roster that already replicates. Increment 2 adds lobby readiness
    /// (appended union tags, host-authoritative) and moves character selection behind a host-gated
    /// Start. <c>Ready</c> and <c>Start</c> are deliberately absent here rather than present and
    /// inert — a button that does nothing is worse than one that is not there yet.</para>
    ///
    /// <para><b>Invite is absent for a reason, not an oversight.</b> A real invite is a Steam
    /// overlay call against a Steam lobby, which is migration Phase 3. Built now it would be a
    /// second button that copies a code, which <c>Copy Code</c> already does.</para>
    /// </summary>
    internal class LobbyPanel : ModalBase
    {
        /// <summary>
        /// Resolved in <see cref="Awake"/>, never in a static initialiser.
        ///
        /// <para>This was a <c>static readonly</c> field assigned from <c>Plugin.Services</c>, and
        /// it stopped the entire plugin loading. <c>ClassInjector.RegisterTypeInIl2Cpp&lt;T&gt;</c>
        /// runs the type's static constructor, and registration happens in <c>Plugin.Load</c> ~50
        /// lines before the DI host is built — so the cctor dereferenced a null <c>Host</c>, and
        /// because it threw during type initialisation the failure surfaced as
        /// <c>TypeInitializationException</c> out of <c>RegisterTypeInIl2Cpp</c> rather than
        /// anywhere near this file. Every other injected MonoBehaviour in this project resolves in
        /// <c>Awake</c>; that is the reason, not a style preference.</para>
        /// </summary>
        private ILobbyViewService lobbyViewService;

        /// <summary>Tall and narrow: this is a list, and a list reads better than it spreads.</summary>
        protected override Vector2 PanelSize => new(560, 780);

        protected override Color PanelBackgroundColor => new(0.06f, 0.06f, 0.07f, 0.94f);

        /// <summary>
        /// Rebuilt on a timer, never per frame. <see cref="ILobbyViewService.GetMembers"/> allocates
        /// a list and each rebuild touches TMP text, so at 60 Hz this would be exactly the kind of
        /// idle allocation <c>docs/netplay/04-performance-and-gc.md</c> exists to prevent. Twice a
        /// second is faster than anyone can join.
        /// </summary>
        private const float RefreshIntervalSeconds = 0.5f;

        private float refreshAccumulator;

        /// <summary>
        /// Matches every other button in this UI (<c>NetworkMenuTab</c> uses 300x70 at eight sites).
        /// The first version invented 420x48 and spaced rows 52 apart, so real 70px-tall clones
        /// overlapped by 18px each — measure the thing you are cloning.
        /// </summary>
        private const float ButtonWidth = 300f;
        private const float ButtonHeight = 70f;

        /// <summary>Button pitch: the button's own height plus a small gap.</summary>
        private const float ButtonPitch = ButtonHeight + 6f;

        /// <summary>Centre of the first button row.</summary>
        private const float FirstButtonY = -80f;

        private static Vector2 ButtonSlot(int index) => new(0f, FirstButtonY - index * ButtonPitch);

        /// <summary>
        /// Handed in rather than found. <c>NetworkMenuTab</c> does the same: the panel clones one of
        /// MainMenu's buttons for every button it draws, and a scene search per clone is both
        /// wasteful and fragile — it depends on the menu still being the active scene at the moment
        /// the panel happens to build itself.
        /// </summary>
        private MainMenu mainMenu;

        /// <summary>
        /// Main-menu roots hidden while the lobby is open, remembered so they can be put back
        /// exactly as they were. Only ones that were actually active are recorded, so restoring
        /// cannot switch on something the game had deliberately hidden.
        /// </summary>
        private readonly List<GameObject> hiddenMenuRoots = [];

        private TextMeshProUGUI titleText;
        private TextMeshProUGUI codeText;
        private GameObject memberListRoot;
        private readonly List<GameObject> memberRows = [];

        private CustomButton copyCodeButton;
        private CustomButton joinFromClipboardButton;
        private CustomButton leaveLobbyButton;
        private CustomButton readyButton;
        private CustomButton startButton;

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

        /// <summary>
        /// Runs before <c>ModalBase.Start</c>, so the service is available by the time
        /// <see cref="OnUICreated"/> draws the first frame of the panel.
        /// </summary>
        public void Awake()
        {
            lobbyViewService = Plugin.Services.GetService<ILobbyViewService>();

            // Unconditional lifecycle logging, deliberately. Two rounds were spent unable to tell
            // "the panel never ran" from "the panel ran and rendered invisibly", because every log
            // line in here was on a failure branch. A silent success path is exactly what this
            // project's own doctrine warns about: absence of a log line is not absence of the event.
            Plugin.Log.LogInfo($"[lobby] LobbyPanel.Awake; service resolved: {lobbyViewService != null}");
        }

        protected override void OnUICreated()
        {
            Plugin.Log.LogInfo($"[lobby] LobbyPanel.OnUICreated; panel object: {panel != null}, mainMenu: {mainMenu != null}");

            // ModalBase puts its blocker at SetAsFirstSibling — behind everything on the Canvas,
            // including the game's own main menu. That is fine for a modal opened over the menu the
            // mod itself drew, but this panel opens over the game's menu, which then showed through
            // it: PLAY, UNLOCKS and TOGETHER! were all visible and clickable behind the lobby.
            //
            // Re-ordered so the blocker is in front of the menu and the panel in front of the
            // blocker. Order matters: blocker first, then panel, or the panel ends up underneath.
            blocker?.transform.SetAsLastSibling();
            panel?.transform.SetAsLastSibling();

            HideMainMenuChrome();

            EventManager.SubscribeLobbyStartRequestedEvents(OnLobbyStartRequested);

            // The loader and status text ModalBase builds are for connection feedback; the panel
            // starts with neither showing.
            HideLoader();

            CreateTitle();
            CreateCodeLine();
            CreateMemberList();
            CreateButtons();

            Refresh();

            Plugin.Log.LogInfo("[lobby] LobbyPanel built and refreshed.");
        }

        /// <summary>
        /// Clears the main menu so the lobby sits on the empty scene, rather than floating over
        /// PLAY / UNLOCKS / QUESTS / SHOP with the leaderboard and achievement cards still visible.
        ///
        /// <para>Hiding the roots rather than covering them with the blocker: a translucent panel
        /// over a live menu still reads as two screens at once, and the buttons underneath stay
        /// focusable by controller even when they cannot be clicked.</para>
        /// </summary>
        private void HideMainMenuChrome()
        {
            if (mainMenu == null)
            {
                return;
            }

            foreach (var root in new[] { mainMenu.tabMenu, mainMenu.leaderboards, mainMenu.quickQuests })
            {
                if (root != null && root.activeSelf)
                {
                    root.SetActive(false);
                    hiddenMenuRoots.Add(root);
                }
            }
        }

        /// <summary>Puts back exactly what <see cref="HideMainMenuChrome"/> took away.</summary>
        private void RestoreMainMenuChrome()
        {
            foreach (var root in hiddenMenuRoots)
            {
                if (root != null)
                {
                    root.SetActive(true);
                }
            }

            hiddenMenuRoots.Clear();
        }

        private void CreateTitle()
        {
            titleText = CreateLabel("LobbyTitle", new Vector2(0f, 330f), new Vector2(520f, 60f), 40f);
            titleText.text = "Lobby";
            titleText.color = new Color(1f, 0.85f, 0.3f);
        }

        private void CreateCodeLine()
        {
            codeText = CreateLabel("LobbyCode", new Vector2(0f, 284f), new Vector2(520f, 40f), 24f);
            codeText.color = new Color(0.75f, 0.75f, 0.78f);
        }

        private void CreateMemberList()
        {
            memberListRoot = new GameObject("MemberList");
            memberListRoot.transform.SetParent(panel.transform, false);

            var rect = memberListRoot.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, 240f);

            // Six rows at 46px. Stops above the first button row so a full lobby cannot run into it.
            rect.sizeDelta = new Vector2(480f, 260f);
        }

        private void CreateButtons()
        {
            // Cloned from the game's own PLAY button so the panel inherits its art, font and hover
            // behaviour rather than shipping a second visual language. CustomButton is required
            // because Unity Actions do not survive the BepInEx/IL2CPP boundary.
            // Copy and Join-from-clipboard share slot 0: they are mutually exclusive (you either
            // have a lobby or you do not), so hiding one must not leave a gap in the column.
            copyCodeButton = CreateButton("CopyCodeButton", "Copy Code", ButtonSlot(0), OnCopyCodeClicked);
            joinFromClipboardButton = CreateButton("JoinClipboardButton", "Join From Clipboard", ButtonSlot(0), OnJoinFromClipboardClicked);
            readyButton = CreateButton("LobbyReadyButton", "Ready", ButtonSlot(1), OnReadyClicked);
            startButton = CreateButton("LobbyStartButton", "Start", ButtonSlot(2), OnStartClicked);

            // "Back" and "Leave Lobby" would be the same action in this position — the panel only
            // exists while you are in a lobby, so going back IS leaving. Two buttons that do one
            // thing is worse than one that says what it does. A distinct Back returns in increment 2
            // if the flow gains a screen behind this one.
            leaveLobbyButton = CreateButton("LeaveLobbyButton", "Leave Lobby", ButtonSlot(3), OnLeaveLobbyClicked);
        }

        /// <summary>
        /// Rebuilds the member rows. Destroys and recreates rather than diffing: the list is at most
        /// six entries and only redraws twice a second, so a diff would be more code guarding less
        /// cost.
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

            // Copying or leaving is meaningless without a lobby; hide rather than grey out, so the
            // panel never offers an action that cannot work.
            var inLobby = lobbyViewService.IsInLobby;
            SetButtonVisible(copyCodeButton, inLobby && !string.IsNullOrEmpty(code));
            SetButtonVisible(leaveLobbyButton, inLobby);
            SetButtonVisible(joinFromClipboardButton, !inLobby);
            SetButtonVisible(readyButton, inLobby);

            // Start is the host's alone. Shown greyed rather than hidden for the host, so the
            // reason the run has not begun is visible ("everyone is not ready yet") instead of the
            // button simply being missing; hidden entirely for clients, for whom it is not merely
            // disabled but not theirs.
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
                memberRows.Add(CreateMemberRow(members[i], i));
            }
        }

        private GameObject CreateMemberRow(LobbyMemberView member, int index)
        {
            var rowObj = new GameObject($"Member_{member.ConnectionId}");
            rowObj.transform.SetParent(memberListRoot.transform, false);

            var rect = rowObj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -(index * 46f));
            rect.sizeDelta = new Vector2(0f, 42f);

            var label = rowObj.AddComponent<TextMeshProUGUI>();

            // The crown marks the host; "(you)" marks the local player. Both are text rather than
            // icons because there is no avatar or icon source until Steam lobbies land in Phase 3,
            // and a placeholder image would promise something the row cannot yet show.
            // Text, not a glyph. The first version used a crown character the game's font does not
            // contain, so every host row rendered as a tofu box.
            var role = member.IsHost ? " (host)" : "";
            var you = member.IsLocal ? " (you)" : "";

            // Ready state is the thing a player scans this list for, so it gets its own column on
            // the right rather than being folded into the name. Text, not a tick sprite — there is
            // no icon source until Steam lobbies land, and the row already reads left-to-right.
            var ready = member.IsReady ? "READY" : "";

            label.text = $"{member.Name}{role}{you}<pos=76%>{ready}";
            label.alignment = TextAlignmentOptions.Left;
            label.fontSize = 28f;
            label.color = member.IsReady
                ? new Color(0.55f, 0.95f, 0.55f)
                : (member.IsLocal ? new Color(1f, 0.95f, 0.6f) : Color.white);

            return rowObj;
        }

        protected override void Update()
        {
            base.Update();

            refreshAccumulator += Time.unscaledDeltaTime;
            if (refreshAccumulator < RefreshIntervalSeconds)
            {
                return;
            }
            refreshAccumulator = 0f;

            Refresh();
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

            // Redrawn immediately rather than waiting for the refresh tick: on the host the toggle
            // is already applied, and on a client the label still needs to stop saying the thing
            // that was just pressed. The host's broadcast is what actually settles it.
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
        /// advance. One path for both, so the host does not take a different route to the same
        /// screen than the clients do.
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

            // Cleared before invoking: CloseModal destroys this component, and a callback that
            // reopens the panel would otherwise re-enter a half-destroyed object.
            OnClosed = null;

            CloseModal();
            closed?.Invoke();
        }

        public override void OnDestroy()
        {
            // Restored here rather than only in Close(), so a panel torn down by a scene change or
            // a teardown path still gives the menu back instead of leaving the player on a blank
            // screen.
            RestoreMainMenuChrome();

            // Unsubscribed or the delegate keeps this destroyed panel alive and a second lobby
            // would advance twice.
            EventManager.UnsubscribeLobbyStartRequestedEvents(OnLobbyStartRequested);

            foreach (var row in memberRows)
            {
                if (row != null)
                {
                    Destroy(row);
                }
            }
            memberRows.Clear();

            base.OnDestroy();
        }

        #region UI construction helpers

        private TextMeshProUGUI CreateLabel(string name, Vector2 anchoredPosition, Vector2 size, float fontSize)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(panel.transform, false);

            var rect = obj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var label = obj.AddComponent<TextMeshProUGUI>();
            label.text = "";
            label.alignment = TextAlignmentOptions.Center;
            label.fontSize = fontSize;
            label.color = Color.white;

            return label;
        }

        private CustomButton CreateButton(string name, string label, Vector2 anchoredPosition, Action onClick)
        {
            if (mainMenu == null || mainMenu.btnPlay == null)
            {
                Plugin.Log.LogWarning($"[lobby] MainMenu.btnPlay unavailable; '{label}' not created.");
                return null;
            }

            var buttonObj = GameObject.Instantiate(mainMenu.btnPlay.gameObject);
            buttonObj.name = name;
            buttonObj.transform.SetParent(panel.transform, false);

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
                textWrapper.t_text.fontSize = 30;
            }
            else
            {
                var tmp = buttonObj.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.text = label;
                }
            }

            var rect = buttonObj.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = anchoredPosition;
                rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
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

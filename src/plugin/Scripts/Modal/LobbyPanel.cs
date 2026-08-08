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
        private static readonly ILobbyViewService lobbyViewService =
            Plugin.Services.GetService<ILobbyViewService>();

        /// <summary>Tall and narrow: this is a list, and a list reads better than it spreads.</summary>
        protected override Vector2 PanelSize => new(560, 620);

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
        /// Handed in rather than found. <c>NetworkMenuTab</c> does the same: the panel clones one of
        /// MainMenu's buttons for every button it draws, and a scene search per clone is both
        /// wasteful and fragile — it depends on the menu still being the active scene at the moment
        /// the panel happens to build itself.
        /// </summary>
        private MainMenu mainMenu;

        private TextMeshProUGUI titleText;
        private TextMeshProUGUI codeText;
        private GameObject memberListRoot;
        private readonly List<GameObject> memberRows = [];

        private CustomButton copyCodeButton;
        private CustomButton joinFromClipboardButton;
        private CustomButton leaveLobbyButton;
        private CustomButton backButton;

        /// <summary>Set by the caller that opened the panel; runs when Back or Leave is pressed.</summary>
        internal Action OnClosed { get; set; }

        /// <summary>Runs when Leave lobby is pressed, before the panel closes.</summary>
        internal Action OnLeaveRequested { get; set; }

        /// <summary>Runs when Join from clipboard is pressed, with the trimmed clipboard text.</summary>
        internal Action<string> OnJoinRequested { get; set; }

        /// <summary>Call before the panel builds itself — i.e. before the component is enabled.</summary>
        internal void Initialize(MainMenu menu)
        {
            mainMenu = menu;
        }

        protected override void OnUICreated()
        {
            // The loader and status text ModalBase builds are for connection feedback; the panel
            // starts with neither showing.
            HideLoader();

            CreateTitle();
            CreateCodeLine();
            CreateMemberList();
            CreateButtons();

            Refresh();
        }

        private void CreateTitle()
        {
            titleText = CreateLabel("LobbyTitle", new Vector2(0f, 260f), new Vector2(520f, 60f), 40f);
            titleText.text = "Lobby";
            titleText.color = new Color(1f, 0.85f, 0.3f);
        }

        private void CreateCodeLine()
        {
            codeText = CreateLabel("LobbyCode", new Vector2(0f, 212f), new Vector2(520f, 40f), 24f);
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
            rect.anchoredPosition = new Vector2(0f, 180f);
            rect.sizeDelta = new Vector2(480f, 300f);
        }

        private void CreateButtons()
        {
            // Cloned from the game's own PLAY button so the panel inherits its art, font and hover
            // behaviour rather than shipping a second visual language. CustomButton is required
            // because Unity Actions do not survive the BepInEx/IL2CPP boundary.
            copyCodeButton = CreateButton("CopyCodeButton", "Copy Code", new Vector2(0f, -132f), OnCopyCodeClicked);
            joinFromClipboardButton = CreateButton("JoinClipboardButton", "Join From Clipboard", new Vector2(0f, -188f), OnJoinFromClipboardClicked);
            leaveLobbyButton = CreateButton("LeaveLobbyButton", "Leave Lobby", new Vector2(0f, -244f), OnLeaveLobbyClicked);
            backButton = CreateButton("LobbyBackButton", "Back", new Vector2(0f, -300f), OnBackClicked);
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
            var crown = member.IsHost ? "♛ " : "   ";
            var you = member.IsLocal ? "  (you)" : "";

            label.text = $"{crown}{member.Name}{you}";
            label.alignment = TextAlignmentOptions.Left;
            label.fontSize = 28f;
            label.color = member.IsLocal ? new Color(1f, 0.95f, 0.6f) : Color.white;

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

            OnLeaveRequested?.Invoke();
            Close();
        }

        private void OnBackClicked()
        {
            PlaySelectSfx();
            Close();
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
                rect.sizeDelta = new Vector2(420f, 48f);
            }

            var button = buttonObj.AddComponent<CustomButton>();
            button.SetOnClickAction(onClick);

            return button;
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

using Assets.Scripts.Managers;
using HarmonyLib;
using OmniBonk.Common;
using OmniBonk.Common.Models;
using OmniBonk.Configuration;
using OmniBonk.Helpers;
using OmniBonk.Patches.Enemies;
using OmniBonk.Scripts.Button;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Threading.Tasks;
using TextCopy;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(WindowManager))]
    internal static class WindowManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IAutoUpdaterService autoUpdaterService = Plugin.Services.GetService<IAutoUpdaterService>();
        // Must be INetTransport: the inactive transport answers "all ready" from an empty peer set.
        private static readonly INetTransport udpClientService = Plugin.Services.GetService<INetTransport>();
        private static bool hasShownUpdateModal = false;
        private static GameObject friendliesInfoDisplay;
        private static GameObject readyStatusDisplay;
        private static MyButton previousSelectedButton = null;

        /// <summary>Main menu: tear down networking and check for updates. Character menu: friendlies info.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(WindowManager.WindowOpened))]
        public static void WindowOpened_Postfix(Window newWindow)
        {
            if (newWindow.name == "Menu")
            {
                EnemyPatch.EnemiesDistanceThrottler.ClearAll();
                Plugin.Instance.NetworkHandler.ResetNetworking();
                Plugin.Instance.NetPlayersDisplayer.ResetCards();
                Plugin.Instance.HideModal();
                Plugin.Instance.ResetWorldSize();
                Plugin.Instance.CameraSwitcher.ResetToLocalPlayer();
                Plugin.Instance.ClearPrefabs();
                Plugin.Instance.RestoreDeath(false);

                // The session just ended; destroying the panel is its whole teardown (OnDestroy).
                var lobbyPanel = Scripts.Modal.LobbyPanel.Current;
                if (lobbyPanel != null)
                {
                    GameObject.Destroy(lobbyPanel.gameObject);
                }

                DestroyFriendliesInfoDisplay();

                if (SpawnPlayerPortalPatches.WaitForLobbyCoroutine != null)
                {
                    CoroutineRunner.Instance.Stop(SpawnPlayerPortalPatches.WaitForLobbyCoroutine);
                }

                if (autoUpdaterService.IsCustomBuild() && hasShownUpdateModal) return;

                // FIX P2-4: respect the auto-update setting.
                if (!ModConfig.CheckForUpdates.Value) return;

                Task.Run(async () =>
                {
                    await autoUpdaterService.CheckAndUpdate();

                    if (autoUpdaterService.IsAnUpdateAvailable())
                    {
                        Plugin.ShowUpdateAvailableModal();

                        if (autoUpdaterService.IsCustomBuild())
                        {
                            hasShownUpdateModal = true;
                        }

                        if (Plugin.Instance.PlayTogetherButton == null)
                        {
                            return;
                        }
                        Plugin.Instance.PlayTogetherButton.enabled = false;
                    }
                });
            }

            if (newWindow.name == "W_Character")
            {
                if (Plugin.Instance.Mode.Mode == NetworkModeType.Friendlies)
                {
                    var mainMenu = Plugin.Instance.GetMainMenu();
                    CreateFriendliesInfoDisplay(mainMenu.tabCharacters.transform, newWindow);
                }
            }
            else
            {
                if (friendliesInfoDisplay)
                {
                    DestroyFriendliesInfoDisplay();
                }
            }
        }

        /// <summary>
        /// Re-enables the confirm button on close. Null-guarded (buttons may be torn down). Uses
        /// <see cref="WindowCast.AsCharacterMenu"/>: a bare <c>as</c> fails on a plain Il2Cpp <c>Window</c> wrapper.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(WindowManager.WindowClosed))]
        public static void WindowClosed_Postfix(Window closedWindow)
        {
            if (closedWindow == null
                || closedWindow.name != "W_Character"
                || Plugin.Instance.Mode.Mode != NetworkModeType.Friendlies)
            {
                return;
            }

            var character = WindowCast.AsCharacterMenu(closedWindow);
            if (character == null || character.b_confirm == null)
            {
                return;
            }

            character.b_confirm.state = MyButton.EButtonState.Active;
            character.b_confirm.RefreshState();
        }

        /// <summary>Friendlies host: disable confirm buttons until 2+ players are present and ready.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(WindowManager.Update))]
        public static void Update_Postix()
        {
            if (ButtonManager.selectedButton2 == Plugin.Instance.PlayTogetherButton && WindowManager.activeWindow?.name == "W_Character")
            {
                Plugin.Log.LogInfo("Resetting selected button");
                ButtonManager.selectedButton2 = WindowManager.activeWindow.allButtons.Count > 0 ? WindowManager.activeWindow.allButtons[0] : null;
            }

            if (WindowManager.activeWindow?.name == "W_Character" && Plugin.Instance.Mode.Mode == NetworkModeType.Friendlies && Plugin.Instance.Mode.Role == Role.Host)
            {
                // Per frame: must not throw.
                var character = WindowCast.AsCharacterMenu(WindowManager.activeWindow);
                if (character != null && character.b_confirm != null)
                {
                    character.b_confirm.state = playerManagerService.GetAllPlayers().Count() < 2
                        ? MyButton.EButtonState.Inactive
                        : MyButton.EButtonState.Active;
                    character.b_confirm.RefreshState();
                }
            }

            if (WindowManager.activeWindow?.name == "Maps And Stats")
            {
                if (Plugin.Instance.Mode.Mode == NetworkModeType.Friendlies && Plugin.Instance.Mode.Role == Role.Host)
                {
                    var mainMenu = Plugin.Instance.GetMainMenu();

                    var confirmButton = mainMenu.mapSelectionUi.btnConfirm;

                    if (confirmButton == null) return;

                    if (playerManagerService.GetAllPlayers().Count() < 2 || !udpClientService.AreAllPeersReady() || udpClientService.IsHandlingConnection())
                    {
                        confirmButton.state = MyButton.EButtonState.Inactive;
                        confirmButton.RefreshState();

                        if (!readyStatusDisplay)
                        {
                            CreateReadyStatusDisplay(mainMenu.tabMaps.transform);
                        }

                        UpdateReadyStatusDisplay();
                    }
                    else
                    {
                        confirmButton.state = MyButton.EButtonState.Active;
                        confirmButton.RefreshState();

                        DestroyReadyStatusDisplay();
                    }
                }
                else
                {
                    if (readyStatusDisplay)
                    {
                        DestroyReadyStatusDisplay();
                    }
                }
            }
        }

        private static void CreateFriendliesInfoDisplay(Transform parent, Window newWindow)
        {
            var menu = WindowCast.AsCharacterMenu(newWindow);
            var btn = menu?.b_confirm;
            DestroyFriendliesInfoDisplay();

            var mainMenu = Plugin.Instance.GetMainMenu();

            friendliesInfoDisplay = new GameObject("FriendliesInfoDisplay");
            friendliesInfoDisplay.transform.SetParent(parent, false);

            var rectTransform = friendliesInfoDisplay.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(1f, 1f);
            rectTransform.anchorMax = new Vector2(1f, 1f);
            rectTransform.pivot = new Vector2(1f, 1f);
            rectTransform.anchoredPosition = new Vector2(-250f, -50f);
            rectTransform.sizeDelta = new Vector2(300f, 120f);

            friendliesInfoDisplay.transform.SetAsLastSibling();

            var mode = Plugin.Instance.Mode;
            var roleText = mode.Role.ToString();

            var textObj = new GameObject("InfoText");
            textObj.transform.SetParent(friendliesInfoDisplay.transform, false);

            var textRectTransform = textObj.AddComponent<RectTransform>();
            textRectTransform.anchorMin = new Vector2(0f, 1f);
            textRectTransform.anchorMax = new Vector2(1f, 1f);
            textRectTransform.pivot = new Vector2(1f, 1f);
            textRectTransform.anchoredPosition = new Vector2(0f, 40f);
            textRectTransform.sizeDelta = new Vector2(0f, 250f);

            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.text = $"Role: {roleText}\nCode: ******";
            var sharedExpValue = Plugin.Instance.Mode.Role == Role.Host ? ModConfig.EnabledSharedExperience.Value : Plugin.Instance.Mode.EnabledSharedExperience;
            if (sharedExpValue.HasValue)
            {
                var sharedExpStatus = sharedExpValue.Value
                ? "<color=green>ON</color>"
                : "<color=red>OFF</color>";

                text.text += $"\nShared Experience: {sharedExpStatus}";
            }
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.fontSize = 36;
            text.color = Color.white;

            // The copy button is cloned from the confirm button; without one, show the text alone.
            if (Plugin.Instance.Mode.Role != Role.Host || btn == null)
            {
                return;
            }

            var copyButtonObj = GameObject.Instantiate(btn.gameObject);
            copyButtonObj.name = "CopyCodeButton";
            copyButtonObj.transform.SetParent(friendliesInfoDisplay.transform, false);
            copyButtonObj.SetActive(true);

            var style = ButtonStyle.CaptureAndRemove(copyButtonObj);

            UnityEngine.UI.Button button = copyButtonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (button != null)
            {
                button.onClick = new();
            }

            var localizeStringEvent = copyButtonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEvent != null)
            {
                UnityEngine.Object.DestroyImmediate(localizeStringEvent);
            }

            var copyButton = copyButtonObj.AddComponent<CustomButton>();
            style.ApplyTo(copyButton);
            copyButton.SetOnClickAction(OnCopyCodeClicked);
            copyButton.OverrideStartHoverAction(() =>
            {
                previousSelectedButton = ButtonManager.selectedButton2;
            });
            copyButton.OverrideEndHoverAction(() =>
            {
                ButtonManager.selectedButton2 = previousSelectedButton;
                previousSelectedButton = null;
            });

            var textWrapper = copyButtonObj.GetComponent<ButtonTextWrapper>();
            textWrapper.t_text.text = "Copy Code";
            textWrapper.t_text.fontSize = 25;

            var copyButtonRect = copyButtonObj.GetComponent<RectTransform>();
            copyButtonRect.anchorMin = new Vector2(1f, 0f);
            copyButtonRect.anchorMax = new Vector2(1f, 0f);
            copyButtonRect.pivot = new Vector2(1f, 0f);
            copyButtonRect.anchoredPosition = new Vector2(0f, 10f);
            copyButtonRect.sizeDelta = new Vector2(200f, 40f);

            copyButtonObj.transform.SetAsLastSibling();

            copyButton.state = MyButton.EButtonState.Active;
            copyButton.RefreshState();
        }

        private static void OnCopyCodeClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);

            try
            {
                ClipboardService.SetText(Plugin.Instance.Mode.RoomCode);
                Plugin.Log.LogInfo("Room code copied to clipboard");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Failed to copy to clipboard: {ex.Message}");
            }
        }

        private static void DestroyFriendliesInfoDisplay()
        {
            if (friendliesInfoDisplay)
            {
                GameObject.Destroy(friendliesInfoDisplay);
                friendliesInfoDisplay = null;
            }
        }

        private static void CreateReadyStatusDisplay(Transform parent)
        {
            DestroyReadyStatusDisplay();

            readyStatusDisplay = new GameObject("ReadyStatusDisplay");
            readyStatusDisplay.transform.SetParent(parent, false);

            var rectTransform = readyStatusDisplay.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(1f, 1f);
            rectTransform.anchorMax = new Vector2(1f, 1f);
            rectTransform.pivot = new Vector2(1f, 1f);
            rectTransform.anchoredPosition = new Vector2(-200f, -40f);
            rectTransform.sizeDelta = new Vector2(400f, 80f);

            readyStatusDisplay.transform.SetAsLastSibling();

            var textObj = new GameObject("StatusText");
            textObj.transform.SetParent(readyStatusDisplay.transform, false);

            var textRectTransform = textObj.AddComponent<RectTransform>();
            textRectTransform.anchorMin = new Vector2(0f, 1f);
            textRectTransform.anchorMax = new Vector2(1f, 1f);
            textRectTransform.pivot = new Vector2(1f, 1f);
            textRectTransform.anchoredPosition = new Vector2(0f, 0f);
            textRectTransform.sizeDelta = new Vector2(0f, 80f);

            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.text = "Waiting for players to be ready...";
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.TopRight;
            text.fontSize = 36;
        }

        private static void UpdateReadyStatusDisplay()
        {
            if (!readyStatusDisplay) return;

            var textObj = readyStatusDisplay.transform.Find("StatusText");
            if (textObj == null) return;

            var text = textObj.GetComponent<TextMeshProUGUI>();
            if (text == null) return;

            int readyCount = udpClientService.GetCurrentReadyPeersCount();
            int totalCount = playerManagerService.GetAllPlayers().Count() - 1;

            text.text = $"Waiting for players to be ready...\nReady: {readyCount}/{totalCount}";
        }

        private static void DestroyReadyStatusDisplay()
        {
            if (readyStatusDisplay)
            {
                GameObject.Destroy(readyStatusDisplay);
                readyStatusDisplay = null;
            }
        }
    }
}

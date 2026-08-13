using Assets.Scripts.Settings___Saves.SaveFiles;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Configuration;
using MegabonkTogether.Helpers;
using MegabonkTogether.Scripts.Button;
using MegabonkTogether.Scripts.Modal;
using Microsoft.Extensions.DependencyInjection;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace MegabonkTogether.Scripts
{
    internal class NetworkMenuTab : ModalBase
    {
        private CustomButton randomButton;
        private CustomButton friendliesButton;
        private CustomButton closeButton;
        private CustomButton stopButton;
        private TMP_InputField playerNameInput;
        private MainMenu mainMenu;
        private GameObject label;
        private bool wasInputFocused;
        private Coroutine connectionCoroutine;
        private ProfanityFilter.ProfanityFilter filter;

        private GameObject friendliesTitle;
        private CustomButton hostButton;
        private CustomButton joinButton;
        private CustomButton friendliesBackButton;
        private TMP_InputField codeInput;
        private GameObject codeLabel;

        private GameObject saveToggleSetting;
        private CustomButton saveToggleLeftButton;
        private CustomButton saveToggleRightButton;
        private TextMeshProUGUI saveToggleStatusText;

        private CustomButton netplayOptionsButton;
        private GameObject netplayOptionsTitle;
        private CustomButton netplayOptionsBackButton;
        private GameObject sharedExpToggleSetting;
        private CustomButton sharedExpToggleLeftButton;
        private CustomButton sharedExpToggleRightButton;
        private TextMeshProUGUI sharedExpToggleStatusText;

        /// <summary>
        /// Resolved in Awake, never in a static initialiser — RegisterTypeInIl2Cpp runs the type's
        /// static constructor during Plugin.Load, before the DI host exists.
        /// </summary>
        private Services.SteamInviteService steamInviteService;

        protected void Awake()
        {
            filter = new ProfanityFilter.ProfanityFilter();
            steamInviteService = Plugin.Services.GetService<Services.SteamInviteService>();
        }

        public void SetMainMenu(MainMenu menu)
        {
            mainMenu = menu;
        }

        protected override void OnUICreated()
        {
            CreateCloseButton();
            CreatePlayerNameInput();
            CreateMatchButtons();
            CreateStopButton();
            CreateFriendliesUI();
            CreateNetplayOptionsUI();

            TryJoinFromInvite();
        }

        /// <summary>
        /// If a Steam invite is waiting, join it now rather than leaving the player to find the
        /// pre-filled code themselves.
        ///
        /// <para>Here, at the end of <see cref="OnUICreated"/>, because everything
        /// <see cref="JoinWithCode"/> touches — the code field, the loader, the friendlies panel —
        /// has just been built. Running it any earlier dereferences a half-made screen.</para>
        ///
        /// <para>The invite is consumed, so the pre-fill in <see cref="UpdateFriendliesUI"/> will
        /// not offer it a second time. The two paths cover different orderings: this one when the
        /// menu opens <i>because</i> an invite arrived, the pre-fill when an invite arrives while
        /// the menu is already open.</para>
        /// </summary>
        private void TryJoinFromInvite()
        {
            var invited = steamInviteService?.ConsumeJoinCode();
            if (string.IsNullOrEmpty(invited))
            {
                return;
            }

            Plugin.Log.LogInfo($"[steam-invite] Joining room {invited} from an invite.");
            JoinWithCode(invited);
        }

        private void CreateCloseButton()
        {
            var buttonObj = GameObject.Instantiate(mainMenu.btnPlay.gameObject);
            buttonObj.transform.SetParent(panel.transform, false);

            var originalButton = buttonObj.GetComponent<MyButtonNormal>();
            if (originalButton != null)
            {
                UnityEngine.Object.DestroyImmediate(originalButton);
            }

            UnityEngine.UI.Button button = buttonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (button != null)
            {
                button.onClick = new();
            }

            var localizeStringEvent = buttonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEvent != null)
            {
                UnityEngine.Object.DestroyImmediate(localizeStringEvent);
            }

            closeButton = buttonObj.AddComponent<CustomButton>();
            closeButton.SetOnClickAction(OnCloseClicked);

            var textWrapper = buttonObj.GetComponent<ButtonTextWrapper>();
            textWrapper.t_text.text = "Close";
            textWrapper.t_text.fontSize = 36;

            var rectTransform = buttonObj.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.anchoredPosition = new Vector2(0, -200f);
            rectTransform.sizeDelta = new Vector2(300, 70);
        }

        private void CreatePlayerNameInput()
        {
            var inputObj = new GameObject("PlayerNameInput");
            inputObj.transform.SetParent(panel.transform, false);

            var rectTransform = inputObj.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0.7f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.7f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.anchoredPosition = Vector2.zero;
            rectTransform.sizeDelta = new Vector2(300, 50);

            var image = inputObj.AddComponent<Image>();
            image.color = new Color(0.2f, 0.2f, 0.2f, 1f);

            playerNameInput = inputObj.AddComponent<TMP_InputField>();
            playerNameInput.textComponent = CreateInputText(inputObj);
            playerNameInput.placeholder = CreatePlaceholderText(inputObj);
            playerNameInput.text = ModConfig.PlayerName.Value;
            playerNameInput.characterLimit = 20;

            playerNameInput.caretWidth = 3;
            playerNameInput.caretColor = Color.white;
            playerNameInput.customCaretColor = true;
            playerNameInput.caretBlinkRate = 0.85f;

            playerNameInput.selectionColor = new Color(0.65f, 0.8f, 1f, 0.5f);

            // Enable/disable to force caret initialization (Thanks random user on Stack exchange)
            playerNameInput.enabled = false;
            playerNameInput.enabled = true;

            label = new GameObject("Label");
            label.transform.SetParent(panel.transform, false);

            var labelRect = label.AddComponent<RectTransform>();
            labelRect.anchorMin = new Vector2(0.5f, 0.75f);
            labelRect.anchorMax = new Vector2(0.5f, 0.75f);
            labelRect.pivot = new Vector2(0.5f, 0.5f);
            labelRect.anchoredPosition = new Vector2(0, 25);
            labelRect.sizeDelta = new Vector2(300, 40);

            var labelText = label.AddComponent<TextMeshProUGUI>();
            labelText.text = "Player Name:";
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.fontSize = 50;
            labelText.color = Color.white;
        }

        private void CreateSaveToggle()
        {
            var settings = mainMenu.settings.GetComponent<Settings>();
            var settingPrefab = settings.GetSettingPrefab(SettingType.Enum);

            saveToggleSetting = GameObject.Instantiate(settingPrefab, panel.transform);

            var rectTransform = saveToggleSetting.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0.6f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.6f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.anchoredPosition = new Vector2(0, 25);
            rectTransform.sizeDelta = new Vector2(700, 60);

            var textComponents = Il2CppFindHelper.RuntimeGetComponentsInChildren<TextMeshProUGUI>(saveToggleSetting);
            foreach (var textComp in textComponents)
            {
                if (textComp.name.StartsWith("Text"))
                {
                    textComp.text = "Allow Saving progression\nUse at your own risk";
                    textComp.fontSize = 20;
                    textComp.enableWordWrapping = false;
                }
                else if (textComp.name.StartsWith("StatusText"))
                {
                    saveToggleStatusText = textComp;
                    saveToggleStatusText.gameObject.SetActive(true);
                    UpdateSaveToggleStatus();
                }
            }

            var buttons = Il2CppFindHelper.RuntimeGetComponentsInChildren<UnityEngine.UI.Button>(saveToggleSetting);
            foreach (var btn in buttons)
            {
                if (btn.name == "B_Left")
                {
                    var origButton = btn.GetComponent<MyButtonNormal>();
                    if (origButton != null)
                    {
                        UnityEngine.Object.DestroyImmediate(origButton);
                    }

                    btn.onClick = new();

                    saveToggleLeftButton = btn.gameObject.AddComponent<CustomButton>();
                    saveToggleLeftButton.SetOnClickAction(OnSaveToggleLeftClicked);
                }
                else if (btn.name == "B_Right")
                {
                    var origButton = btn.GetComponent<MyButtonNormal>();
                    if (origButton != null)
                    {
                        UnityEngine.Object.DestroyImmediate(origButton);
                    }

                    btn.onClick = new();

                    saveToggleRightButton = btn.gameObject.AddComponent<CustomButton>();
                    saveToggleRightButton.SetOnClickAction(OnSaveToggleRightClicked);
                }
            }

            saveToggleSetting.SetActive(false);
        }

        private void OnSaveToggleLeftClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiClick.sounds[0]);
            ToggleSaveOption(false);
        }

        private void OnSaveToggleRightClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiClick.sounds[0]);
            ToggleSaveOption(true);
        }

        private void ToggleSaveOption(bool isEnabled)
        {
            ModConfig.AllowSavesDuringNetplay.Value = isEnabled;
            ModConfig.Save();
            UpdateSaveToggleStatus();
        }

        private void UpdateSaveToggleStatus()
        {
            if (saveToggleStatusText != null)
            {
                saveToggleStatusText.text = ModConfig.AllowSavesDuringNetplay.Value ? "ON" : "OFF";
                saveToggleStatusText.color = ModConfig.AllowSavesDuringNetplay.Value ? Color.green : Color.red;
            }
        }

        private void CreateSharedExpToggle()
        {
            var settings = mainMenu.settings.GetComponent<Settings>();
            var settingPrefab = settings.GetSettingPrefab(SettingType.Enum);

            sharedExpToggleSetting = GameObject.Instantiate(settingPrefab, panel.transform);

            var rectTransform = sharedExpToggleSetting.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.anchoredPosition = new Vector2(0, -50);
            rectTransform.sizeDelta = new Vector2(700, 60);

            var textComponents = Il2CppFindHelper.RuntimeGetComponentsInChildren<TextMeshProUGUI>(sharedExpToggleSetting);
            foreach (var textComp in textComponents)
            {
                if (textComp.name.StartsWith("Text"))
                {
                    textComp.text = "Shared Experience(Experimental)\nXP/Gold/Interaction shared\nActive pause enabled";
                    textComp.fontSize = 18;
                    textComp.enableWordWrapping = false;
                }
                else if (textComp.name.StartsWith("StatusText"))
                {
                    sharedExpToggleStatusText = textComp;
                    sharedExpToggleStatusText.gameObject.SetActive(true);
                    UpdateSharedExpToggleStatus();
                }
            }

            var buttons = Il2CppFindHelper.RuntimeGetComponentsInChildren<UnityEngine.UI.Button>(sharedExpToggleSetting);
            foreach (var btn in buttons)
            {
                if (btn.name == "B_Left")
                {
                    var origButton = btn.GetComponent<MyButtonNormal>();
                    if (origButton != null)
                    {
                        UnityEngine.Object.DestroyImmediate(origButton);
                    }

                    btn.onClick = new();

                    sharedExpToggleLeftButton = btn.gameObject.AddComponent<CustomButton>();
                    sharedExpToggleLeftButton.SetOnClickAction(OnSharedExpToggleLeftClicked);
                }
                else if (btn.name == "B_Right")
                {
                    var origButton = btn.GetComponent<MyButtonNormal>();
                    if (origButton != null)
                    {
                        UnityEngine.Object.DestroyImmediate(origButton);
                    }

                    btn.onClick = new();

                    sharedExpToggleRightButton = btn.gameObject.AddComponent<CustomButton>();
                    sharedExpToggleRightButton.SetOnClickAction(OnSharedExpToggleRightClicked);
                }
            }

            sharedExpToggleSetting.SetActive(false);
        }

        private void OnSharedExpToggleLeftClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiClick.sounds[0]);
            ToggleSharedExpOption(false);
        }

        private void OnSharedExpToggleRightClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiClick.sounds[0]);
            ToggleSharedExpOption(true);
        }

        private void ToggleSharedExpOption(bool isEnabled)
        {
            ModConfig.EnabledSharedExperience.Value = isEnabled;
            ModConfig.Save();
            UpdateSharedExpToggleStatus();
        }

        private void UpdateSharedExpToggleStatus()
        {
            if (sharedExpToggleStatusText != null)
            {
                sharedExpToggleStatusText.text = ModConfig.EnabledSharedExperience.Value ? "ON" : "OFF";
                sharedExpToggleStatusText.color = ModConfig.EnabledSharedExperience.Value ? Color.green : Color.red;
            }
        }

        private void CreateNetplayOptionsUI()
        {
            netplayOptionsTitle = new GameObject("NetplayOptionsTitle");
            netplayOptionsTitle.transform.SetParent(panel.transform, false);

            var titleRect = netplayOptionsTitle.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 0.85f);
            titleRect.anchorMax = new Vector2(0.5f, 0.85f);
            titleRect.pivot = new Vector2(0.5f, 0.5f);
            titleRect.anchoredPosition = Vector2.zero;
            titleRect.sizeDelta = new Vector2(400, 60);

            var titleText = netplayOptionsTitle.AddComponent<TextMeshProUGUI>();
            titleText.text = "Netplay Options";
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.fontSize = 60;
            titleText.color = Color.white;

            netplayOptionsTitle.SetActive(false);

            CreateSaveToggle();
            CreateSharedExpToggle();

            var backButtonObj = GameObject.Instantiate(mainMenu.btnPlay.gameObject);
            backButtonObj.transform.SetParent(panel.transform, false);

            var originalBackButton = backButtonObj.GetComponent<MyButtonNormal>();
            if (originalBackButton != null)
            {
                UnityEngine.Object.DestroyImmediate(originalBackButton);
            }

            UnityEngine.UI.Button backButton = backButtonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (backButton != null)
            {
                backButton.onClick = new();
            }

            var localizeStringEventBack = backButtonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEventBack != null)
            {
                UnityEngine.Object.DestroyImmediate(localizeStringEventBack);
            }

            netplayOptionsBackButton = backButtonObj.AddComponent<CustomButton>();
            netplayOptionsBackButton.SetOnClickAction(OnNetplayOptionsBackClicked);

            var backTextWrapper = backButtonObj.GetComponent<ButtonTextWrapper>();
            backTextWrapper.t_text.text = "Back";
            backTextWrapper.t_text.fontSize = 36;

            var backRectTransform = backButtonObj.GetComponent<RectTransform>();
            backRectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            backRectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            backRectTransform.pivot = new Vector2(0.5f, 0.5f);
            backRectTransform.anchoredPosition = new Vector2(0, -200f);
            backRectTransform.sizeDelta = new Vector2(300, 70);

            netplayOptionsBackButton.gameObject.SetActive(false);
        }

        private TextMeshProUGUI CreateInputText(GameObject parent)
        {
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(parent.transform, false);

            var rectTransform = textObj.AddComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.sizeDelta = Vector2.zero;
            rectTransform.anchoredPosition = Vector2.zero;

            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.alignment = TextAlignmentOptions.Left;
            text.verticalAlignment = VerticalAlignmentOptions.Middle;
            text.fontSize = 35;
            text.color = Color.white;
            text.margin = new Vector4(10, 0, 10, 0);

            return text;
        }

        private TextMeshProUGUI CreatePlaceholderText(GameObject parent)
        {
            var placeholderObj = new GameObject("Placeholder");
            placeholderObj.transform.SetParent(parent.transform, false);

            var rectTransform = placeholderObj.AddComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.sizeDelta = Vector2.zero;
            rectTransform.anchoredPosition = Vector2.zero;

            var text = placeholderObj.AddComponent<TextMeshProUGUI>();
            text.text = "Enter your name...";
            text.alignment = TextAlignmentOptions.Left;
            text.verticalAlignment = VerticalAlignmentOptions.Middle;
            text.fontSize = 24;
            text.color = new Color(1f, 1f, 1f, 0.3f);
            text.margin = new Vector4(10, 0, 10, 0);

            return text;
        }

        private void CreateStopButton()
        {
            var buttonObj = GameObject.Instantiate(mainMenu.btnPlay.gameObject);
            buttonObj.transform.SetParent(panel.transform, false);

            var originalButton = buttonObj.GetComponent<MyButtonNormal>();
            if (originalButton != null)
            {
                UnityEngine.Object.DestroyImmediate(originalButton);
            }

            UnityEngine.UI.Button button = buttonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (button != null)
            {
                button.onClick = new();
            }

            var localizeStringEvent = buttonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEvent != null)
            {
                UnityEngine.Object.DestroyImmediate(localizeStringEvent);
            }

            stopButton = buttonObj.AddComponent<CustomButton>();
            stopButton.SetOnClickAction(OnStopClicked);

            var textWrapper = buttonObj.GetComponent<ButtonTextWrapper>();
            textWrapper.t_text.fontSize = 36;

            var rectTransform = buttonObj.GetComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.anchoredPosition = new Vector2(0, -220f);
            rectTransform.sizeDelta = new Vector2(300, 70);

            stopButton.gameObject.SetActive(false);
        }

        private void CreateMatchButtons()
        {
            var randomButtonObj = GameObject.Instantiate(mainMenu.btnPlay.gameObject);
            randomButtonObj.transform.SetParent(panel.transform, false);

            var originalRandomButton = randomButtonObj.GetComponent<MyButtonNormal>();
            if (originalRandomButton != null)
            {
                UnityEngine.Object.DestroyImmediate(originalRandomButton);
            }

            UnityEngine.UI.Button button = randomButtonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (button != null)
            {
                button.onClick = new();
            }

            var localizeStringEvent = randomButtonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEvent != null)
            {
                UnityEngine.Object.DestroyImmediate(localizeStringEvent);
            }

            randomButton = randomButtonObj.AddComponent<CustomButton>();
            randomButton.SetOnClickAction(OnRandomClicked);

            var randomTextWrapper = randomButtonObj.GetComponent<ButtonTextWrapper>();
            randomTextWrapper.t_text.text = "Random";
            randomTextWrapper.t_text.fontSize = 36;

            var randomRectTransform = randomButtonObj.GetComponent<RectTransform>();
            randomRectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            randomRectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            randomRectTransform.pivot = new Vector2(0.5f, 0.5f);
            randomRectTransform.anchoredPosition = new Vector2(-180f, -100f);
            randomRectTransform.sizeDelta = new Vector2(300, 70);

            var friendliesButtonObj = GameObject.Instantiate(mainMenu.btnPlay.gameObject);
            friendliesButtonObj.transform.SetParent(panel.transform, false);

            var originalFriendliesButton = friendliesButtonObj.GetComponent<MyButtonNormal>();
            if (originalFriendliesButton != null)
            {
                UnityEngine.Object.DestroyImmediate(originalFriendliesButton);
            }

            UnityEngine.UI.Button butt = friendliesButtonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (butt != null)
            {
                butt.onClick = new();
            }

            var localizeStringEventFriendlies = friendliesButtonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEventFriendlies != null)
            {
                UnityEngine.Object.DestroyImmediate(localizeStringEventFriendlies);
            }

            friendliesButton = friendliesButtonObj.AddComponent<CustomButton>();
            friendliesButton.SetOnClickAction(OnFriendliesClicked);

            var friendliesTextWrapper = friendliesButtonObj.GetComponent<ButtonTextWrapper>();
            friendliesTextWrapper.t_text.text = "Friendlies";
            friendliesTextWrapper.t_text.fontSize = 36;

            var friendliesRectTransform = friendliesButtonObj.GetComponent<RectTransform>();
            friendliesRectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            friendliesRectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            friendliesRectTransform.pivot = new Vector2(0.5f, 0.5f);
            friendliesRectTransform.anchoredPosition = new Vector2(180f, -100f);
            friendliesRectTransform.sizeDelta = new Vector2(300, 70);

            var optionsButtonObj = GameObject.Instantiate(mainMenu.btnPlay.gameObject);
            optionsButtonObj.transform.SetParent(panel.transform, false);

            var originalOptionsButton = optionsButtonObj.GetComponent<MyButtonNormal>();
            if (originalOptionsButton != null)
            {
                UnityEngine.Object.DestroyImmediate(originalOptionsButton);
            }

            UnityEngine.UI.Button optBtn = optionsButtonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (optBtn != null)
            {
                optBtn.onClick = new();
            }

            var localizeStringEventOptions = optionsButtonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEventOptions != null)
            {
                UnityEngine.Object.DestroyImmediate(localizeStringEventOptions);
            }

            netplayOptionsButton = optionsButtonObj.AddComponent<CustomButton>();
            netplayOptionsButton.SetOnClickAction(OnNetplayOptionsClicked);

            var optionsTextWrapper = optionsButtonObj.GetComponent<ButtonTextWrapper>();
            optionsTextWrapper.t_text.text = "Netplay Options";
            optionsTextWrapper.t_text.fontSize = 30;

            var optionsRectTransform = optionsButtonObj.GetComponent<RectTransform>();
            optionsRectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            optionsRectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            optionsRectTransform.pivot = new Vector2(0.5f, 0.5f);
            optionsRectTransform.anchoredPosition = new Vector2(0, -10f);
            optionsRectTransform.sizeDelta = new Vector2(300, 70);
        }

        private void CreateFriendliesUI()
        {
            friendliesTitle = new GameObject("FriendliesTitle");
            friendliesTitle.transform.SetParent(panel.transform, false);

            var titleRect = friendliesTitle.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.5f, 0.85f);
            titleRect.anchorMax = new Vector2(0.5f, 0.85f);
            titleRect.pivot = new Vector2(0.5f, 0.5f);
            titleRect.anchoredPosition = Vector2.zero;
            titleRect.sizeDelta = new Vector2(400, 60);

            var titleText = friendliesTitle.AddComponent<TextMeshProUGUI>();
            titleText.text = "Friendlies";
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.fontSize = 60;
            titleText.color = Color.white;

            friendliesTitle.SetActive(false);

            var hostButtonObj = GameObject.Instantiate(mainMenu.btnPlay.gameObject);
            hostButtonObj.transform.SetParent(panel.transform, false);

            var originalHostButton = hostButtonObj.GetComponent<MyButtonNormal>();
            if (originalHostButton != null)
            {
                UnityEngine.Object.DestroyImmediate(originalHostButton);
            }

            UnityEngine.UI.Button button = hostButtonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (button != null)
            {
                button.onClick = new();
            }

            var localizeStringEvent = hostButtonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEvent != null)
            {
                UnityEngine.Object.DestroyImmediate(localizeStringEvent);
            }

            hostButton = hostButtonObj.AddComponent<CustomButton>();
            hostButton.SetOnClickAction(OnHostClicked);

            var hostTextWrapper = hostButtonObj.GetComponent<ButtonTextWrapper>();
            hostTextWrapper.t_text.text = "Host";
            hostTextWrapper.t_text.fontSize = 36;

            var hostRectTransform = hostButtonObj.GetComponent<RectTransform>();
            hostRectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            hostRectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            hostRectTransform.pivot = new Vector2(0.5f, 0.5f);
            hostRectTransform.anchoredPosition = new Vector2(0, 50f);
            hostRectTransform.sizeDelta = new Vector2(300, 70);

            hostButton.gameObject.SetActive(false);

            codeLabel = new GameObject("CodeLabel");
            codeLabel.transform.SetParent(panel.transform, false);

            var codeLabelRect = codeLabel.AddComponent<RectTransform>();
            codeLabelRect.anchorMin = new Vector2(0.5f, 0.4f);
            codeLabelRect.anchorMax = new Vector2(0.5f, 0.4f);
            codeLabelRect.pivot = new Vector2(0.5f, 0.5f);
            codeLabelRect.anchoredPosition = new Vector2(0, 25);
            codeLabelRect.sizeDelta = new Vector2(300, 40);

            var codeLabelText = codeLabel.AddComponent<TextMeshProUGUI>();
            codeLabelText.text = "Room Code:";
            codeLabelText.alignment = TextAlignmentOptions.Center;
            codeLabelText.fontSize = 40;
            codeLabelText.color = Color.white;

            codeLabel.SetActive(false);

            var codeInputObj = new GameObject("CodeInput");
            codeInputObj.transform.SetParent(panel.transform, false);

            var codeInputRect = codeInputObj.AddComponent<RectTransform>();
            codeInputRect.anchorMin = new Vector2(0.5f, 0.35f);
            codeInputRect.anchorMax = new Vector2(0.5f, 0.35f);
            codeInputRect.pivot = new Vector2(0.5f, 0.5f);
            codeInputRect.anchoredPosition = new Vector2(-80f, -20f);
            codeInputRect.sizeDelta = new Vector2(250, 50);

            var codeInputImage = codeInputObj.AddComponent<Image>();
            codeInputImage.color = new Color(0.2f, 0.2f, 0.2f, 1f);

            codeInput = codeInputObj.AddComponent<TMP_InputField>();
            codeInput.textComponent = CreateCodeInputText(codeInputObj);
            codeInput.placeholder = CreateCodePlaceholderText(codeInputObj);
            codeInput.characterLimit = 10;

            codeInput.caretWidth = 3;
            codeInput.caretColor = Color.white;
            codeInput.customCaretColor = true;
            codeInput.caretBlinkRate = 0.85f;

            codeInput.selectionColor = new Color(0.65f, 0.8f, 1f, 0.5f);

            codeInput.enabled = false;
            codeInput.enabled = true;

            codeInput.gameObject.SetActive(false);

            var joinButtonObj = GameObject.Instantiate(mainMenu.btnPlay.gameObject);
            joinButtonObj.transform.SetParent(panel.transform, false);

            var originalJoinButton = joinButtonObj.GetComponent<MyButtonNormal>();
            if (originalJoinButton != null)
            {
                UnityEngine.Object.DestroyImmediate(originalJoinButton);
            }

            UnityEngine.UI.Button butt = joinButtonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (butt != null)
            {
                butt.onClick = new();
            }

            var localizeStringEventJoin = joinButtonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEventJoin != null)
            {
                UnityEngine.Object.DestroyImmediate(localizeStringEventJoin);
            }

            joinButton = joinButtonObj.AddComponent<CustomButton>();
            joinButton.SetOnClickAction(OnJoinClicked);

            var joinTextWrapper = joinButtonObj.GetComponent<ButtonTextWrapper>();
            joinTextWrapper.t_text.text = "Join";
            joinTextWrapper.t_text.fontSize = 36;

            var joinRectTransform = joinButtonObj.GetComponent<RectTransform>();
            joinRectTransform.anchorMin = new Vector2(0.5f, 0.35f);
            joinRectTransform.anchorMax = new Vector2(0.5f, 0.35f);
            joinRectTransform.pivot = new Vector2(0.5f, 0.5f);
            joinRectTransform.anchoredPosition = new Vector2(140f, -20f);
            joinRectTransform.sizeDelta = new Vector2(150, 50);

            joinButton.gameObject.SetActive(false);

            var backButtonObj = GameObject.Instantiate(mainMenu.btnPlay.gameObject);
            backButtonObj.transform.SetParent(panel.transform, false);

            var originalBackButton = backButtonObj.GetComponent<MyButtonNormal>();
            if (originalBackButton != null)
            {
                UnityEngine.Object.DestroyImmediate(originalBackButton);
            }

            UnityEngine.UI.Button backButton = backButtonObj.GetComponentInChildren<UnityEngine.UI.Button>();
            if (backButton != null)
            {
                backButton.onClick = new();
            }

            var localizeStringEventBack = backButtonObj.GetComponentInChildren<LocalizeStringEvent>();
            if (localizeStringEventBack != null)
            {
                UnityEngine.Object.DestroyImmediate(localizeStringEventBack);
            }

            friendliesBackButton = backButtonObj.AddComponent<CustomButton>();
            friendliesBackButton.SetOnClickAction(OnFriendliesBackClicked);

            var backTextWrapper = backButtonObj.GetComponent<ButtonTextWrapper>();
            backTextWrapper.t_text.text = "Back";
            backTextWrapper.t_text.fontSize = 36;

            var backRectTransform = backButtonObj.GetComponent<RectTransform>();
            backRectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            backRectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            backRectTransform.pivot = new Vector2(0.5f, 0.5f);
            backRectTransform.anchoredPosition = new Vector2(0, -200f);
            backRectTransform.sizeDelta = new Vector2(300, 70);

            friendliesBackButton.gameObject.SetActive(false);
        }

        private TextMeshProUGUI CreateCodeInputText(GameObject parent)
        {
            var textObj = new GameObject("Text");
            textObj.transform.SetParent(parent.transform, false);

            var rectTransform = textObj.AddComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.sizeDelta = Vector2.zero;
            rectTransform.anchoredPosition = Vector2.zero;

            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.alignment = TextAlignmentOptions.Center;
            text.verticalAlignment = VerticalAlignmentOptions.Middle;
            text.fontSize = 30;
            text.color = Color.white;
            text.margin = new Vector4(10, 0, 10, 0);

            return text;
        }

        private TextMeshProUGUI CreateCodePlaceholderText(GameObject parent)
        {
            var placeholderObj = new GameObject("Placeholder");
            placeholderObj.transform.SetParent(parent.transform, false);

            var rectTransform = placeholderObj.AddComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.sizeDelta = Vector2.zero;
            rectTransform.anchoredPosition = Vector2.zero;

            var text = placeholderObj.AddComponent<TextMeshProUGUI>();
            text.text = "Enter code...";
            text.alignment = TextAlignmentOptions.Center;
            text.verticalAlignment = VerticalAlignmentOptions.Middle;
            text.fontSize = 22;
            text.color = new Color(1f, 1f, 1f, 0.3f);
            text.margin = new Vector4(10, 0, 10, 0);

            return text;
        }

        protected override void Update()
        {
            base.Update();

            if (playerNameInput == null) return;

            bool isFocused = playerNameInput.isFocused;

            if (wasInputFocused && !isFocused)
            {
                OnPlayerNameEndEdit();
            }
            wasInputFocused = isFocused;
        }

        private void OnPlayerNameEndEdit()
        {
            if (playerNameInput == null) return;

            string newName = playerNameInput.text;

            if (string.IsNullOrWhiteSpace(newName))
            {
                playerNameInput.text = ModConfig.PlayerName.Value;
                return;
            }

            if (filter.IsProfanity(newName))
            {
                newName = filter.CensorString(newName);
                playerNameInput.text = newName;
            }

            newName = newName.Trim();

            ModConfig.PlayerName.Value = newName;
            ModConfig.Save();
            Plugin.Log.LogInfo($"Player name updated to: {ModConfig.PlayerName.Value}");
        }

        private void OnCloseClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);
            CloseModal();
        }

        private void OnRandomClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);

            // The refusal on the Steam transport lives in the service now, with the same wording.
            // Quickplay needs a pool of strangers, which on Steam means a lobby browser that does
            // not exist yet, and running the matchmaker while INetTransport points at Steam is the
            // exact combination that produced a session where every send was refused.
            SessionService.Quickplay();

            if (!BeganConnecting())
            {
                return;
            }

            UpdateModalContents(false);
            ShowLoader("Connecting...");

            connectionCoroutine = CoroutineRunner.Instance.Run(WatchSession(NetworkModeType.Random));
        }

        private void OnFriendliesClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);

            Plugin.Instance.Mode.Mode = NetworkModeType.Friendlies;

            UpdateModalContents(false);
            UpdateFriendliesUI(true);
        }

        private void OnHostClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);

            SessionService.Host();

            if (!BeganConnecting())
            {
                return;
            }

            UpdateFriendliesUI(false);
            ShowLoader("Connecting...");

            connectionCoroutine = CoroutineRunner.Instance.Run(WatchSession(NetworkModeType.Friendlies));
        }

        private void OnJoinClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);

            var code = codeInput.text.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(code))
            {
                SetStatusText("Please enter a room code");
                return;
            }

            JoinWithCode(code);
        }

        /// <summary>
        /// Joins a room by code. Factored out of <see cref="OnJoinClicked"/> so that accepting a
        /// Steam invite takes exactly the same path a player pressing Join does — rather than a
        /// parallel one that would drift out of step with it.
        /// </summary>
        internal void JoinWithCode(string code)
        {
            // Mode, Role and RoomCode are set by the service, which also normalises the code. The
            // empty-code check in OnJoinClicked stays because it reports on this screen; Join
            // refuses an empty code too, so the invite path is covered as well.
            SessionService.Join(code);

            if (!BeganConnecting())
            {
                return;
            }

            UpdateFriendliesUI(false);
            ShowLoader("Joining room...");

            connectionCoroutine = CoroutineRunner.Instance.Run(WatchSession(NetworkModeType.Friendlies));
        }

        /// <summary>
        /// The session service, which until now was registered and called by nothing.
        ///
        /// <para><b>This is the seam that made the Steam transport unreachable.</b> The Steam
        /// session start was built onto <c>INetplaySessionService</c> because that is where the
        /// session lifecycle is supposed to live — but nothing called it, so with the transport flag
        /// on the menu still ran the matchmaker flow while gameplay sends went to a Steam transport
        /// that had never been started. Every send was refused, which broke readiness, and no Steam
        /// lobby was ever created, which hid the invite button.</para>
        ///
        /// <para>Every entry point routes through it — host, join and quickplay, on either
        /// transport. The service picks the transport internally, which is what let the menu's two
        /// connect coroutines go.</para>
        /// </summary>
        private static Services.INetplaySessionService SessionService =>
            Plugin.Services.GetRequiredService<Services.INetplaySessionService>();

        /// <summary>
        /// Whether the attempt just requested actually started, and reports it on the current
        /// screen when it did not.
        ///
        /// <para>Some requests are refused before anything is in flight — quickplay on the Steam
        /// transport, an empty room code, a second press while one attempt is already running. The
        /// service sets <c>Failed</c> synchronously for those, so taking the screen down and
        /// raising a loader first would show a spinner for a session that was never attempted, then
        /// four seconds of nothing, before returning to the screen the player was already on. The
        /// refusal always carries a reason; this is the guard saying it out loud.</para>
        /// </summary>
        private bool BeganConnecting()
        {
            if (SessionService.IsBusy)
            {
                return true;
            }

            SetStatusText(SessionService.StatusMessage);
            return false;
        }

        /// <summary>
        /// Drives the loader, the status line and the lobby hand-off from the session service's
        /// state. One watcher for host, join and quickplay on either transport, replacing
        /// <c>HandleFriendlies</c> and <c>HandleConnectionStatus</c>.
        ///
        /// <para>Those two were near-copies that differed in which screen they put back on failure
        /// and which notification they raised on success — which is what
        /// <paramref name="mode"/> now carries. Everything else about them was the matchmaker's
        /// flags read directly, and the service owns those.</para>
        ///
        /// <para><b><paramref name="mode"/> is a parameter and not a read of
        /// <c>Plugin.Instance.Mode</c>, deliberately.</b> A failure inside the service calls
        /// <c>ResetNetworking</c>, which does <c>Plugin.Instance.Mode = new()</c>, and it does so
        /// <i>before</i> the state this loop is waiting on becomes <c>Failed</c>. So by the time the
        /// failure is visible here the mode reads <c>Random</c> — its default — whatever the player
        /// actually pressed, and a failed Join would put back the wrong screen. The flow has to be
        /// captured when the attempt starts, not read back afterwards.</para>
        /// </summary>
        private IEnumerator WatchSession(NetworkModeType mode)
        {
            stopButton.gameObject.SetActive(true);
            var stopTextWrapper = stopButton.gameObject.GetComponent<ButtonTextWrapper>();
            stopTextWrapper.t_text.text = "Stop";

            var shownMessage = "";

            while (SessionService.IsBusy || SessionService.State == Services.NetplayConnectState.Idle)
            {
                // Quickplay spends its wait in WaitingForMatch with something to say — the shared
                // experience notice the old coroutine printed once. Mirroring the message means
                // this loop does not need to know which flow produces one.
                if (SessionService.StatusMessage != shownMessage)
                {
                    shownMessage = SessionService.StatusMessage;
                    SetStatusText(shownMessage);
                }

                yield return new WaitForSeconds(0.1f);
            }

            if (SessionService.State != Services.NetplayConnectState.Ready)
            {
                HideLoader();
                SetStatusText(SessionService.StatusMessage);
                stopButton.gameObject.SetActive(false);
                yield return new WaitForSeconds(4f);

                if (mode == NetworkModeType.Friendlies)
                {
                    UpdateFriendliesUI(true);
                }
                else
                {
                    UpdateModalContents(true);
                }

                SetStatusText("");
                yield break;
            }

            AudioManager.Instance.PlaySfx(AudioManager.Instance.purchaseSfx.sounds[0]);
            HideLoader();
            SetStatusText("Joined!");
            stopButton.gameObject.SetActive(false);

            if (mode == NetworkModeType.Random)
            {
                var role = Plugin.Instance.NetworkHandler.IsHost ? "Host" : "Client";
                var lobbySize = Plugin.Instance.NetworkHandler.GetLobbySize();
                Plugin.StartNotification(("MegabonkTogether", "MatchSuccess"), ("MegabonkTogether", "MatchSuccessDesc"), [role, lobbySize.ToString()]);
            }
            else if (Plugin.Instance.NetworkHandler.IsHost)
            {
                Plugin.StartNotification(("MegabonkTogether", "FriendliesHostSuccess"), ("MegabonkTogether", "FriendliesHostSuccessDesc"), []);
            }
            else
            {
                Plugin.StartNotification(("MegabonkTogether", "FriendliesClientSuccess"), ("MegabonkTogether", "FriendliesClientSuccessDesc"), [""]);
            }

            yield return new WaitForSeconds(1f);

            // Panel before modal: ShowLobbyPanel reads this.mainMenu and CloseModal destroys this
            // component's GameObject. Both orders are observed to work — the managed reference
            // outlives the destroyed component — but this is the order with a reason behind it.
            ShowLobbyPanel();
            CloseModal();
        }

        private void OnFriendliesBackClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);

            UpdateFriendliesUI(false);
            UpdateModalContents(true);
        }

        private void OnNetplayOptionsClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);

            UpdateModalContents(false);
            UpdateNetplayOptionsUI(true);
        }

        private void OnNetplayOptionsBackClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);

            UpdateNetplayOptionsUI(false);
            UpdateModalContents(true);
        }

        private void OnStopClicked()
        {
            AudioManager.Instance.PlaySfx(AudioManager.Instance.uiSelect.sounds[0]);

            // Two coroutines end here, and they are not the same one. This stops the menu's
            // watcher; Cancel stops the service's connect routine and tears the session down —
            // including leaving the Steam lobby, which ResetNetworking alone would not do on
            // every path.
            if (connectionCoroutine != null)
            {
                CoroutineRunner.Instance.StopCoroutine(connectionCoroutine);
                connectionCoroutine = null;
            }

            SessionService.Cancel();

            HideLoader();

            UpdateModalContents(true);
            stopButton.gameObject.SetActive(false);

            var closeTextWrapper = closeButton.gameObject.GetComponent<ButtonTextWrapper>();
            closeTextWrapper.t_text.text = "Close";
            SetStatusText("");
        }

        private void UpdateModalContents(bool isVisible)
        {
            randomButton.gameObject.SetActive(isVisible);
            friendliesButton.gameObject.SetActive(isVisible);
            netplayOptionsButton.gameObject.SetActive(isVisible);
            closeButton.gameObject.SetActive(isVisible);
            playerNameInput.gameObject.SetActive(isVisible);
            label.SetActive(isVisible);
        }

        private void UpdateFriendliesUI(bool isVisible)
        {
            friendliesTitle.SetActive(isVisible);
            hostButton.gameObject.SetActive(isVisible);
            codeLabel.SetActive(isVisible);
            codeInput.gameObject.SetActive(isVisible);
            joinButton.gameObject.SetActive(isVisible);
            friendliesBackButton.gameObject.SetActive(isVisible);

            if (isVisible)
            {
                PrefillInvitedCode();
            }
        }

        /// <summary>
        /// Fills the room code in for a player who got here by accepting a friend's Steam invite,
        /// so they do not have to retype something they were handed.
        ///
        /// <para>Deliberately stops at filling the box rather than joining for them. Driving this
        /// screen's own flow from outside would mean reproducing what <see cref="OnJoinClicked"/>
        /// does to <c>Plugin.Instance.Mode</c>, the loader and the connection coroutine, and
        /// getting that subtly wrong would strand a player in a half-started join with no way back.
        /// One press of Join is a small price for not owning that risk yet.</para>
        ///
        /// <para>The code is consumed, so it is offered once. A player who backs out and comes
        /// again gets an empty box rather than a stale invite from an hour ago.</para>
        /// </summary>
        private void PrefillInvitedCode()
        {
            if (codeInput == null || !string.IsNullOrWhiteSpace(codeInput.text))
            {
                return;
            }

            var invited = steamInviteService?.ConsumeJoinCode();
            if (string.IsNullOrEmpty(invited))
            {
                return;
            }

            codeInput.text = invited;
            SetStatusText("Invite ready - press Join");
            Plugin.Log.LogInfo($"[steam-invite] Filled the room code from an invite: {invited}.");
        }

        private void UpdateNetplayOptionsUI(bool isVisible)
        {
            netplayOptionsTitle.SetActive(isVisible);
            saveToggleSetting.SetActive(isVisible);
            sharedExpToggleSetting.SetActive(isVisible);
            netplayOptionsBackButton.gameObject.SetActive(isVisible);
        }

        /// <summary>
        /// Opens the lobby panel, which now sits between joining and character selection.
        ///
        /// <para>This step used to not exist: a successful join went straight to
        /// <c>GoToCharacterSelection</c>, which made the character screen double as the lobby and
        /// left no point at which a player could see who else had arrived. See
        /// <c>docs/ui/00-lobby-panel.md</c>.</para>
        /// </summary>
        private void ShowLobbyPanel()
        {
            var panelObj = new GameObject("LobbyPanel");
            var lobbyPanel = panelObj.AddComponent<LobbyPanel>();

            // Handed the menu before the component builds itself — the panel clones one of
            // MainMenu's buttons per button it draws.
            lobbyPanel.Initialize(mainMenu);

            lobbyPanel.OnContinueRequested = GoToCharacterSelection;
            lobbyPanel.OnLeaveRequested = LeaveLobby;
        }

        /// <summary>The step the lobby panel now precedes rather than replaces.</summary>
        private void GoToCharacterSelection()
        {
            mainMenu.GoToCharacterSelection();

            var characterMenu = WindowManager.activeWindow as CharacterMenu;
            if (characterMenu != null)
            {
                characterMenu.selectedButton = characterMenu.characterButtons[0];
                characterMenu.b_confirm.SetInteractable(false);
            }
        }

        /// <summary>
        /// Tears the session down and returns to the main menu. Routed through
        /// <c>ResetNetworking</c> rather than just closing the panel, because a peer that abandons
        /// the UI while still connected is exactly the "player who never reports" case the lobby
        /// barrier has to survive — better to actually leave.
        /// </summary>
        private void LeaveLobby()
        {
            Plugin.Instance.NetworkHandler.ResetNetworking();
        }

    }
}
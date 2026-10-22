using HarmonyLib;
using OmniBonk.Common;
using OmniBonk.Configuration;
using OmniBonk.Scripts.Button;
using OmniBonk.Scripts.Modal;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(MainMenu))]
    internal static class MainMenuPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetRequiredService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetRequiredService<IPlayerManagerService>();
        private static readonly IAutoUpdaterService autoUpdaterService = Plugin.Services.GetRequiredService<IAutoUpdaterService>();
        private static readonly IChangelogService changelogService = Plugin.Services.GetRequiredService<IChangelogService>();

        private static bool hasCheckedChangelog = false;

        /// <summary>Adds the mod's button, with its version underneath.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(MainMenu.Start))]
        public static void Start_Postfix(MainMenu __instance)
        {
            var playBtn = __instance.btnPlay;

            var go = UnityEngine.Object.Instantiate(playBtn.gameObject);

            // See Helpers/ButtonStyle: otherwise every hover throws.
            var style = OmniBonk.Helpers.ButtonStyle.CaptureAndRemove(go);

            UnityEngine.UI.Button button = go.GetComponentInChildren<UnityEngine.UI.Button>();
            if (button != null)
            {
                button.onClick = new();
            }

            var customButton = go.AddComponent<PlayTogetherButton>();
            style.ApplyTo(customButton);
            customButton.SetMainMenu(__instance);

            var textWrapper = go.GetComponent<ButtonTextWrapper>();
            textWrapper.t_text.text = OmniBonk.Helpers.ModVersionLabel.Name;

            var version = new UnityEngine.GameObject("VersionText");
            version.transform.SetParent(textWrapper.t_text.transform);
            version.transform.localPosition = new UnityEngine.Vector3(0, -35, 0);
            version.transform.localScale = UnityEngine.Vector3.one;

            var tmpText = textWrapper.t_text;
            var versionTmp = version.AddComponent<TMPro.TextMeshProUGUI>();
            versionTmp.font = tmpText.font;
            versionTmp.fontSize = tmpText.fontSize * 0.45f;
            versionTmp.alignment = tmpText.alignment;
            versionTmp.color = tmpText.color;
            //versionTmp.text = OmniBonk.Helpers.ModVersionLabel.ShortVersion;


            var menuColumn = playBtn.transform.parent;
            AddSpacerAbove(menuColumn, go.transform, playBtn.transform);
            go.transform.SetParent(menuColumn);

            Plugin.Instance.PlayTogetherButton = customButton;
            Plugin.Instance.SetMainMenu(__instance);

            if (autoUpdaterService.IsCustomBuild() && autoUpdaterService.IsAnUpdateAvailable())
            {
                customButton.gameObject.SetActive(false);
            }

            if (!hasCheckedChangelog)
            {
                hasCheckedChangelog = true;

                // Fresh install: PreviousVersion stays empty, which makes the changelog show only the
                // newest entry. Closing the popup records the current version, so it shows once.
                if (string.IsNullOrEmpty(ModConfig.PreviousVersion.Value) && !autoUpdaterService.IsCustomBuild())
                {
                    Plugin.Log.LogInfo("First launch: showing the current version's changelog");
                    ModConfig.ShowChangelog.Value = true;
                    ModConfig.Save();
                }

                bool shouldShow = changelogService.ShouldShowChangelog();

                if (shouldShow)
                {
                    ShowChangelogModal();
                }
            }
        }

        /// <summary>
        /// Spacer for the button's overhang: <c>SetParent</c> keeps world scale, so the clone ends
        /// up ~1.5x larger than its rect. The larger look is kept on purpose.
        /// </summary>
        private static void AddSpacerAbove(UnityEngine.Transform menuColumn, UnityEngine.Transform button, UnityEngine.Transform reference)
        {
            // The scale it will get once parented.
            var columnScale = menuColumn.lossyScale.y;
            var scaleRatio = columnScale > 0f ? button.lossyScale.y / columnScale / reference.localScale.y : 1f;

            var rect = button.GetComponent<UnityEngine.RectTransform>();
            var height = rect != null ? rect.rect.height : 0f;
            var pivotFromTop = rect != null ? 1f - rect.pivot.y : 0.5f;
            var overhang = (scaleRatio - 1f) * height * pivotFromTop;

            if (overhang <= 0.5f)
            {
                return;
            }

            var spacer = new UnityEngine.GameObject("OmniBonkSpacer");
            var spacerRect = spacer.AddComponent<UnityEngine.RectTransform>();
            spacer.transform.SetParent(menuColumn, false);
            spacerRect.sizeDelta = new UnityEngine.Vector2(0f, overhang);

            var element = spacer.AddComponent<UnityEngine.UI.LayoutElement>();
            element.minHeight = overhang;
            element.preferredHeight = overhang;
            element.flexibleHeight = 0f;
        }

        private static void ShowChangelogModal()
        {
            Task.Run(async () =>
            {
                try
                {
                    var previousVersion = Configuration.ModConfig.PreviousVersion.Value;
                    var currentVersion = MyPluginInfo.PLUGIN_VERSION;

                    Plugin.Log.LogInfo($"Fetching changelog for versions: {previousVersion} -> {currentVersion}");

                    var changelog = await changelogService.LoadChangelogAsync();
                    if (changelog == null || changelog.Count == 0)
                    {
                        Plugin.Log.LogInfo("No changelog data available");
                        changelogService.MarkChangelogAsShown();
                        return;
                    }

                    var changes = changelogService.GetChangesBetweenVersions(changelog, previousVersion, currentVersion);
                    if (changes == null || changes.Count == 0)
                    {
                        Plugin.Log.LogInfo("No changes found between versions");
                        changelogService.MarkChangelogAsShown();
                        return;
                    }

                    Plugin.Log.LogInfo($"Found {changes.Count} version(s) with changes to display");

                    ChangelogModal.Show(changes);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogError($"Error showing changelog: {ex.Message}");
                    changelogService.MarkChangelogAsShown();
                }
            });
        }

        /// <summary>Syncs the selected character; clients wait for the host instead of picking a map.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MainMenu.GoToMapSelection))]
        public static bool GoToMapSelection_Prefix()
        {
            if (!Plugin.Instance.NetworkHandler.HasFoundMatch.HasValue || !Plugin.Instance.NetworkHandler.HasFoundMatch.Value)
            {
                return true;
            }

            if (synchronizationService.IsLoading())
            {
                return false;
            }

            synchronizationService.OnSelectedCharacter();

            var isHost = synchronizationService.IsServerMode() ?? false;
            if (!isHost)
            {
                Plugin.Instance.ShowModal("Waiting for the host...");
                return false;
            }

            return true;
        }

        /// <summary>Restores the button text, which otherwise disappears.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(MainMenu.GoToMenu))]
        public static void GoToMenu_Postfix()
        {
            if (Plugin.Instance.PlayTogetherButton != null)
            {
                var textWrapper = Plugin.Instance.PlayTogetherButton.GetComponent<ButtonTextWrapper>();
                textWrapper.t_text.text = OmniBonk.Helpers.ModVersionLabel.Name;
            }
        }
    }
}

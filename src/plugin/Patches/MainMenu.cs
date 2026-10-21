using HarmonyLib;
using MegabonkTogether.Common;
using MegabonkTogether.Configuration;
using MegabonkTogether.Scripts.Button;
using MegabonkTogether.Scripts.Modal;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(MainMenu))]
    internal static class MainMenuPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetRequiredService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetRequiredService<IPlayerManagerService>();
        private static readonly IAutoUpdaterService autoUpdaterService = Plugin.Services.GetRequiredService<IAutoUpdaterService>();
        private static readonly IChangelogService changelogService = Plugin.Services.GetRequiredService<IChangelogService>();

        private static bool hasCheckedChangelog = false;

        /// <summary>
        /// Add the mod's button, with its version underneath, to the main menu
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(MainMenu.Start))]
        public static void Start_Postfix(MainMenu __instance)
        {
            var playBtn = __instance.btnPlay;

            var go = UnityEngine.Object.Instantiate(playBtn.gameObject);

            // Captured before the original goes, applied after the replacement arrives. Destroying
            // it outright left background null and every hover on the button threw — see
            // Helpers/ButtonStyle.
            var style = MegabonkTogether.Helpers.ButtonStyle.CaptureAndRemove(go);

            UnityEngine.UI.Button button = go.GetComponentInChildren<UnityEngine.UI.Button>();
            if (button != null)
            {
                button.onClick = new();
            }

            var customButton = go.AddComponent<PlayTogetherButton>();
            style.ApplyTo(customButton);
            customButton.SetMainMenu(__instance);

            var textWrapper = go.GetComponent<ButtonTextWrapper>();
            textWrapper.t_text.text = MegabonkTogether.Helpers.ModVersionLabel.Name;

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
            versionTmp.text = MegabonkTogether.Helpers.ModVersionLabel.ShortVersion;


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

                if (string.IsNullOrEmpty(ModConfig.PreviousVersion.Value) && !autoUpdaterService.IsCustomBuild())
                {
                    Plugin.Log.LogInfo("First time with changelog system detected");
                    ModConfig.ShowChangelog.Value = true;
                    ModConfig.PreviousVersion.Value = "1.0.0";
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
        /// Puts an empty spacer in the menu column before our button, as tall as the part of the
        /// button that pokes out above its own rect, so the gap under Shop matches the others.
        ///
        /// <para><b>Why the button pokes out.</b> It is cloned with no parent, so it starts at
        /// scale 1 in world space, and <c>SetParent</c> keeps world size by default. The menu canvas
        /// is scaled down, so Unity scales the clone up to compensate: in play it was drawn about
        /// 1.5x the size of the buttons around it. The column lays children out by their unscaled
        /// rect, so the extra height spilled into the gap above it. The bigger button is how it has
        /// always looked, so it is kept and the column is given room for it instead.</para>
        ///
        /// <para>The spacer is a sized rect and a <see cref="UnityEngine.UI.LayoutElement"/>, so a
        /// layout group reads the same height whether or not it controls child heights. Confirmed
        /// in play on 2026-10-21: the gap under Shop now matches the others.</para>
        /// </summary>
        private static void AddSpacerAbove(UnityEngine.Transform menuColumn, UnityEngine.Transform button, UnityEngine.Transform reference)
        {
            // Measured as it will be once parented, without parenting it yet: the scale it is about
            // to get is its world scale over the column's.
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

        /// <summary>
        /// Synchronize the selected character and prevent going to map selection if not host (Wait for host)
        /// </summary>
        /// <returns></returns>
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

        /// <summary>
        /// Restore the button's text when going back to main menu (because its disappears for some reason)
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(MainMenu.GoToMenu))]
        public static void GoToMenu_Postfix()
        {
            if (Plugin.Instance.PlayTogetherButton != null)
            {
                var textWrapper = Plugin.Instance.PlayTogetherButton.GetComponent<ButtonTextWrapper>();
                textWrapper.t_text.text = MegabonkTogether.Helpers.ModVersionLabel.Name;
            }
        }
    }
}

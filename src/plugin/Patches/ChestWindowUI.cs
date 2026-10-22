using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Utility;
using HarmonyLib;
using OmniBonk.Helpers;
using Microsoft.Extensions.DependencyInjection;
using System.Collections;
using UnityEngine;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(ChestOpening))]
    internal static class ChestOpeningPatches
    {
        private static readonly Services.ISynchronizationService synchronizationService = Plugin.Services.GetService<Services.ISynchronizationService>();

        /// <summary>Skip the opening animation unless shared experience is on.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChestOpening.OpenChest))]
        public static void OpenChest_Postfix(ChestOpening __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (synchronizationService.IsSharedExperienceEnabled())
            {
                return;
            }

            __instance.skipped = true;
        }
    }


    //TODO: Refacto with LevelUpScreenPatch
    [HarmonyPatch(typeof(ChestWindowUi))]
    internal static class ChestWindowUiPatches
    {
        private static readonly Services.ISynchronizationService synchronizationService = Plugin.Services.GetService<Services.ISynchronizationService>();
        public static Coroutine CurrentRoutine;
        private static TMPro.TextMeshProUGUI infoText;
        private static MyButton openButton;

        // SE-12 diagnostic; null = not sampled yet.
        private static bool? itemCallbacksWereSet;
        private static bool reportedStrandedItemCallbacks;

        /// <summary>Diagnostic for #93 (chest soft lock). No netplay guard: logging only.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChestWindowUi.Open))]
        public static void Open_Prefix(ChestWindowUi __instance, Assets.Scripts.UI.InGame.Rewards.EEncounter encounterType)
        {
            if (__instance.chestOpening == null || __instance.b_open == null ||
                __instance.b_leave == null || __instance.b_take == null ||
                __instance.b_banish == null || __instance.t_itemName == null)
            {
                Plugin.Log.LogError($"[chest #93] ChestWindowUi.Open about to dereference a destroyed ref: " +
                    $"encounter={encounterType} shared={synchronizationService.IsSharedExperienceEnabled()} " +
                    $"chestOpening={__instance.chestOpening == null} b_open={__instance.b_open == null} " +
                    $"b_leave={__instance.b_leave == null} b_take={__instance.b_take == null} " +
                    $"b_banish={__instance.b_banish == null} t_itemName={__instance.t_itemName == null}");
            }

            ReportStrandedItemCallbacks();
        }

        /// <summary>
        /// Reports, once, if the item-callback statics went from set to null (stranded by a throw in
        /// NetPlayer.AddItem/RemoveItem; cf. P1-9).
        /// </summary>
        private static void ReportStrandedItemCallbacks()
        {
            var setNow = ItemInventory.A_ItemAdded != null && ItemInventory.A_ItemRemoved != null;

            if (!itemCallbacksWereSet.HasValue)
            {
                itemCallbacksWereSet = setNow;
                return;
            }

            if (!itemCallbacksWereSet.Value || setNow || reportedStrandedItemCallbacks)
            {
                return;
            }

            reportedStrandedItemCallbacks = true;
            Plugin.Log.LogError(
                "[chest #81] ItemInventory.A_ItemAdded/A_ItemRemoved were set when the first chest " +
                $"opened and are null now (added={ItemInventory.A_ItemAdded == null} " +
                $"removed={ItemInventory.A_ItemRemoved == null}). The mod borrows and restores those " +
                "around NetPlayer.AddItem/RemoveItem, so something threw in between and the game's " +
                "item callbacks are dead for the rest of the process - the suspected cause of " +
                "chests only offering banish after the first one.");
        }

        /// <summary>Auto-opens and hides the open button (restored in OnClose_Postfix), unless shared experience.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChestWindowUi.Open))]
        public static void Open_Postfix(ChestWindowUi __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (synchronizationService.IsSharedExperienceEnabled())
            {
                return;
            }

            if (openButton == null && __instance.b_open != null)
            {
                openButton = __instance.b_open;
            }

            __instance.OpenButton();
            __instance.b_open = null;
        }

        /// <summary>5 s of invulnerability after opening a chest, when not in shared experience.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChestWindowUi.OpeningFinished))]
        public static void OpeningFinished_Postfix(ChestWindowUi __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (synchronizationService.IsSharedExperienceEnabled())
            {
                return;
            }

            if (!GameManager.Instance.player.playerInput.CanInput())
            {
                Plugin.Log.LogInfo("Player cannot input, skipping invulnerability after chest opening.");
                return;
            }

            MyTime.Unpause();

            CoroutineRunner.Instance.Stop(CurrentRoutine);
            CurrentRoutine = CoroutineRunner.Instance.Run(Wait5SecBeforeCanTakeDamageAgain(__instance));
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChestWindowUi.OnClose))]
        public static void OnClose_Postfix(ChestWindowUi __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            // FIX #93: restore b_open before the early return below. Unity's == also rejects a destroyed button.
            if (openButton != null)
            {
                __instance.b_open = openButton;
            }

            if (CurrentRoutine == null)
            {
                Plugin.Log.LogDebug("No active invulnerability routine, skipping.");
                return;
            }

            CoroutineRunner.Instance.Stop(CurrentRoutine);
            CurrentRoutine = CoroutineRunner.Instance.Run(CancelWaitForVulnerabilityAfter1sec());
        }

        /// <summary>Clears session statics; a kept <c>openButton</c> would be destroyed next session (#93).</summary>
        internal static void Reset()
        {
            CurrentRoutine = null;
            openButton = null;
            infoText = null;
            itemCallbacksWereSet = null;
            reportedStrandedItemCallbacks = false;
        }

        private static IEnumerator CancelWaitForVulnerabilityAfter1sec()
        {
            if (infoText == null)
            {
                var go = new GameObject("InfoText");
                UnityEngine.GameObject.DontDestroyOnLoad(go);
                infoText = go.AddComponent<TMPro.TextMeshProUGUI>();
            }

            if (infoText == null)
            {
                yield break;
            }

            GameManager.Instance.player.isTeleporting = true;
            Plugin.Instance.IS_MANUAL_INVINCIBLE = true;

            infoText.enabled = true;
            infoText.transform.SetParent(UiManager.Instance.encounterWindows.transform);
            infoText.rectTransform.anchorMin = new Vector2(0, 0);
            infoText.rectTransform.anchorMax = new Vector2(0, 0);
            infoText.rectTransform.pivot = new Vector2(0, 0);
            infoText.rectTransform.anchoredPosition = new Vector2(100, 100);
            infoText.alignment = TMPro.TextAlignmentOptions.Center;
            infoText.fontSize = 36;
            infoText.color = Color.white;

            float timer = 0.3f;
            while (timer > 0f)
            {
                int seconds = Mathf.FloorToInt(timer);
                int milliseconds = Mathf.FloorToInt((timer - seconds) * 1000);
                infoText.text = $"You will be vulnerable in {seconds.ToString("D2")}:{milliseconds.ToString("D3")} secondes";
                yield return null;
                timer -= Time.deltaTime;

                if (!Plugin.Instance.IS_MANUAL_INVINCIBLE)
                {
                    infoText.enabled = false;
                    Plugin.Instance.NetPlayersDisplayer.Show();
                    yield break;
                }
            }

            GameManager.Instance.player.isTeleporting = false;
            Plugin.Instance.IS_MANUAL_INVINCIBLE = false;
            infoText.enabled = false;

            Plugin.Instance.NetPlayersDisplayer.Show();
        }

        private static IEnumerator Wait5SecBeforeCanTakeDamageAgain(ChestWindowUi __instance)
        {
            if (infoText == null)
            {
                var go = new GameObject("InfoText");
                UnityEngine.GameObject.DontDestroyOnLoad(go);
                infoText = go.AddComponent<TMPro.TextMeshProUGUI>();
            }

            if (infoText == null)
            {
                yield break;
            }

            GameManager.Instance.player.isTeleporting = true;
            Plugin.Instance.IS_MANUAL_INVINCIBLE = true;

            Plugin.Instance.NetPlayersDisplayer.Hide();

            infoText.enabled = true;
            infoText.transform.SetParent(__instance.transform);
            infoText.rectTransform.anchorMin = new Vector2(0, 0);
            infoText.rectTransform.anchorMax = new Vector2(0, 0);
            infoText.rectTransform.pivot = new Vector2(0, 0);
            infoText.rectTransform.anchoredPosition = new Vector2(100, 100);
            infoText.alignment = TMPro.TextAlignmentOptions.Center;
            infoText.fontSize = 36;

            float timer = 5f;
            while (timer > 0f)
            {
                int seconds = Mathf.FloorToInt(timer);
                int milliseconds = Mathf.FloorToInt((timer - seconds) * 1000);
                infoText.text = $"You will be vulnerable in {seconds.ToString("D2")}:{milliseconds.ToString("D3")} secondes";
                yield return null;
                timer -= Time.deltaTime;

                if (!Plugin.Instance.IS_MANUAL_INVINCIBLE)
                {
                    infoText.enabled = false;
                    yield break;
                }
            }

            GameManager.Instance.player.isTeleporting = false;
            Plugin.Instance.IS_MANUAL_INVINCIBLE = false;

            infoText.text = "You are now vulnerable!";
            infoText.color = Color.red;
        }
    }
}

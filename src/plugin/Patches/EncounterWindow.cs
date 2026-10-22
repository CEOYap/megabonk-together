using Assets.Scripts.UI.InGame.Levelup;
using Assets.Scripts.UI.InGame.Rewards;
using Assets.Scripts.Utility;
using Coffee.UIExtensions;
using HarmonyLib;
using OmniBonk.Helpers;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(EncounterWindows))]
    internal static class EncounterWindowPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IEncounterService encounterService = Plugin.Services.GetService<IEncounterService>();

        /// <summary>
        /// Pop a missed reward only once the player can move; popping mid level change modifies the queue while iterating.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EncounterWindows.PopReward))]
        public static bool PopReward_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (!GameManager.Instance.player.playerInput.CanInput())
            {
                //Plugin.Log.LogWarning($"Player can't move yet, skipping reward pop for now");
                return false;
            }

            if (GameManager.Instance.player.IsDead())
            {
                if (synchronizationService.IsSharedExperienceEnabled())
                {
                    MyTime.Pause();
                    ScreenTextHelper.Show("Waiting for other player(s) choices...", new Vector2(0, -350));
                    synchronizationService.RewardFinished();
                }
                return false;
            }

            return true;
        }

        /// <summary>Queues an encounter while one is in progress; popped later in LateUpdate.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EncounterWindows.AddEncounter))]
        public static bool AddEncounter_Prefix(EncounterWindows __instance, EEncounter rewardWindowType)
        {

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (__instance.encounterInProgress)
            {
                //Plugin.Log.LogWarning($"Encounter in progress, queueing encounter {rewardWindowType}");
                __instance.rewardQueue.Enqueue(rewardWindowType);
                return false;
            }

            if (GameManager.Instance.player.IsDead())
            {
                if (synchronizationService.IsSharedExperienceEnabled())
                {
                    MyTime.Pause();
                    synchronizationService.RewardFinished();
                    ScreenTextHelper.Show("Waiting for other player(s) choices...", new Vector2(0, -350));
                }
                return false;
            }

            return true;
        }

        /// <summary>
        /// Prevent pause on netplay reward pop (Shady guy and other) on non shared experience
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(EncounterWindows.PopReward))]
        public static void PopReward_Postfix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (synchronizationService.IsSharedExperienceEnabled())
            {
                UiManager.Instance.encounterWindows?.activeEncounterWindow?.gameObject.SetActive(true);
                return;
            }

            MyTime.Unpause();
        }

        /// <summary>Pops a reward deferred by PopReward_Prefix once the player can move.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EncounterWindows.LateUpdate))]
        public static void LateUpdate_Prefix(EncounterWindows __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            // Failsafe first: a stuck peer is paused or dead, so the early returns below would skip it.
            // LateUpdate runs at timeScale 0.
            if (synchronizationService.IsSharedExperienceEnabled() && encounterService.HasWaitedTooLong())
            {
                synchronizationService.ForceCloseEncounter(
                    $"no release after {encounterService.WaitFailsafeSeconds:F0}s");
                return;
            }

            if (!GameManager.Instance.player.playerInput.CanInput())
            {
                return;
            }

            if (GameManager.Instance.player.IsDead())
            {
                return;
            }

            var currentQueue = __instance.rewardQueue;
            if (currentQueue.Count > 0 && !__instance.encounterInProgress)
            {
                //Plugin.Log.LogInfo($"Pop previously missed reward");
                __instance.PopReward();
            }
        }


        /// <summary>Reports the end of a reward to the shared-experience barrier.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EncounterWindows.RewardFinished))]
        public static bool RewardFinished_Prefix(EncounterWindows __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (!synchronizationService.IsSharedExperienceEnabled())
            {
                return true;
            }

            var currentQueue = __instance.rewardQueue;
            if (currentQueue.Count > 0) //Keep popping reward until queue is empty
            {
                return true;
            }

            if (encounterService.IsClosable())
            {
                ScreenTextHelper.Clear();
                encounterService.ClearClosedEncounters();
                return true;
            }

            synchronizationService.RewardFinished();

            // Reporting can release the round synchronously (re-entering this method); !IsWaiting
            // means that happened, so do not show the "waiting" UI.
            if (!encounterService.IsWaiting)
            {
                return false;
            }

            var ui = UiManager.Instance;
            ui.encounterWindows?.activeEncounterWindow?.gameObject.SetActive(false);

            foreach (var particles in Il2CppFindHelper.RuntimeGetComponentsInChildren<UIParticleRenderer>(ui))
            {
                particles.enabled = false;
            }

            ScreenTextHelper.Show("Waiting for other player(s) choices...", new Vector2(0, -350));

            return false;
        }
    }
}

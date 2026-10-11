using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(ChargeShrine))]
    internal static class ChargeShrinePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Synchronize starting to charge shrine.
        /// The server check and notify other clients if they need to start (Nothing happens if already charging)
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChargeShrine.OnTriggerEnter))]
        public static bool OnTriggerEnter_Prefix(ChargeShrine __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return true;
            }

            var shrineNetplayId = DynamicData.For(__instance.gameObject).Get<uint?>("netplayId");

            if (shrineNetplayId.HasValue)
            {
                return synchronizationService.OnStartingToChargingShrine(shrineNetplayId.Value);
            }
            else
            {
                Plugin.Log.LogWarning("Charge shrine has no netplay id set!");
            }

            return true;
        }

        /// <summary>
        /// Synchronize stopping to charge shrine.
        /// The server check and notify other clients if they need to stop (Nothing happens if a player is still charging)
        /// </summary>
        /// <summary>
        /// OB-6, reopened 2026-10-11. Only the host may complete a charge shrine.
        ///
        /// <para>Each peer fills the bar itself (ChargeShrine.Update, VA 0x1804C2E20, decompiled)
        /// and calls Complete when its own bar is full; Update then opens the reward window with
        /// AddEncounter(1). Nothing tied those moments together across machines, so under shared
        /// experience the barrier made whoever completed first wait for the other: 8 to 15 s per
        /// round on 2026-10-11, and the 20 s failsafe when one peer completed a shrine the other
        /// never did (the steady +1 the desync detector showed on the Charge Shrines row).</para>
        ///
        /// <para>A client's own completion is held: its bar sits at full, and Update's
        /// "already full" early return stops it calling Complete again every frame. The host's
        /// ChargeShrineCompleted message completes it. Prefix-false is deliberate suppression.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChargeShrine.Complete))]
        public static bool Complete_Prefix(ChargeShrine __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (synchronizationService.IsServerMode() ?? false)
            {
                return true;
            }

            return SynchronizationService.IsApplyingHostShrineCompletion;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChargeShrine.Complete))]
        public static void Complete_Postfix(ChargeShrine __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            synchronizationService.OnChargeShrineCompleted(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChargeShrine.OnTriggerExit))]
        public static bool OnTriggerExit_Prefix(ChargeShrine __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }
            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return true;
            }
            var shrineNetplayId = DynamicData.For(__instance.gameObject).Get<uint?>("netplayId");
            if (shrineNetplayId.HasValue)
            {
                return synchronizationService.OnStoppingChargingShrine(shrineNetplayId.Value);
            }
            else
            {
                Plugin.Log.LogWarning("Charge shrine has no netplay id set!");
            }
            return true;
        }
    }
}

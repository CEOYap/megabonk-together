using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(ChargeShrine))]
    internal static class ChargeShrinePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Host checks and tells clients to start charging (no-op if already charging).</summary>
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
        /// OB-6: only the host completes a charge shrine. Each peer fills its own bar
        /// (<c>ChargeShrine.Update</c>, VA 0x1804C2E20, decompiled), so completions desynced the
        /// shared-experience barrier.
        /// <para>Prefix-false holds a client's completion; Update's "already full" return stops it
        /// retrying. The host's ChargeShrineCompleted completes it.</para>
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

            if (SynchronizationService.IsApplyingHostShrineCompletion)
            {
                return true;
            }

            // Tell the host: it completes for everyone on the earliest fill from any player.
            synchronizationService.RequestChargeShrineCompletion(__instance);
            return false;
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

        /// <summary>Host checks and tells clients to stop (no-op while a player is still charging).</summary>
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

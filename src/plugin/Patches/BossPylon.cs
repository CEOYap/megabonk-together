using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(BossPylon))]
    internal static class BossPylonPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Synchronize pylon charge start; the host relays it (no-op if already charging).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(BossPylon.OnTriggerEnter))]
        public static bool OnTriggerEnter_Prefix(BossPylon __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return true;
            }

            var pylonNetplayId = DynamicData.For(__instance.gameObject).Get<uint?>("netplayId");

            if (pylonNetplayId.HasValue)
            {
                return synchronizationService.OnStartingToChargingPylon(pylonNetplayId.Value);
            }
            else
            {
                Plugin.Log.LogWarning("Pylon has no netplay id set!");
            }

            return true;
        }

        /// <summary>Synchronize pylon charge stop (no-op while another player still charges).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(BossPylon.OnTriggerExit))]
        public static bool OnTriggerExit_Prefix(BossPylon __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }
            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return true;
            }
            var pylonNetplayId = DynamicData.For(__instance.gameObject).Get<uint?>("netplayId");
            if (pylonNetplayId.HasValue)
            {
                return synchronizationService.OnStoppingChargingPylon(pylonNetplayId.Value);
            }
            else
            {
                Plugin.Log.LogWarning("BossPylon has no netplay id set!");
            }
            return true;
        }
    }
}

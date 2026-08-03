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

        /// <summary>
        /// Diagnostic only — see <see cref="Helpers.ShrineDiagnostics"/>, and delete both together.
        ///
        /// <para><c>Complete</c> is what sets <c>rewardGiven</c> and disables the mesh for good, so
        /// the question this answers is whether a client reaches it earlier than the host does.
        /// Charge speed is derived per-peer from the local player's Wrench
        /// (<c>ChargeShrine$$FindChargeTime</c>), which is a real mechanism for the two peers
        /// disagreeing about when the shrine is finished.</para>
        ///
        /// <para><b>Deliberately has no ownership check</b>, which every other patch here does:
        /// the whole point is to see this fire on <i>every</i> peer and compare. It sends nothing,
        /// so there is no echo to cause.</para>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChargeShrine.Complete))]
        public static void Complete_Postfix(ChargeShrine __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;

            Plugin.Log.LogInfo($"[shrine] Complete on {(isHost ? "host" : "client")} — {Helpers.ShrineDiagnostics.Describe(__instance)}");
        }
    }
}

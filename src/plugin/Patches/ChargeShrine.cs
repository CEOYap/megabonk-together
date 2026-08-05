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
            var role = isHost ? "host" : "client";

            Plugin.Log.LogInfo($"[shrine] Complete on {role} — {Helpers.ShrineDiagnostics.Describe(__instance)}");
            Plugin.Log.LogInfo($"[shrine-render] {role} complete — {Helpers.ShrineDiagnostics.DescribeRenderers(__instance)}");
        }

        /// <summary>
        /// Diagnostic only — reports the frame the rune stone moves, and nothing on any other frame.
        ///
        /// <para>The previous run established that the stone starts at the correct position on a
        /// client and is displaced later, always to the same world point, but not when or by what.
        /// Sampling here is what turns that into an answer without spending another playtest per
        /// guess: the log line names the frame, the delta, the local position afterwards, and the
        /// shrine's charge state at that moment.</para>
        ///
        /// <para><b>No ownership check</b>, deliberately — the whole point is to see it on every
        /// peer and compare. It sends nothing, so there is no echo to cause.</para>
        ///
        /// <para><b>Cost while healthy:</b> one dictionary lookup and one squared-distance compare
        /// per shrine per frame, and no allocation or logging unless the stone actually moves.
        /// Delete with the rest of the shrine instrumentation.</para>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChargeShrine.Update))]
        public static void Update_Postfix(ChargeShrine __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var moved = Helpers.ShrineDiagnostics.SampleForMovement(__instance);
            if (moved == null)
            {
                return;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;

            Plugin.Log.LogWarning($"[shrine-move] {(isHost ? "host" : "client")} — {moved}");
        }
    }
}

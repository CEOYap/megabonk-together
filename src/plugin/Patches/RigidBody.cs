using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(Rigidbody))]
    internal class RigidBodyPatches
    {
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        /// <summary>Redirects get_position (used by enemy spawn positioning) so enemies can spawn near a remote player.</summary>
        [HarmonyPostfix]
        [HarmonyPatch("get_position")]
        public static void Get_position_PostFix(ref Vector3 __result, Transform __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var netplayerIdNullable = playerManagerService.PeakNetplayerPosition();
            if (netplayerIdNullable.HasValue)
            {
                var netPlayer = playerManagerService.GetNetPlayerByNetplayId(netplayerIdNullable.Value);
                if (netPlayer != null)
                {
                    __result = netPlayer.Model.transform.position;
                }

                return;
            }
        }
    }
}

using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileMelee))]
    internal static class ProjectileMeleePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Use the correct player (local / remote) transform
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileMelee.MyUpdate))]
        public static void MyUpdate_Prefix(ProjectileMelee __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var netPlayer = Plugin.Services.GetService<IPlayerManagerService>().GetNetPlayerByWeapon(__instance.weaponBase);
            if (netPlayer == null)
            {
                return;
            }

            playerManagerService.AddGetNetplayerPositionRequest(netPlayer.ConnectionId);

            __state = true;

        }

        /// <summary>
        /// Restore original transform after prefix
        /// </summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileMelee.MyUpdate))]
        public static void MyUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
}

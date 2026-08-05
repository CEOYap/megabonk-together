using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(ReturnWeaponWui))]
    internal static class ReturnWeaponWuiPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Use remote player position when boss returns their weapon
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ReturnWeaponWui.Update))]
        public static void Update_Prefix(ReturnWeaponWui __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var netPlayers = playerManagerService.GetAllSpawnedNetPlayers();
            foreach (var netPlayer in netPlayers)
            {
                if (netPlayer.ReturnWeaponWui == __instance)
                {
                    playerManagerService.AddGetNetplayerPositionRequest(netPlayer.ConnectionId);
                    __state = true;

                    break;
                }
            }
        }


        /// <summary>
        /// Remove the position request after updating
        /// </summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ReturnWeaponWui.Update))]
        public static void Update_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
}

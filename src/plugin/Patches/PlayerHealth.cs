using Assets.Scripts.Inventory__Items__Pickups;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(PlayerHealth))]
    internal static class PlayerHealthPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerHealth.PlayerDied))]
        public static void PlayerDied_Postfix(PlayerHealth __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            synchronizationService.OnPlayerDied();
        }

        /// <summary>Prevents remote inventory ticks.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerHealth.Tick))]
        public static bool Tick_Prefix(PlayerHealth __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isRemotePlayer = playerManagerService.IsRemotePlayerHealth(__instance);
            if (isRemotePlayer)
            {
                return false;
            }

            return true;
        }
    }
}

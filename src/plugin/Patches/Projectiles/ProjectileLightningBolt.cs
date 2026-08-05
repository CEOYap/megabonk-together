using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileLightningBolt))]
    internal static class ProjectileLightningBoltPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Use the correct player (local / remote) transform
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileLightningBolt.TryInit))]
        public static bool TryInit_Prefix(ProjectileLightningBolt __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;
            if (!isHost)
            {
                return true;
            }

            var netPlayer = Plugin.Services.GetService<IPlayerManagerService>().GetNetPlayerByWeapon(__instance.weaponBase);
            if (netPlayer == null)
            {
                return true;
            }

            playerManagerService.AddGetNetplayerPositionRequest(netPlayer.ConnectionId);

            __state = true;

            return true;
        }

        /// <summary>
        /// Restore original transform after prefix
        /// </summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileLightningBolt.TryInit))]
        public static void TryInit_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
}

using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileScythe))]
    internal static class ProjectileScythePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Use the correct player (local / remote) transform
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileScythe.MyUpdate))]
        public static void MyUpdate_Prefix(ProjectileScythe __instance, out bool __state)
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
        [HarmonyPatch(nameof(ProjectileScythe.MyUpdate))]
        public static void MyUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

        /// <summary>
        /// Use the correct player (local / remote) transform
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileScythe.TryInit))]
        public static void TryInit_Prefix(ProjectileScythe __instance, out bool __state)
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
        [HarmonyPatch(nameof(ProjectileScythe.TryInit))]
        public static void TryInit_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

        /// <summary>
        /// Use the correct player (local / remote) transform
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileScythe.CheckZone))]
        public static void CheckZone_Prefix(ProjectileScythe __instance, out bool __state)
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
        [HarmonyPatch(nameof(ProjectileScythe.CheckZone))]
        public static void CheckZone_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
}

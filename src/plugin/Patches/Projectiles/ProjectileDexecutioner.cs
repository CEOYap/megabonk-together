using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileDexecutioner))]
    internal static class ProjectileDexecutionerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileDexecutioner.MyUpdate))]
        public static bool MyUpdate_Prefix(ProjectileDexecutioner __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;
            if (!isHost)
            {
                return false;
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

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileDexecutioner.MyUpdate))]
        public static void MyUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileDexecutioner.TryInit))]
        public static bool TryInit_Prefix(ProjectileDexecutioner __instance, int projectileIndex, out bool __state)
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
            var netPlayer = playerManagerService.GetNetPlayerByWeapon(__instance.weaponBase);
            if (netPlayer == null)
            {
                return true;
            }
            playerManagerService.AddGetNetplayerPositionRequest(netPlayer.ConnectionId);
            __state = true;
            return true;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileDexecutioner.TryInit))]
        public static void TryInit_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileDexecutioner.CheckZone))]
        public static bool CheckZone_Prefix(ProjectileDexecutioner __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }
            var isHost = synchronizationService.IsServerMode() ?? false;
            if (!isHost)
            {
                return false;
            }
            var netPlayer = playerManagerService.GetNetPlayerByWeapon(__instance.weaponBase);
            if (netPlayer == null)
            {
                return true;
            }
            playerManagerService.AddGetNetplayerPositionRequest(netPlayer.ConnectionId);
            __state = true;
            return true;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileDexecutioner.CheckZone))]
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

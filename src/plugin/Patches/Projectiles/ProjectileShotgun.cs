using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileShotgun))]
    internal static class ProjectileShotgunPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Use the owning player's (local or remote) transform.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileShotgun.TryInit))]
        public static void MyUpdate_Prefix(ProjectileShotgun __instance, out bool __state)
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

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileShotgun.TryInit))]
        public static void MyUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

        /// <summary>Use the owning player's (local or remote) transform.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileShotgun.GetAttackDir))]
        public static void GetAttackDir_Prefix(ProjectileShotgun __instance, out bool __state)
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

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileShotgun.GetAttackDir))]
        public static void GetAttackDir_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

        /// <summary>Use the owning player's (local or remote) transform.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileShotgun.GetShootingPosition))]
        public static void GetShootingPosition_Prefix(ProjectileShotgun __instance, out bool __state)
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

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileShotgun.GetShootingPosition))]
        public static void GetShootingPosition_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
}

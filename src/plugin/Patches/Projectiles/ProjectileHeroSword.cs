using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileHeroSword))]
    internal static class ProjectileHeroSwordPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Use the owning player's transform.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileHeroSword.MyUpdate))]
        public static bool MyUpdate_Prefix(ProjectileHeroSword __instance, out bool __state)
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

        /// <summary>Restore the original transform.</summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileHeroSword.MyUpdate))]
        public static void MyUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

        /// <summary>Use the owning player's transform.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileHeroSword.MyFixedUpdate))]
        public static bool MyFixedUpdate_Prefix(ProjectileHeroSword __instance, out bool __state)
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

        /// <summary>Restore the original transform.</summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileHeroSword.MyFixedUpdate))]
        public static void MyFixedUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }


        /// <summary>Use the owning player's transform.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileHeroSword.TryInit))]
        public static bool TryInit_Prefix(ProjectileHeroSword __instance, out bool __state)
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

        /// <summary>Restore the original transform.</summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileHeroSword.TryInit))]
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

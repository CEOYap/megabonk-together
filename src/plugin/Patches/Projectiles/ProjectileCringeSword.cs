using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileCringeSword))]
    internal static class ProjectileCringeSwordPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Swaps in the owning player's transform.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileCringeSword.MyUpdate))]
        public static bool MyUpdate_Prefix(ProjectileCringeSword __instance, out bool __state)
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

        /// <summary>Restores the transform swapped by the prefix.</summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileCringeSword.MyUpdate))]
        public static void MyUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

        /// <summary>Swaps in the owning player's transform.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileCringeSword.MyFixedUpdate))]
        public static bool MyFixedUpdate_Prefix(ProjectileCringeSword __instance, out bool __state)
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

        /// <summary>Restores the transform swapped by the prefix.</summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileCringeSword.MyFixedUpdate))]
        public static void MyFixedUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

        /// <summary>Swaps in the owning player's transform.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileCringeSword.TryInit))]
        public static bool TryInit_Prefix(ProjectileBlackHole __instance, out bool __state)
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

        /// <summary>Restores the transform swapped by the prefix.</summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileCringeSword.TryInit))]
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

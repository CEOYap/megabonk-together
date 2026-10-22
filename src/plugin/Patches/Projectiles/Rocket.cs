using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches.Projectiles
{
    [HarmonyPatch(typeof(Rocket))]
    internal static class RocketPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Uses the owner's transform, and prefix-false on clients for host-owned rockets.
        /// <c>Rocket</c> does not derive from <c>ProjectileBase</c>, so those guards never applied; its
        /// decompiled FixedUpdate flies and detonates on the local clock.
        /// <para><b>UNVERIFIED:</b> the timeout field is read at <c>weaponBase+4</c> in Ghidra's
        /// output, whose applied struct names are known to sit a slot out. Nothing here depends on it.</para>
        /// <para>Keyed on <c>ownerId</c>, not <c>weaponBase</c>: the client sets weaponBase only on
        /// the ProjectileRocket parent, ownerId on this child.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Rocket.FixedUpdate))]
        public static bool MyFixedUpdate_Prefix(Rocket __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;

            if (!isHost)
            {
                // Received rockets carry ownerId and are driven by ProjectileInterpolator.
                return !DynamicData.For(__instance).Get<uint?>("ownerId").HasValue;
            }

            var netPlayer = playerManagerService.GetNetPlayerByWeapon(__instance.weaponBase);
            if (netPlayer == null)
            {
                return true;
            }

            // Host only, matching the finalizer's pop.
            playerManagerService.AddGetNetplayerPositionRequest(netPlayer.ConnectionId);
            __state = true;

            return true;
        }

        /// <summary>Prefix-false on clients for host-owned rockets (decompiled: spawns the explosion and pools the rocket).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Rocket.ProjectileDone))]
        public static bool ProjectileDone_Prefix(Rocket __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;
            if (isHost)
            {
                return true;
            }

            return !DynamicData.For(__instance).Get<uint?>("ownerId").HasValue;
        }

        /// <summary>Restore original transform after prefix</summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(Rocket.FixedUpdate))]
        public static void MyFixedUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }

    [HarmonyPatch(typeof(ProjectileRocket))]
    internal static class ProjectileRocketPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Use the correct player (local / remote) transform</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileRocket.TryInit))]
        public static bool TryInit_Prefix(ProjectileRocket __instance, out bool __state)
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

        /// <summary>Restore original transform after prefix</summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileRocket.TryInit))]
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

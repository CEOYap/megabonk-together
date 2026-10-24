using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using System.Collections.Generic;

namespace OmniBonk.Patches.Projectiles
{
    [HarmonyPatch(typeof(Rocket))]
    internal static class RocketPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Received rockets by Unity instance id, which belongs to the native object. The
        /// <c>DynamicData</c> "ownerId" tag alone is lost: Il2CppInterop caches wrappers weakly
        /// (<c>Il2CppObjectPool</c>) and <c>DynamicData</c> keys on the wrapper, so once the tagging
        /// wrapper is collected the next access gets a fresh, untagged one. The client then ran the
        /// game's flight on top of the interpolator (OB-25: 15,080 LookRotation warnings, all from
        /// <c>Rocket.StepMovement</c>). Main thread only.
        /// </summary>
        private static readonly HashSet<int> receivedRockets = [];

        internal static void MarkReceived(Rocket rocket)
        {
            if (rocket != null)
            {
                receivedRockets.Add(rocket.GetInstanceID());
            }
        }

        internal static void Reset() => receivedRockets.Clear();

        private static bool IsReceived(Rocket rocket)
            => receivedRockets.Contains(rocket.GetInstanceID())
               || DynamicData.For(rocket).Get<uint?>("ownerId").HasValue;

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
                // Received rockets are driven by ProjectileInterpolator.
                return !IsReceived(__instance);
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

            return !IsReceived(__instance);
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

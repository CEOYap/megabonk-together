using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches.ConstantAttacks
{
    //TODO: Synchronize laser beam attacks properly
    [HarmonyPatch(typeof(LaserBeamAttack))]
    internal static class LaserBeamAttackPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Queues the owner's position for the attack's spawn point.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(LaserBeamAttack.Update))]
        public static void FixedUpdate_Prefix(LaserBeamAttack __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }
            var ownerId = DynamicData.For(__instance).Get<uint?>("ownerId");
            if (!ownerId.HasValue)
            {
                return;
            }
            var ownerPlayer = playerManagerService.GetPlayer(ownerId.Value);
            var localPlayer = playerManagerService.GetLocalPlayer();
            if (ownerPlayer.ConnectionId != localPlayer.ConnectionId)
            {
                playerManagerService.AddGetNetplayerPositionRequest(ownerPlayer.ConnectionId);
                __state = true;
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(LaserBeamAttack.Update))]
        public static void FixedUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

        /// <summary>Remote players' beams do not hit enemies locally.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(LaserBeamAttack.HitEnemy))]
        public static bool HitEnemy_Prefix(LaserBeamAttack __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }
            var ownerId = DynamicData.For(__instance).Get<uint?>("ownerId");
            if (!ownerId.HasValue)
            {
                return true;
            }
            var playerManagerService = Plugin.Services.GetService<Services.IPlayerManagerService>();
            var ownerPlayer = playerManagerService.GetPlayer(ownerId.Value);
            var localPlayer = playerManagerService.GetLocalPlayer();
            return ownerPlayer.ConnectionId == localPlayer.ConnectionId;
        }
    }
}

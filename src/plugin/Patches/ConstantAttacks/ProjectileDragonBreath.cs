using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches.ConstantAttacks
{
    [HarmonyPatch(typeof(ProjectileDragonsBreath))]
    internal static class ProjectileDragonBreathPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Queue net player position used for the attack spawn position</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileDragonsBreath.Update))]
        public static void Prefix_Update(ProjectileDragonsBreath __instance, out bool __state)
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

        /// <summary>P1-11: pops on prefix state only; re-derived conditions can change mid-call.</summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileDragonsBreath.Update))]
        public static void Finalizer_Update(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
}

using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace MegabonkTogether.Patches.ConstantAttacks
{
    [HarmonyPatch(typeof(ProjectileDragonsBreath))]
    internal static class ProjectileDragonBreathPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        // Was resolved per call in both patches, on ProjectileDragonsBreath.Update — a DI lookup
        // every frame per beam. Cached to match every other patch class, and needed by the finalizer.
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Queue net player position used for the attack spawn position
        /// </summary>
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

        /// <summary>
        /// Unqueue net player position request after use. See the other converted pairs: the pop
        /// carries no condition of its own, because every condition the old postfix re-derived —
        /// the session flag, the ownerId, and the owner/local comparison — can change between
        /// prefix and postfix and strand the request (P1-11).
        /// </summary>
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

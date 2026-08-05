using Assets.Scripts.Game.Combat.EnemySpecialAttacks;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace MegabonkTogether.Patches.SpecialAttack
{
    [HarmonyPatch(typeof(EnemyProjectileMud))]
    internal static class EnemyProjectileMudPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Intercept projectile to target the correct player instead of the orignal function targeting always the local player.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyProjectileMud.Init))]
        public static void Init_Prefix(EnemyProjectileMud __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var targetId = DynamicData.For(__instance.enemy).Get<uint?>("targetId");
            if (targetId.HasValue)
            {
                playerManagerService.AddGetNetplayerPositionRequest(targetId.Value);
                __state = true;
            }

        }

        /// <summary>
        /// Remove queued request after attack is over.
        /// </summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(EnemyProjectileMud.Init))]
        public static void Init_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
}

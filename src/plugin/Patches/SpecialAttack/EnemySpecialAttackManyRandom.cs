using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using static EnemySpecialAttackManyRandom;

namespace MegabonkTogether.Patches.SpecialAttack
{
    [HarmonyPatch(typeof(_DoAttack_d__8))]
    internal static class EnemySpecialAttackManyRandomPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Intercept projectile to target the correct player instead of the orignal function targeting always the local player.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(_DoAttack_d__8.MoveNext))]
        public static void MoveNext_Prefix(_DoAttack_d__8 __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var targetId = DynamicData.For(__instance.__4__this.enemy).Get<uint?>("targetId");
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
        [HarmonyPatch(nameof(_DoAttack_d__8.MoveNext))]
        public static void MoveNext_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }

    }
}

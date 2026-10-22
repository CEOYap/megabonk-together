using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.SpecialAttack
{
    [HarmonyPatch(typeof(EnemySpecialAttackCactusProjectile))]
    internal static class EnemySpecialAttackCactusProjectilePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Aim at the enemy's actual target; the original always targets the local player.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemySpecialAttackCactusProjectile.Init))]
        public static void Init_Prefix(EnemySpecialAttackCactusProjectile __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }
            var targetId = MonoMod.Utils.DynamicData.For(__instance.enemy).Get<uint?>("targetId");
            if (targetId.HasValue)
            {
                playerManagerService.AddGetNetplayerPositionRequest(targetId.Value);
                __state = true;
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(EnemySpecialAttackCactusProjectile.Init))]
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

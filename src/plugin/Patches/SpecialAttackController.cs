using Assets.Scripts.Game.Combat.EnemySpecialAttacks;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(SpecialAttackController))]
    internal static class SpecialAttackControllerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Only the host uses enemy special attacks, unless explicitly allowed.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(SpecialAttackController.UseSpecialAttack))]
        public static bool UseSpecialAttack_Prefix(SpecialAttackController __instance, EnemySpecialAttack attack)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return Plugin.CAN_ENEMY_USE_SPECIAL_ATTACK;
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(SpecialAttackController.UseSpecialAttack))]
        public static void UseSpecialAttack_Postfix(SpecialAttackController __instance, EnemySpecialAttack attack)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;

            if (!isServer)
            {
                return;
            }

            synchronizationService.OnSpawnedEnemySpecialAttack(__instance.enemy, attack);
        }
    }
}

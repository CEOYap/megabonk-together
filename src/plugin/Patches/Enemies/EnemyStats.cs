using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory.Stats;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Enemies
{
    [HarmonyPatch(typeof(EnemyStats))]
    internal static class EnemyStatsPatches
    {
        private static readonly IGameBalanceService gameBalanceService = Plugin.Services.GetService<IGameBalanceService>();
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Adjust enemy HP based on netplay configuration
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(EnemyStats.GetHp))]
        private static void GetHpPostfix(Enemy enemy, ref float __result)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return;
            }

            var multiplier = gameBalanceService.GetEnemyHpMultiplier(enemy.enemyFlag);
            __result *= multiplier;
        }

        /// <summary>
        /// Endless (Phase 4): enemy damage to players grows per boss loop. <c>GetDamage</c>
        /// (VA 0x18041B290) feeds <c>DamageUtility.GetPlayerDamage</c> and the enemies' special
        /// attacks (xrefs, decompiled), and a player takes damage on their own machine, so this runs
        /// on every peer, unlike the host-only HP above. Every peer has the same loop count, since
        /// every peer follows the host's route. No ownership check: each peer scales the damage its
        /// own player takes. <b>UNVERIFIED, not run in-game.</b>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(EnemyStats.GetDamage))]
        private static void GetDamagePostfix(ref float __result)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            __result *= gameBalanceService.GetEnemyDamageMultiplier();
        }
    }
}

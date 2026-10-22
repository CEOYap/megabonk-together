using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory.Stats;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Enemies
{
    [HarmonyPatch(typeof(EnemyStats))]
    internal static class EnemyStatsPatches
    {
        private static readonly IGameBalanceService gameBalanceService = Plugin.Services.GetService<IGameBalanceService>();
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Host scales enemy HP per lobby settings.</summary>
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
        /// Endless: enemy damage grows per boss loop. <c>GetDamage</c> (VA 0x18041B290, decompiled) feeds
        /// player damage, taken locally, so this runs on every peer with no ownership check.
        /// <b>UNVERIFIED, not run in-game.</b>
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

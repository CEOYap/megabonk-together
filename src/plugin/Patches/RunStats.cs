using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// OB-14. Keeps a teammate's damage out of this player's end-of-run damage panel.
    ///
    /// <para><b>The defect.</b> RunStats.OnEnemyDamaged (VA 0x180411930, decompiled) is a static
    /// handler on the global Enemy.A_Damage event. It looks the hit's damageSource up in a
    /// per-source table and adds the damage -- that table is the panel. It runs for every hit
    /// applied on this machine, and in netplay that includes the teammate's: hits replayed from
    /// EnemyDamaged, and on the host the projectiles and auras it simulates for remote players. So
    /// each player's panel listed the other's weapons and items at nearly their full totals, at
    /// LVL 0 because the viewer does not own them. Confirmed from both end screens of the
    /// 2026-10-11 run: Wireless Dagger at LVL 0 / 644.5K on the host against LVL 10 / 602.4K on the
    /// client that owns it; Lightning Staff at LVL 0 / 1.1M on the client against LVL 8 / 1.2M on
    /// the host that owns it.</para>
    ///
    /// <para><b>How the owner is known.</b> Every path that applies a teammate's hit already tells
    /// the tracker whose hit it is, for kill credit: OnReceivedEnemyDamaged sets the attacker,
    /// ProjectileBase.HitEnemy sets the weapon's owner on the host, and CombatAura now does the same.
    /// This only reads it. A hit with no attribution is treated as ours, which is what it was
    /// before, so a gap in attribution can only under-correct, never hide our own damage.</para>
    /// </summary>
    [HarmonyPatch(typeof(Assets.Scripts.Saves___Serialization.Progression.Stats.RunStats))]
    internal static class RunStatsPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDamaged")]
        public static bool OnEnemyDamaged_Prefix(Enemy enemy, DamageContainer dc)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var attacker = trackerService.GetCurrentPlayerId();
            if (!attacker.HasValue)
            {
                return true;
            }

            // Record only our own damage. Prefix-false is deliberate: the original does nothing
            // but add to the panel's per-source table.
            return playerManagerService.IsLocalConnectionId(attacker.Value);
        }
    }
}

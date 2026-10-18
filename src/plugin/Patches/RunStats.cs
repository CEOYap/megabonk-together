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
                ReportForeignSource(dc, attacker);
                return true;
            }

            // Record only our own damage. Prefix-false is deliberate: the original does nothing
            // but add to the panel's per-source table.
            var ours = playerManagerService.IsLocalConnectionId(attacker.Value);
            if (ours)
            {
                ReportForeignSource(dc, attacker);
            }

            return ours;
        }

        private static readonly System.Collections.Generic.HashSet<string> checkedSources = [];

        /// <summary>
        /// OB-14 diagnostic. The first time a source is about to be recorded as ours, checks that we
        /// own a weapon or item of that name, and says which path recorded it if we do not.
        ///
        /// <para><b>Why.</b> On 2026-10-18 the client's panel listed the host's Aura (2,331) and
        /// Chunkers (946) at LVL 0. Every path read offline attributes those hits to the host --
        /// the host stamps <c>AttackerId</c> from the tracker, and the client replays with it -- so
        /// whatever records them is a path not yet found. "Unattributed" means a hit applied here
        /// with no tracker scope at all; "attributed to us" means something stamped our id on it.
        /// <c>replaying</c> is true inside <c>OnReceivedEnemyDamaged</c>.</para>
        ///
        /// <para>Once per source per session, so it costs a set lookup per hit. Status effects
        /// (Burn, Poison, ...) are not weapons or items and will be reported once each; that is
        /// expected, not a leak.</para>
        /// </summary>
        private static void ReportForeignSource(DamageContainer dc, uint? attacker)
        {
            var source = dc?.damageSource;
            if (string.IsNullOrEmpty(source) || !checkedSources.Add(source))
            {
                return;
            }

            var inventory = GameManager.Instance?.player?.inventory;
            if (inventory == null)
            {
                return;
            }

            if (inventory.weaponInventory?.weapons != null)
            {
                foreach (var weapon in inventory.weaponInventory.weapons.Keys)
                {
                    if (weapon.ToString() == source)
                    {
                        return;
                    }
                }
            }

            if (inventory.itemInventory?.items != null)
            {
                foreach (var item in inventory.itemInventory.items.Keys)
                {
                    if (item.ToString() == source)
                    {
                        return;
                    }
                }
            }

            var how = attacker.HasValue ? $"attributed to us ({attacker.Value})" : "unattributed";
            Plugin.Log.LogInfo(
                $"OB-14: recording '{source}' as our damage, but we own no weapon or item by that name -- " +
                $"{how}, replaying {Plugin.Instance.CAN_DAMAGE_ENEMIES}, host {synchronizationService.IsServerMode() ?? false}. " +
                "A teammate's source here is the leak; a status effect is expected.");
        }

        /// <summary>Clears the once-per-source memory so each session reports afresh.</summary>
        internal static void ResetDiagnostics() => checkedSources.Clear();
    }
}

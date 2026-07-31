using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(WeaponUtility))]
    internal static class WeaponUtilityPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Host.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Synchronize lightning strike weapon
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponUtility.LightningStrike))]
        public static void LightningStrike_Postfix(Enemy enemy, int bounces, DamageContainer dc, float bounceRange, float bounceProcCoefficient)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return;
            }

            synchronizationService.OnLightningStrike(enemy, bounces, dc, bounceRange, bounceProcCoefficient);
        }

        // FIX P1-5: the GetDamageContainer postfix that used to live here has been removed.
        //
        // It stamped an "ownerId" onto the returned DamageContainer via DynamicData, intending to
        // stop money flying to the wrong player. Nothing ever read it back. A repo-wide search for
        // a DamageContainer ownerId read finds exactly one site — the postfix's own
        // "already assigned?" guard, checking a value only it wrote. The attribution was
        // write-only, so removing it changes no behaviour.
        //
        // It could not have worked in any case. WeaponUtility caches its DamageContainer in
        // _StaticFields — one instance shared process-wide, recycled per hit via Reuse() — so a
        // side table keyed on reference identity collapses every player's attacks into one entry.
        // The guard then read that shared stale entry and skipped the reassignment.
        //
        // Kept out rather than repaired: GetDamageContainer is on the per-hit path, and this was
        // a DynamicData lookup plus a possible dictionary write on every weapon attack, for a
        // value with no consumer. Real attribution runs through ITrackerService (kills) and
        // per-attack / per-projectile / per-pickup ownerId entries, none of which touch this.
        //
        // See P1-5 in docs/netplay/01-critical-fixes.md.
    }
}

using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(WeaponUtility))]
    internal static class WeaponUtilityPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Host.Services.GetService<ISynchronizationService>();

        /// <summary>Synchronize lightning strikes.</summary>
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

            // OB-10: only the host may broadcast this.
            if (!(synchronizationService.IsServerMode() ?? false))
            {
                return;
            }

            synchronizationService.OnLightningStrike(enemy, bounces, dc, bounceRange, bounceProcCoefficient);
        }

        // FIX P1-5: no GetDamageContainer ownerId postfix. The container is a shared static recycled
        // per hit, so per-reference attribution cannot work; use ITrackerService instead.
    }
}

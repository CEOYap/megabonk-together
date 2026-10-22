using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches.Enemies
{
    /// <summary>
    /// OB-14. Attributes every weapon hit the host applies for a teammate at the common choke point:
    /// <c>WeaponUtility.GetDamageContainer</c> (33 hit sites, some overrides skip the base hit method).
    /// <para>The postfix notes the owner; the Enemy.Damage prefix opens a tracker scope for a hit
    /// carrying the same (shared, recycled, P1-5) container, and the finalizer closes it. Narrower
    /// scopes already open are left alone. Host only.</para>
    /// </summary>
    internal static class WeaponHitAttribution
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        /// <summary>The teammate whose weapon built the current weapon hit. Main thread only.</summary>
        private static uint? notedOwner;

        /// <summary>The hit object that weapon returned. The game recycles one shared instance.</summary>
        private static System.IntPtr notedHit;

        internal static void NoteWeapon(WeaponBase weaponBase, DamageContainer hit)
        {
            if (!synchronizationService.HasNetplaySessionStarted() || !(synchronizationService.IsServerMode() ?? false))
            {
                notedOwner = null;
                notedHit = System.IntPtr.Zero;
                return;
            }

            // OB-41: the inventory store, which survives a level change.
            notedOwner = playerManagerService.GetWeaponOwnerId(weaponBase);
            notedHit = hit == null ? System.IntPtr.Zero : hit.Pointer;
        }

        /// <summary>True when a scope was opened, which End must close.</summary>
        /// <remarks>Matched on the hit object, not consumed once: one hit can be applied to several enemies.</remarks>
        internal static bool Begin(DamageContainer hit)
        {
            if (!notedOwner.HasValue || hit == null || hit.Pointer != notedHit)
            {
                return false;
            }

            var owner = notedOwner;
            if (trackerService.GetCurrentPlayerId().HasValue)
            {
                return false;
            }

            trackerService.SetCurrentPlayerId(owner.Value);
            return true;
        }

        internal static void End(bool began)
        {
            if (began)
            {
                trackerService.UnsetCurrentPlayerId();
            }
        }
    }

    [HarmonyPatch(typeof(WeaponUtility))]
    internal static class WeaponHitAttributionNotePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponUtility.GetDamageContainer),
            new[] { typeof(WeaponBase), typeof(ProjectileBase), typeof(Enemy), typeof(Vector3), typeof(float) })]
        public static void GetDamageContainer_Postfix(WeaponBase weaponBase, DamageContainer __result)
            => WeaponHitAttribution.NoteWeapon(weaponBase, __result);
    }

    [HarmonyPatch(typeof(Enemy))]
    internal static class WeaponHitAttributionScopePatches
    {
        // First, so the scope is open before any other prefix reads attribution.
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(nameof(Enemy.Damage))]
        public static void Damage_Prefix(DamageContainer damageContainer, out bool __state)
            => __state = WeaponHitAttribution.Begin(damageContainer);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(Enemy.Damage))]
        public static void Damage_Finalizer(bool __state) => WeaponHitAttribution.End(__state);
    }
}

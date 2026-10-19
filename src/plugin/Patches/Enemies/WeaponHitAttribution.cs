using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace MegabonkTogether.Patches.Enemies
{
    /// <summary>
    /// OB-14. Attributes every weapon hit the host applies on a teammate's behalf, at the one place
    /// all of them pass through, instead of chasing each attack's own hit method.
    ///
    /// <para><b>Why.</b> Attribution was being added one hit method at a time (ProjectileBase.HitEnemy,
    /// CombatAura, the four constant attacks). The 2026-10-18 end screens showed the gap that
    /// approach leaves: the teammate's Dice and Slutty Rocket still on the host's panel at LVL 0,
    /// because Rocket.HitEnemy and ProjectileDice.Hitscan are overrides that never call the patched
    /// base method. An xref on WeaponUtility.GetDamageContainer(WeaponBase, ...) gives 33 separate
    /// hit sites.</para>
    ///
    /// <para><b>How.</b> Every one of those builds its hit with GetDamageContainer and then applies it
    /// with Enemy.Damage, in the same call. The postfix notes the weapon's owner when it is a
    /// teammate; the Damage prefix turns that note into a tracker scope for exactly that hit, and the
    /// finalizer closes it. Inside the scope sit everything that reads attribution: the damage panel
    /// (RunStatsPatches), kill credit (Enemy.EnemyDied_Prefix) and the AttackerId the host stamps on
    /// the hit it forwards.</para>
    ///
    /// <para><b>Not on the hit object.</b> The game recycles one shared DamageContainer for every
    /// weapon hit (P1-5), so the owner is held here alongside that object, and Damage is attributed when
    /// it carries the same object. The next weapon hit always overwrites the note -- with no owner for
    /// our own weapons, so it can never go stale onto our damage.
    /// Hits already attributed by a narrower scope (a replayed hit, the per-attack patches) are left
    /// alone.</para>
    ///
    /// <para><b>Host only.</b> A client never applies its own hits; the host forwards them back.</para>
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
        /// <remarks>
        /// Matched on the hit object rather than consumed once. An attack may build its hit once and
        /// apply it to several enemies; every one of those Damage calls carries the same object and
        /// gets the same owner. Damage built any other way -- an item proc, which passes its own
        /// container to the other GetDamageContainer overload -- does not match and is left to
        /// whatever scope it is already in. The next weapon hit always overwrites the note, with no
        /// owner for our own weapons.
        /// </remarks>
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

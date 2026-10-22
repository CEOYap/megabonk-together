using Assets.Scripts.Game.Combat.ConstantAttacks;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.ConstantAttacks
{
    /// <summary>
    /// OB-14: scopes the tracker to the teammate whose attack the host is running, for damage panel
    /// and kill credit. Per attack (prefix/finalizer), not per hit: the game recycles one static
    /// DamageContainer (P1-5). Host only; local attacks are left alone.
    /// </summary>
    internal static class RemoteAttackAttribution
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        /// <summary>True when an owner was set, which the matching End must then clear.</summary>
        internal static bool Begin(WeaponBase weaponBase)
        {
            if (weaponBase == null
                || !synchronizationService.HasNetplaySessionStarted()
                || !(synchronizationService.IsServerMode() ?? false))
            {
                return false;
            }

            // OB-41: the inventory store, which survives a level change.
            var owner = playerManagerService.GetWeaponOwnerId(weaponBase);
            if (!owner.HasValue)
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

    /// <summary>Chunkers: its orbiting projectiles deal damage here.</summary>
    [HarmonyPatch(typeof(RotatingProjectiles))]
    internal static class RotatingProjectilesAttributionPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(RotatingProjectiles.HitEnemy))]
        public static void HitEnemy_Prefix(RotatingProjectiles __instance, out bool __state)
            => __state = RemoteAttackAttribution.Begin(__instance.weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(RotatingProjectiles.HitEnemy))]
        public static void HitEnemy_Finalizer(bool __state) => RemoteAttackAttribution.End(__state);
    }

    /// <summary>Frostwalker: IceAura builds its hits inside FixedUpdate.</summary>
    [HarmonyPatch(typeof(IceAura))]
    internal static class IceAuraAttributionPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(IceAura.FixedUpdate))]
        public static void FixedUpdate_Prefix(IceAura __instance, out bool __state)
            => __state = RemoteAttackAttribution.Begin(__instance.weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(IceAura.FixedUpdate))]
        public static void FixedUpdate_Finalizer(bool __state) => RemoteAttackAttribution.End(__state);
    }

    /// <summary>Dragon's Breath.</summary>
    [HarmonyPatch(typeof(ProjectileDragonsBreath))]
    internal static class DragonsBreathAttributionPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileDragonsBreath.HitEnemy))]
        public static void HitEnemy_Prefix(ProjectileDragonsBreath __instance, out bool __state)
            => __state = RemoteAttackAttribution.Begin(__instance.weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileDragonsBreath.HitEnemy))]
        public static void HitEnemy_Finalizer(bool __state) => RemoteAttackAttribution.End(__state);
    }

    /// <summary>Space Noodle: the laser hits in HitEnemy and once more in StopLaser.</summary>
    [HarmonyPatch(typeof(LaserBeamAttack))]
    internal static class LaserBeamAttributionPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(LaserBeamAttack.HitEnemy))]
        public static void HitEnemy_Prefix(LaserBeamAttack __instance, out bool __state)
            => __state = RemoteAttackAttribution.Begin(__instance.weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(LaserBeamAttack.HitEnemy))]
        public static void HitEnemy_Finalizer(bool __state) => RemoteAttackAttribution.End(__state);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LaserBeamAttack.StopLaser))]
        public static void StopLaser_Prefix(LaserBeamAttack __instance, out bool __state)
            => __state = RemoteAttackAttribution.Begin(__instance.weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(LaserBeamAttack.StopLaser))]
        public static void StopLaser_Finalizer(bool __state) => RemoteAttackAttribution.End(__state);
    }
}

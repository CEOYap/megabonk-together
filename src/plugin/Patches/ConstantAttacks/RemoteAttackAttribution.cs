using Assets.Scripts.Game.Combat.ConstantAttacks;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.ConstantAttacks
{
    /// <summary>
    /// OB-14. Tells the tracker whose attack is hitting, for the attacks the host runs on behalf of
    /// a teammate's avatar, so the end-screen damage panel (RunStatsPatches) and kill credit
    /// (Enemy.EnemyDied_Prefix) go to the right player.
    ///
    /// <para><b>Why per attack, not per hit.</b> Every weapon hit is built by
    /// WeaponUtility.GetDamageContainer(WeaponBase, ...) -- 33 callers, these four among them -- but
    /// the game caches ONE DamageContainer in static fields and recycles it per hit, so anything
    /// attached to the hit object is overwritten by the next hit. That is why the earlier
    /// GetDamageContainer postfix was removed (P1-5). Scoping the owner to the attack's own call,
    /// set in a prefix and cleared in a finalizer, is the same shape ProjectileBase.HitEnemy and
    /// CombatAura.FixedUpdate already use, and nests correctly.</para>
    ///
    /// <para><b>Host only.</b> A client blocks its own Enemy.Damage outside replays, so its copies
    /// of a teammate's attacks never record anything. A local attack resolves to no NetPlayer and is
    /// left alone, so it stays attributed to us.</para>
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

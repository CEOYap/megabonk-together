using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using HarmonyLib;
using MegabonkTogether.Patches.Enemies;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace MegabonkTogether.Patches.Items
{
    /// <summary>
    /// OB-37. A hit's item effects come from the player who landed it, and only the host runs them.
    ///
    /// <para><b>What the game does</b> (decompiled, build 21750826). Both item hooks on a hit read
    /// the <i>local</i> player's items, whoever owns the weapon:</para>
    /// <list type="bullet">
    /// <item><c>Enemy.DamageFromPlayerWeapon</c> (VA 0x1804AC700) and <c>DamageFromPlayerOther</c>
    /// (0x1804AC650) call <c>Damage</c>, then <c>MyPlayer.Instance.inventory.itemInventory.PostDamage</c>.
    /// <c>PostDamage</c> (0x180442BF0) runs every on-hit item in <i>that</i> inventory, repeated per
    /// Overpowered Lamp (EItem 26).</item>
    /// <item>Both <c>WeaponUtility.GetDamageContainer</c> overloads (0x180434A50, 0x180434FF0) run the
    /// local player's <c>ItemInventory.PreAttack</c> (0x180442E90).</item>
    /// </list>
    ///
    /// <para><b>What that did in netplay.</b> The host runs every player's weapons, so every teammate
    /// hit got the host's pre-attack modifiers and the host's on-hit procs, credited to the teammate
    /// (2026-10-19: the host's Big Bonk, Spicy Meatball, Ice Cube and Power Gloves replayed on Player2
    /// as Player2's). A teammate's own on-hit items ran only where their machine happened to call
    /// <c>PostDamage</c> itself: inside a replayed hit, through a damage listener such as Tony
    /// McZoom's Zap passive (<c>PassiveAbilityZooma.OnEnemyDamage</c>, 0x1804800F0), which applied
    /// client-only damage and forwarded the item hits.</para>
    ///
    /// <para><b>Now.</b> On the host, a hit the tracker attributes to a teammate runs that teammate's
    /// <c>PostDamage</c> and <c>PreAttack</c> instead of the host's. A client skips <c>PostDamage</c>
    /// entirely: it applies no hits of its own, the host applies them all, so the host is the one
    /// place each hit's procs run. Without that the procs that already reached the host through a
    /// client's forward would now run twice.</para>
    ///
    /// <para><b>Stats.</b> The items' numbers come from the teammate too: OB-38's GetStat postfix
    /// covers the stats they read, and baseDamage is swapped around the redirected call. A hit the
    /// host cannot attribute to anyone still runs the host's items, as before.</para>
    ///
    /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
    /// </summary>
    internal static class HitItemOwnership
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        private static bool loggedRedirect = false;

        /// <summary>Host only: the teammate's item inventory when this hit is theirs, else null.</summary>
        internal static ItemInventory TeammateInventoryFor(ItemInventory instance, out uint owner)
        {
            owner = 0;
            if (!IsLocal(instance))
            {
                return null;
            }

            // The weapon scope when one is open (GetDamageContainer, where PreAttack runs before
            // any tracker scope exists), else the tracker. See TeammateCombatStats.
            var teammateId = TeammateCombatStats.CurrentTeammate();
            if (!teammateId.HasValue)
            {
                return null;
            }

            owner = teammateId.Value;
            var teammate = playerManagerService.FindPlayerInventory(owner)?.itemInventory;
            if (teammate == null)
            {
                return null;
            }

            if (!loggedRedirect)
            {
                loggedRedirect = true;
                Plugin.Log.LogInfo($"[items] OB-37: running {owner}'s on-hit items for their hit instead of the host's (logged once per session).");
            }

            return teammate;
        }

        internal static bool IsClientSession()
            => synchronizationService.HasNetplaySessionStarted() && !(synchronizationService.IsServerMode() ?? false);

        internal static void ResetDiagnostics() => loggedRedirect = false;

        private static bool IsLocal(ItemInventory instance)
        {
            var local = GameManager.Instance?.player?.inventory?.itemInventory;
            return instance != null && local != null && instance.Pointer == local.Pointer;
        }
    }

    [HarmonyPatch(typeof(ItemInventory))]
    internal static class ItemInventoryOwnershipPatches
    {
        // Prefix-false is deliberate in both branches: the original would run the wrong player's
        // items (host) or procs for a hit this machine never applied (client).
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemInventory.PostDamage))]
        public static bool PostDamage_Prefix(ItemInventory __instance, DamageContainer dc)
        {
            if (HitItemOwnership.IsClientSession())
            {
                return false;
            }

            var teammate = HitItemOwnership.TeammateInventoryFor(__instance, out var owner);
            if (teammate == null)
            {
                return true;
            }

            // OB-38. Several on-hit items (Thunder, Cursed and Blood Mitts) scale by
            // MyPlayer.baseDamage, a raw field no GetStat postfix can reach.
            var savedBaseDamage = TeammateCombatStats.EnterBaseDamage(owner);
            try
            {
                // Re-enters this prefix with the teammate's inventory, which is not local, so it runs.
                teammate.PostDamage(dc);
            }
            finally
            {
                TeammateCombatStats.ExitBaseDamage(savedBaseDamage);
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemInventory.PreAttack))]
        public static bool PreAttack_Prefix(ItemInventory __instance, DamageContainer dc, StatComponents itemModifierStatComponents, ref StatComponents __result)
        {
            var teammate = HitItemOwnership.TeammateInventoryFor(__instance, out _);
            if (teammate == null)
            {
                return true;
            }

            __result = teammate.PreAttack(dc, itemModifierStatComponents);
            return false;
        }
    }

    [HarmonyPatch(typeof(WeaponUtility))]
    internal static class DamageContainerOwnerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(WeaponUtility.GetDamageContainer),
            new[] { typeof(WeaponBase), typeof(ProjectileBase), typeof(Enemy), typeof(Vector3), typeof(float) })]
        public static void GetDamageContainer_Prefix(WeaponBase weaponBase, out TeammateCombatStats.WeaponScope __state)
            => __state = TeammateCombatStats.EnterWeapon(weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(WeaponUtility.GetDamageContainer),
            new[] { typeof(WeaponBase), typeof(ProjectileBase), typeof(Enemy), typeof(Vector3), typeof(float) })]
        public static void GetDamageContainer_Finalizer(TeammateCombatStats.WeaponScope __state)
            => TeammateCombatStats.ExitWeapon(__state);
    }

    /// <summary>
    /// Keeps the teammate attribution open across the whole weapon hit. WeaponHitAttribution scopes
    /// only <c>Enemy.Damage</c>, and <c>PostDamage</c> runs after it returns.
    /// </summary>
    [HarmonyPatch(typeof(Enemy))]
    internal static class DamageFromPlayerWeaponScopePatches
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(nameof(Enemy.DamageFromPlayerWeapon))]
        public static void DamageFromPlayerWeapon_Prefix(DamageContainer dc, out bool __state)
            => __state = WeaponHitAttribution.Begin(dc);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(Enemy.DamageFromPlayerWeapon))]
        public static void DamageFromPlayerWeapon_Finalizer(bool __state)
            => WeaponHitAttribution.End(__state);
    }
}

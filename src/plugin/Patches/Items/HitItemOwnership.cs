using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using HarmonyLib;
using OmniBonk.Patches.Enemies;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches.Items
{
    /// <summary>
    /// OB-37: a hit's item effects use the hitter's inventory, and only the host runs them.
    /// <para>Decompiled: <c>DamageFromPlayerWeapon</c>/<c>Other</c> (VA 0x1804AC700, 0x1804AC650) call
    /// the local player's <c>PostDamage</c> (0x180442BF0); <c>GetDamageContainer</c> (0x180434A50,
    /// 0x180434FF0) runs the local <c>PreAttack</c> (0x180442E90). So the host's items ran on
    /// teammates' hits. Clients skip <c>PostDamage</c> so procs run once, on the host.</para>
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

            // Weapon scope if open (PreAttack runs before any tracker scope), else the tracker.
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

            // OB-38: some items read MyPlayer.baseDamage, which no GetStat postfix reaches.
            var savedBaseDamage = TeammateCombatStats.EnterBaseDamage(owner);

            // OB-43: some items start from MyPlayer's transform; the position-request FIFO redirects it.
            var pushedPosition = PositionRequest.Enter(owner);
            try
            {
                // Re-enters this prefix with the teammate's inventory, which is not local, so it runs.
                teammate.PostDamage(dc);
            }
            finally
            {
                PositionRequest.Exit(pushedPosition);
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

using Assets.Scripts.Inventory.Stats;
using Assets.Scripts.Menu.Shop;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Weapons
{
    /// <summary>
    /// OB-1: remote attacks use the owner's global stats, not the observer's. Decompiled (VA
    /// <c>0x1804346F0</c>, <c>0x1804347A0</c>): <c>PlayerStats.GetStat</c> is static and reads the local player.
    /// Each patch bails unless the weapon belongs to a remote player.
    /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
    /// </summary>
    [HarmonyPatch(typeof(WeaponUtility))]
    internal static class WeaponUtilityPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IPlayerStatsSyncService playerStatsSyncService = Plugin.Services.GetService<IPlayerStatsSyncService>();

        /// <summary>EWeapon.Shotgun. GetAttackQuantity returns a hardcoded 2 for it, before any stat is read.</summary>
        private const int ShotgunEWeapon = 29;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponUtility.GetAttackQuantity))]
        public static void GetAttackQuantity_Postfix(WeaponBase weaponBase, ref int __result)
        {
            // Shotgun never reaches the stat sum, so there is no observer contribution to remove.
            if (weaponBase?.weaponData != null && (int)weaponBase.weaponData.eWeapon == ShotgunEWeapon)
            {
                return;
            }

            var owner = ResolveRemoteOwner(weaponBase);
            if (owner == null)
            {
                return;
            }

            var ownerStat = playerStatsSyncService.GetRemoteStat(owner.Value, (int)EStat.Projectiles);
            if (ownerStat == null)
            {
                return;
            }

            var corrected = __result - (int)PlayerStats.GetStat(EStat.Projectiles) + (int)ownerStat.Value;

            // At least one projectile.
            __result = corrected < 1 ? 1 : corrected;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponUtility.GetAttackSizeMultiplier))]
        public static void GetAttackSizeMultiplier_Postfix(WeaponBase weaponBase, ref float __result)
        {
            var owner = ResolveRemoteOwner(weaponBase);
            if (owner == null)
            {
                return;
            }

            var ownerStat = playerStatsSyncService.GetRemoteStat(owner.Value, (int)EStat.SizeMultiplier);
            if (ownerStat == null)
            {
                return;
            }

            // Decompiled (VA 0x1804347A0): GetStat(Size) * weaponStats[Size], capped at
            // maxSizeMultiplier when above 1. It multiplies, so subtract-and-add is wrong here.
            // (Ghidra mislabels the cap as projectileSpeed; dump.cs says maxSizeMultiplier.)
            var weaponSize = 1f;
            var weaponStats = weaponBase.weaponStats;
            if (weaponStats != null && weaponStats.ContainsKey(EStat.SizeMultiplier))
            {
                weaponSize = weaponStats[EStat.SizeMultiplier];
            }

            var corrected = ownerStat.Value * weaponSize;

            var weaponData = weaponBase.weaponData;
            if (weaponData != null && weaponData.maxSizeMultiplier > 1f && corrected > weaponData.maxSizeMultiplier)
            {
                corrected = weaponData.maxSizeMultiplier;
            }

            if (corrected > 0f)
            {
                __result = corrected;
            }
        }

        /// <summary>
        /// OB-18 class: <c>GetProjectileBounces</c> (VA <c>0x180435930</c>) has the same additive shape.
        /// Does not fix OB-18 as reported (the local stat panel).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponUtility.GetProjectileBounces))]
        public static void GetProjectileBounces_Postfix(WeaponBase weaponBase, ref int __result)
        {
            var owner = ResolveRemoteOwner(weaponBase);
            if (owner == null)
            {
                return;
            }

            var ownerStat = playerStatsSyncService.GetRemoteStat(owner.Value, (int)EStat.ProjectileBounces);
            if (ownerStat == null)
            {
                return;
            }

            var corrected = __result - (int)PlayerStats.GetStat(EStat.ProjectileBounces) + (int)ownerStat.Value;

            // Zero bounces is a real, common value; negative is not.
            __result = corrected < 0 ? 0 : corrected;
        }

        /// <summary>Remote owner's id, or null for the local player. Also the netplay guard (cheap check first).</summary>
        private static uint? ResolveRemoteOwner(WeaponBase weaponBase)
        {
            if (weaponBase == null || !synchronizationService.HasNetplaySessionStarted())
            {
                return null;
            }

            var netPlayer = playerManagerService.GetNetPlayerByWeapon(weaponBase);
            return netPlayer == null ? null : netPlayer.ConnectionId;
        }
    }
}

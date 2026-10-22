using Assets.Scripts.Inventory__Items__Pickups.Weapons.Attacks;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(WeaponAttack))]
    internal static class WeaponAttackPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Synchronize projectile spawn</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponAttack.SuccessfullySpawnedProjectile))]
        public static void SuccessfullySpawnedProjectile_Postfix(WeaponAttack __instance, Il2CppObjectBase projectile)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (__instance.weaponBase.weaponData.eWeapon == EWeapon.LightningStaff)
            {
                // Dealt in LightningBolt patch
                return;
            }

            var netplayer = playerManagerService.GetNetPlayerByWeapon(__instance.weaponBase);
            synchronizationService.OnSpawnedProjectile(projectile, netplayer?.ConnectionId);
        }

        /// <summary>Use the remote player's position when they spawn the projectile.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(WeaponAttack.SpawnProjectile))]
        public static void SpawnProjectile_Prefix(WeaponAttack __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var netplayer = playerManagerService.GetNetPlayerByWeapon(__instance.weaponBase);
            if (netplayer == null)
            {
                return;
            }

            playerManagerService.AddGetNetplayerPositionRequest(netplayer.ConnectionId);

            __state = true;

        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(WeaponAttack.SpawnProjectile))]
        public static void SpawnProjectile_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
    /// <summary>
    /// Lobby setting: No Projectile Cap. Decompiled <c>FixedUpdateAttack</c> (VA 0x180450150) drops
    /// projectiles past <c>maxNumProjectilesWithoutInterval</c> (offset 0x60, set to 80 in the ctor
    /// 0x1804515D0; Ghidra labels it one slot off). <b>LIKELY</b> by offset.
    /// <para>Lobby-wide because the host simulates teammates' volleys. Applied in <c>SetAttack</c>
    /// (0x1804505F0); the original is restored afterwards. <b>UNVERIFIED, not run in-game.</b></para>
    /// </summary>
    [HarmonyPatch(typeof(WeaponAttack))]
    internal static class NoProjectileCapPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly ILobbySettingsService lobbySettingsService = Plugin.Services.GetService<ILobbySettingsService>();

        private static readonly Dictionary<int, (WeaponAttack Attack, int Original)> originals = [];

        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponAttack.SetAttack))]
        public static void SetAttack_Postfix(WeaponAttack __instance)
        {
            if (__instance == null)
            {
                return;
            }

            if (!synchronizationService.HasNetplaySessionStarted() || !lobbySettingsService.NoProjectileCap)
            {
                if (originals.Count > 0)
                {
                    Restore(__instance);
                }

                return;
            }

            var key = __instance.GetInstanceID();
            if (!originals.ContainsKey(key))
            {
                originals[key] = (__instance, __instance.maxNumProjectilesWithoutInterval);
            }

            __instance.maxNumProjectilesWithoutInterval = int.MaxValue;
        }

        private static void Restore(WeaponAttack attack)
        {
            if (originals.Remove(attack.GetInstanceID(), out var saved))
            {
                attack.maxNumProjectilesWithoutInterval = saved.Original;
            }
        }

        /// <summary>Session over: every attack still alive gets the game's value back.</summary>
        internal static void RestoreAll()
        {
            foreach (var (attack, original) in originals.Values)
            {
                if (attack != null)
                {
                    attack.maxNumProjectilesWithoutInterval = original;
                }
            }

            originals.Clear();
        }
    }
}
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Attacks;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(WeaponAttack))]
    internal static class WeaponAttackPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Synchronize projectile spawn
        /// </summary>  
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

        /// <summary>
        /// Use remote player position when they are the one spawning the projectile
        /// </summary>
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

        /// <summary>
        /// Remove the previous request after spawning the projectile
        /// </summary>
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
    /// Lobby setting: No Projectile Cap.
    ///
    /// <para><b>The cap</b> (decompiled, build 21750826). <c>WeaponAttack.FixedUpdateAttack</c>
    /// (VA 0x180450150) fires a volley of <c>WeaponUtility.GetAttackQuantity</c> projectiles. With a
    /// burst interval it spaces them out. With none it fires them all in one frame, but stops at
    /// <c>maxNumProjectilesWithoutInterval</c> (0x60) and then calls <c>StopAttackNoCoroutine</c>,
    /// so the rest are dropped, not delayed. The constructor (0x1804515D0) sets it to 80; Ghidra
    /// labels that store one slot off, as <c>attackQuantity</c>. <b>LIKELY</b> by offset.</para>
    ///
    /// <para><b>Every attack on this machine</b>, the local player's and, on the host, the ones it
    /// simulates for teammates: the host must fire the same volley the owner does. That is why this
    /// is a lobby setting and never a per-player config. Applied in <c>SetAttack</c>
    /// (0x1804505F0), which runs when an attack is set up; the loop reads the field every frame.
    /// The original is kept, so a session without the setting, or singleplayer after it, gets the
    /// game's value back. <b>UNVERIFIED, not run in-game.</b></para>
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
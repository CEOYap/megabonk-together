using Assets.Scripts.Game.Combat.ConstantAttacks;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches
{
    /// <summary>
    /// OB-60. Weapon hit sparks: the host sends every one it draws, a client draws exactly those.
    /// See <see cref="Helpers.WeaponHitSparks"/> for the decompiled call graph.
    /// </summary>
    [HarmonyPatch(typeof(EffectManager))]
    internal static class EnemyHitEffectPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Clients: prefix-false for enemy hits not replayed from the host, unless inside the local
        /// player's own Aegis or laser. Host: queue the spark for clients. Wall hits stay local.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.EnemyHitEffect),
            new[] { typeof(Vector3), typeof(Vector3), typeof(bool), typeof(EWeapon), typeof(GameObject), typeof(bool) })]
        public static bool EnemyHitEffect_Prefix(Vector3 hitPos, Vector3 moveDir, bool hitEnemy, EWeapon eWeapon, GameObject weaponHitEffect, bool useSfx)
        {
            if (!hitEnemy || !synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (!(synchronizationService.IsServerMode() ?? false))
            {
                var allow = Helpers.WeaponHitSparks.AllowLocal(out var ownerRun);
                if (ownerRun)
                {
                    Helpers.ItemVisualSync.CaptureOwnerWeaponSpark(hitPos, moveDir, eWeapon, useSfx);
                }

                return allow;
            }

            if (eWeapon != EWeapon.None)
            {
                Helpers.WeaponHitSparks.Queue(hitPos, moveDir, eWeapon, weaponHitEffect != null, useSfx);
            }

            return true;
        }
    }

    /// <summary>OB-60. Aegis runs on its owner's machine only (<c>AegisAttackPatches</c>).</summary>
    [HarmonyPatch(typeof(AegisAttack))]
    internal static class AegisHitEffectScopePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(AegisAttack.FixedUpdate))]
        public static void FixedUpdate_Prefix() => Helpers.WeaponHitSparks.EnterOwnerRun();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(AegisAttack.FixedUpdate))]
        public static void FixedUpdate_Finalizer() => Helpers.WeaponHitSparks.ExitOwnerRun();
    }

    /// <summary>OB-60. The laser hits on its owner's machine only (<c>LaserBeamAttackPatches</c>).</summary>
    [HarmonyPatch(typeof(LaserBeamAttack))]
    internal static class LaserHitEffectScopePatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(LaserBeamAttack.HitEnemy))]
        public static void HitEnemy_Prefix() => Helpers.WeaponHitSparks.EnterOwnerRun();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(LaserBeamAttack.HitEnemy))]
        public static void HitEnemy_Finalizer() => Helpers.WeaponHitSparks.ExitOwnerRun();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LaserBeamAttack.StopLaser))]
        public static void StopLaser_Prefix() => Helpers.WeaponHitSparks.EnterOwnerRun();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(LaserBeamAttack.StopLaser))]
        public static void StopLaser_Finalizer() => Helpers.WeaponHitSparks.ExitOwnerRun();
    }
}

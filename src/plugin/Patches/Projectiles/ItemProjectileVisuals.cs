using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches.Projectiles
{
    /// <summary>
    /// Shares item projectiles with the other peers and keeps their copies harmless.
    /// <para>Decompiled (build 21750826): every item projectile is created through
    /// <c>ItemProjectile.Set</c> (VA 0x1804D1CC0; callers Soul Harvester x2, Energy Core, and
    /// <c>EffectManager.SpawnGhostProjectile</c> for Ghost and Bob). It deals damage in one place,
    /// <c>CheckCollision</c> (VA 0x1804D0C90), which both the spawn check and the flight step call;
    /// <c>HitEnemy</c> is inlined there, so patching it would not catch anything. <c>ItemProjectileOrb</c>
    /// does not override <c>CheckCollision</c>.</para>
    /// <para>No ownership check on <c>Set</c>: the mod already lets each item run on exactly one machine,
    /// and that machine is the one that shares it. <b>UNVERIFIED</b> in game.</para>
    /// </summary>
    [HarmonyPatch(typeof(ItemProjectile))]
    internal static class ItemProjectileVisualPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IItemProjectileVisualService visualService = Plugin.Services.GetService<IItemProjectileVisualService>();

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ItemProjectile.Set))]
        public static void Set_Postfix(ItemProjectile __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted() || visualService.BuildingCopy)
            {
                return;
            }

            // A pooled object last used as a copy, now set up for real.
            visualService.ForgetCopy(__instance);
            visualService.OnLocalProjectileSet(__instance);
        }

        /// <summary>
        /// A copy that touches an enemy ends without damaging it (prefix-false). Other collisions run
        /// the original, which only ends the projectile.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("CheckCollision")]
        public static bool CheckCollision_Prefix(ItemProjectile __instance, Collider collider, ref bool __result)
        {
            if (!visualService.IsCopy(__instance) || collider == null)
            {
                return true;
            }

            if (collider.GetComponentInParent<Assets.Scripts.Actors.Enemies.Enemy>() == null)
            {
                return true;
            }

            __instance.ProjectileDone();
            __result = true;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch("ProjectileDone")]
        public static void ProjectileDone_Postfix(ItemProjectile __instance) => visualService.ForgetCopy(__instance);
    }
}

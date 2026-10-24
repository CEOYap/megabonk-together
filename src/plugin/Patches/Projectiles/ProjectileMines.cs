using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Projectiles
{
    /// <summary>
    /// OB-58. A received mine runs its own enemy check on a client, so it explodes where the host's
    /// does, once. Decompiled: <c>MyFixedUpdate</c> (0x1804EEF10) calls <c>Explode</c> (0x1804EE9A0)
    /// when an enemy is inside its radius; <c>Explode</c> draws from <c>explosionPool</c>, damages
    /// (blocked on clients) and calls <c>ProjectileDone</c>, which a client refuses for a received
    /// copy. Most host mines instead time out through <c>ProjectileBase.CheckTimeout</c>
    /// (0x18042BB30) without exploding, so drawing an explosion on every removal was wrong (hands-off
    /// run 2026-10-24: host 1 explosion, client 118).
    /// </summary>
    [HarmonyPatch(typeof(ProjectileMines))]
    internal static class ProjectileMinesPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// A received mine that exploded here is hidden: its refused <c>ProjectileDone</c> would have
        /// done that, and an inactive mine runs no further checks, so it cannot explode twice. The
        /// interpolator still removes it on the host's <c>ProjectileDone</c>.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch("Explode")]
        private static void Explode_Postfix(ProjectileMines __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (synchronizationService.IsServerMode() ?? false)
            {
                Helpers.ExplosionDiagnostics.HostMineExploded();
                return;
            }

            if (ProjectileBasePatches.IsReceived(__instance))
            {
                Helpers.ExplosionDiagnostics.DrewMine();
                __instance.gameObject.SetActive(false);
            }
        }
    }
}

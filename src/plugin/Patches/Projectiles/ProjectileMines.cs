using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileMines))]
    internal static class ProjectileMinesPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Prefix-false on clients for received mines. Decompiled, <c>MyFixedUpdate</c> (0x1804EEF10)
        /// checks for enemies and calls <c>Explode</c> (0x1804EE9A0), which draws an explosion, damages
        /// and then calls <c>ProjectileDone</c>. That is refused for a received copy, so the mine stayed
        /// and could explode again at every check until the host's <c>ProjectileDone</c> arrived. The
        /// host's explosion is drawn by <c>ProjectileInterpolator</c> on removal instead.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("MyFixedUpdate")]
        private static bool MyFixedUpdate_Prefix(ProjectileMines __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted() || (synchronizationService.IsServerMode() ?? false))
            {
                return true;
            }

            return !ProjectileBasePatches.IsReceived(__instance);
        }
    }
}

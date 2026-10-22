using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.BossOrbPatch
{
    [HarmonyPatch(typeof(BossOrbBleed))]
    internal static class BossOrbBleedPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Skipped on clients to avoid trail artifacts.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(BossOrbBleed.FloatMovement))]
        public static bool FloatMovement_Prefix(BossOrbBleed __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }
            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
            }
            return true;
        }
    }
}

using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(SkinSelection))]
    internal static class SkinSelectionPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Synchronize skin selection
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(SkinSelection.OnSkinHover))]
        public static void OnSkinSelected_Postfix(SkinSelection __instance, SkinContainer skinContainer)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            synchronizationService.OnSkinSelected(skinContainer.skin);
        }
    }
}

using Assets.Scripts.Utility;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(PauseUi))]
    internal static class PauseUiPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>The pause screen does not pause the game in netplay.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(PauseUi.OnEnable))]
        public static void OnEnable_Postfix(PauseUi __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            MyTime.Unpause();
        }

    }
}

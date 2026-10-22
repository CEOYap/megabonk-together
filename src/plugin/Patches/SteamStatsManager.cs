using Assets.Scripts.Steam;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(SteamStatsManager))]
    internal static class SteamStatsManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>No Steam stats upload during netplay.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(SteamStatsManager.QueueUpload))]
        public static bool QueueUpload_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            Plugin.Log.LogInfo("Blocked attempt to upload stats to Steam.");
            return false;
        }

        /// <summary>No Steam stats upload during netplay.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(SteamStatsManager.TryUploadStats))]
        public static bool TryUploadStats_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            Plugin.Log.LogInfo("Blocked attempt to upload stats to Steam.");
            return false;
        }
    }
}

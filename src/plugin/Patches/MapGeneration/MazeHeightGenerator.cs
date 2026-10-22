using Assets.Scripts.MapGeneration.ProceduralTiles;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.MapGeneration
{
    [HarmonyPatch(typeof(MazeHeightGenerator))]
    internal static class MazeHeightGeneratorPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetRequiredService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetRequiredService<IPlayerManagerService>();

        /// <summary>Same seed for every peer.</summary>

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MazeHeightGenerator.GenerateHeight))]
        public static void GenerateHeights_Prefix(ref int seed)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }
            seed = playerManagerService.GetSeed();
        }

        /// <summary>Same seed for every peer.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MazeHeightGenerator.GenerateHeightHein))]
        public static void GenerateHeightHein_Prefix(ref int seed)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }
            seed = playerManagerService.GetSeed();
        }

        /// <summary>Same seed for every peer.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MazeHeightGenerator.GenerateHeightMe))]
        public static void GenerateHeightMe_Prefix(ref int seed)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }
            seed = playerManagerService.GetSeed();
        }
    }
}

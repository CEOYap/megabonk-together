using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;
using static MapGenerationFinalBoss;

namespace OmniBonk.Patches.MapGeneration
{
    [HarmonyPatch(typeof(_GenerateMap_d__15))]
    internal static class MapGenerationFinalBossPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Store world size for later use
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(_GenerateMap_d__15.MoveNext))]
        public static void MoveNext_Postfix(_GenerateMap_d__15 __instance, ref bool __result)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            if (__instance.__1__state == -1)
            {
                Plugin.Instance.SetWorldSize(new Vector3(600, 600, 600)); // fixed world size for final boss (i think it should be enough ?)

                // OB-36: otherwise TeleportPlayerBackToBounds restores the previous stage's size and
                // that peer's positions are scaled wrong from then on.
                Plugin.Instance.OriginalWorldSize = new Vector3(600, 600, 600);
            }
        }
    }
}


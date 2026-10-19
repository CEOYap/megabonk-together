using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;
using static MapGenerationFinalBoss;

namespace MegabonkTogether.Patches.MapGeneration
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

                // OB-36. Without this the restore in TeleportPlayerBackToBounds (called by the game's
                // out-of-bounds trigger, WorldColliderTrigger.OnCollisionStay) put a player who fell
                // off the arena back on the previous stage's size, while every other peer stayed on
                // 600. Every position that peer sent or received from then on was scaled by the
                // ratio: 2026-10-19, both bots saw the final boss at ~1.9x the host's coordinates
                // ((-137, 81, 27) against (-72, 42, 14)) with HP still matching.
                Plugin.Instance.OriginalWorldSize = new Vector3(600, 600, 600);
            }
        }
    }
}


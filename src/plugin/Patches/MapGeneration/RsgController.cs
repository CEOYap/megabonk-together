using Assets.Scripts.Managers;
using HarmonyLib;
using OmniBonk.Helpers;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches.MapGeneration
{
    [HarmonyPatch(typeof(RsgController._GenerateMap_d__41))]
    internal static class RsgControllerGenerateMapPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Host registers crypt pots and chests for sync.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(RsgController._GenerateMap_d__41.MoveNext))]
        public static void MoveNext_Postfix(RsgController._GenerateMap_d__41 __instance, bool __result)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            if (__result)
            {
                return;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;

            if (!isHost)
            {
                return;
            }

            if (MapController.runConfig.mapData.eMap == Assets.Scripts._Data.MapsAndStages.EMap.Graveyard)
            {
                Plugin.Instance.SetWorldSize(new UnityEngine.Vector3(5000f, 5000f, 5000f)); //Is the crypt outside of the map ? Using a large size fix Quantization issues
            }

            foreach (var piece in __instance.__4__this.allPieces)
            {
                var children = Il2CppFindHelper.RuntimeGetComponentsInChildren<Component>(piece.children);
                foreach (var child in children)
                {
                    if (child.name.StartsWith("Pot") || child.name.StartsWith("ChestFreeCrypt"))
                    {
                        synchronizationService.OnSpawnedObjectInCrypt(child.gameObject);
                    }
                }
            }

            synchronizationService.OnSpawnedObjectInCrypt(__instance.__4__this.rsgEnd.gameObject);
        }
    }
}

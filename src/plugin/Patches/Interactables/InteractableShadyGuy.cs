using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches.Interactables
{
    [HarmonyPatch(typeof(InteractableShadyGuy))]
    internal static class InteractableShadyGuyPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly ISpawnedObjectManagerService spawnedObjectManagerService = Plugin.Services.GetService<ISpawnedObjectManagerService>();

        /// <summary>Client uses the host's rarity.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(InteractableShadyGuy.Start))]
        public static void Start_Prefix(InteractableShadyGuy __instance)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;

            if (isServer) return;

            var rarity = DynamicData.For(__instance).Get<EItemRarity?>("rarity");
            if (rarity.HasValue)
            {
                spawnedObjectManagerService.AddShadyGuyRarityRequest(rarity.Value);
            }

            return;
        }


        /// <summary>Host sends it from Start, once rarity is set.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(InteractableShadyGuy.Start))]
        public static void Start_Postfix(InteractableShadyGuy __instance)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                synchronizationService.OnSpawnedObject(__instance.gameObject);
            }
        }
    }
}

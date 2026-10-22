using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Inventory__Items__Pickups;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Inventories
{
    [HarmonyPatch(typeof(TomeInventory))]
    internal static class TomeInventoryPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Flags a teammate adding a Chaos or Gambler tome. Decompiled <c>TomeUtility$$CheckSpecialTomes</c>
        /// (VA 0x1804304C0) applies their random stat roll to the LOCAL player whichever inventory ran it.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(TomeInventory.AddTome))]
        public static void AddTome_Prefix(TomeInventory __instance, TomeData tomeData)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            ETome tome = tomeData.eTome;
            if ((tome == ETome.Chaos || tome == ETome.Gambler) && playerManagerService.IsRemoteTomeInventory(__instance))
            {
                Plugin.Instance.IS_NETPLAYER_ADDING_TOME = true;
            }
        }
    }
}

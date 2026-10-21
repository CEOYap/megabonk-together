using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Inventory__Items__Pickups;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Inventories
{
    [HarmonyPatch(typeof(TomeInventory))]
    internal static class TomeInventoryPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Track netplayer adding a chaos or gambler tome so we can prevent triggering its effect for everyone.
        ///
        /// <para>Decompiled <c>TomeUtility$$CheckSpecialTomes</c> (VA 0x1804304C0) handles exactly
        /// these two: for Chaos (24) and Gambler (25) it rolls a random stat offer and applies it
        /// through <c>MyPlayer.Instance.inventory.statInventory.ChangeStat</c>, the LOCAL player,
        /// whichever inventory AddTome ran on. Gambler was missing here, so every teammate's
        /// Gambler level-up gave this peer an extra random roll, which can be negative.</para>
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

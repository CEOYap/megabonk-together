using Assets.Scripts.Inventory__Items__Pickups;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Items
{
    [HarmonyPatch(typeof(TomeUtility))]
    internal static class TomeUtilityPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>No Chaos/Gambler tome effect when a netplayer adds the tome.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(TomeUtility.CheckSpecialTomes))]
        public static bool CheckSpecialTomes_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (Plugin.Instance.IS_NETPLAYER_ADDING_TOME)
            {
                Plugin.Instance.IS_NETPLAYER_ADDING_TOME = false;
                return false;
            }

            return true;
        }
    }
}

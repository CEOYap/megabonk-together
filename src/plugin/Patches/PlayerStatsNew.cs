using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    /// <summary>Returns a net player's own stats when requested, not the local player's.</summary>
    [HarmonyPatch(typeof(PlayerStatsNew))]
    internal static class PlayerStatsNewPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static bool shouldIgnoreNextPatch = false;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerStatsNew.GetStat))]
        public static bool GetStat_Prefix(EStat stat, ref float __result)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (shouldIgnoreNextPatch)
            {
                shouldIgnoreNextPatch = false;
                return true;
            }

            var peaked = playerManagerService.PeakNetplayerPositionRequest();
            if (peaked.HasValue && playerManagerService.IsRemoteConnectionId(peaked.Value))
            {
                var netPlayer = playerManagerService.GetNetPlayerByNetplayId(peaked.Value);
                if (netPlayer == null)
                {
                    return true;
                }
                shouldIgnoreNextPatch = true;
                __result = netPlayer.Inventory.playerStats.GetStat(stat);
                return false;
            }

            return true;
        }
    }
}

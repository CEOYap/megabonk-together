using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Items
{
    [HarmonyPatch(typeof(ItemGhost))]
    internal static class ItemGhostPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Skips the original on a failed interaction (it otherwise causes an access violation); this
        /// crash guard is deliberately ungated and applies in singleplayer too.
        /// <para>OB-2: also suppresses remote players' ghosts. Every <c>ItemGhost</c> subscribes to one static
        /// action, and <c>SpawnGhostProjectile</c> spawns on the local player.</para>
        /// <para><b>UNVERIFIED.</b> Not run in-game; whether those ghosts also dealt damage is unknown.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemGhost.OnInteracted))]
        public static bool OnInteracted_Prefix(bool success, ItemGhost __instance)
        {
            if (!success)
            {
                return false;
            }

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (playerManagerService.IsRemoteItem(__instance))
            {
                return false;
            }

            return true;
        }
    }
}

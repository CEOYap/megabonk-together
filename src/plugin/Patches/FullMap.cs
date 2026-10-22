using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(FullMap))]
    internal static class FullMapPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetRequiredService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetRequiredService<IPlayerManagerService>();

        /// <summary>
        /// Reveal fog around all NetPlayers too
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(FullMap.FixedUpdate))]
        public static void FixedUpdate_Postfix(FullMap __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (!GameManager.Instance.player.playerInput.CanInput()) return; //Just to wait a bit if not we can reveal not final players positions

            var netPlayers = playerManagerService.GetAllSpawnedNetPlayers();

            foreach (var netPlayer in netPlayers)
            {
                if (netPlayer?.Model != null)
                {
                    // Push and pop in one method, so try/finally (P1-11).
                    playerManagerService.AddGetNetplayerPositionRequest(netPlayer.ConnectionId);
                    try
                    {
                        __instance.QueueRevealFog(netPlayer.Model.transform.position);
                        __instance.RevealFog();
                    }
                    finally
                    {
                        playerManagerService.UnqueueNetplayerPositionRequest();
                    }
                }
            }
        }
    }

}

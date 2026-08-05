using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
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
                    // Push and pop live in the SAME method here, so try/finally applies and the
                    // finalizer pattern does not. Misfiled as a prefix/postfix pair by the first
                    // sweep because the push sits inside a method named *_Postfix. A throw in
                    // either game call below would strand the request (P1-11) and the loop would
                    // then push again for the next player.
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

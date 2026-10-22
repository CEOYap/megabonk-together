using Assets.Scripts.Game.Spawning;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(SpawnPositions))]
    internal class SpawnPositionsPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Spawns enemies around a random player.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(SpawnPositions.GetEnemySpawnPosition))]
        public static void GetEnemySpawnPosition_Prefix()
        {
            try
            {

                if (!synchronizationService.HasNetplaySessionStarted() || synchronizationService.IsLoadingNextLevel())
                {
                    return;
                }

                var isServer = synchronizationService.IsServerMode();
                if (isServer.HasValue && isServer.Value)
                {
                    var randomPlayerId = playerManagerService.GetRandomPlayerAliveConnectionId();

                    if (randomPlayerId.HasValue)
                    {
                        playerManagerService.AddGetNetplayerPosition(randomPlayerId.Value);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"Error in SpawnPositionsPatch.GetEnemySpawnPosition_Prefix: {ex}");
            }
        }
    }
}

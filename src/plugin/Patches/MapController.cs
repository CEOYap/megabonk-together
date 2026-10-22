using Assets.Scripts.Game.Other;
using Assets.Scripts.Managers;
using HarmonyLib;
using OmniBonk.Common.Models;
using OmniBonk.Extensions;
using OmniBonk.Helpers;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(MapController))]
    internal static class MapControllerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        // Must be INetTransport: the inactive transport answers "all ready" from an empty peer set.
        private static readonly INetTransport udpClientService = Plugin.Services.GetService<INetTransport>();
        private static readonly IWebsocketClientService websocketClientService = Plugin.Services.GetService<IWebsocketClientService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        private static bool isWaitingForServerResponse = false;

        /// <summary>Host waits for every character selection; in Friendlies, locks the lobby first.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapController.StartNewMap))]
        public static bool StartNewMap_Prefix(RunConfig newRunConfig)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            if (TransitionUI.Instance.isTransitioning)
            {
                return false;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;

            if (!isHost)
            {
                UnityEngine.Random.InitState(playerManagerService.GetSeed());
                return true;
            }

            Plugin.Instance.IS_HOST_READY = true;

            if (!udpClientService.AreAllPeersReady())
            {
                Plugin.Instance.ShowModal("Waiting for all players to be ready...");
                return false;
            }

            var isFriendlyMode = Plugin.Instance.Mode.Mode == NetworkModeType.Friendlies;

            if (isFriendlyMode && !isWaitingForServerResponse)
            {
                isWaitingForServerResponse = true;
                CoroutineRunner.Instance.Run(NotifyServerAndStartGame(newRunConfig));
                return false;
            }

            Plugin.Instance.IS_HOST_READY = false;
            Plugin.Instance.HideModal();

            return true;
        }

        private static IEnumerator NotifyServerAndStartGame(RunConfig runConfig)
        {
            // No matchmaker on the Steam path. Known gap: SetLobbyJoinable(false) is not exposed yet.
            if (Configuration.ModConfig.UseSteamTransport.Value)
            {
                Plugin.Instance.IS_HOST_READY = false;
                MapController.StartNewMap(runConfig);
                isWaitingForServerResponse = false;
                yield break;
            }

            Plugin.Instance.ShowModal("Locking lobby...");

            var task = websocketClientService.SendGameStarting();

            while (!task.IsCompleted)
            {
                yield return new WaitForSeconds(0.17f);
            }

            if (task.Result)
            {
                Plugin.Instance.IS_HOST_READY = false;
                Plugin.Instance.HideModal();

                MapController.StartNewMap(runConfig);
                isWaitingForServerResponse = false;
            }
            else
            {
                Plugin.Log.LogError("Failed to get server response for game starting");
                Plugin.Instance.HideModal();
                Plugin.Instance.ShowModal("Failed to lock lobby. Please try again.");
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(MapController.StartNewMap))]
        public static void StartNewMap(RunConfig newRunConfig)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;

            if (!isHost)
            {
                return;
            }

            if (!udpClientService.AreAllPeersReady())
            {
                return;
            }

            if (synchronizationService.IsLoading())
            {
                return;
            }

            UnityEngine.Random.InitState(playerManagerService.GetSeed());
            synchronizationService.OnRunStarted(newRunConfig);

            var allPlayers = playerManagerService.GetAllPlayers().ToList();
            var playerCount = allPlayers.Count;
            var mapName = newRunConfig.mapData.eMap.GetMapName();
            var stageName = newRunConfig.stageData.name;
            var stageIndex = newRunConfig.mapData.stages.IndexOf(newRunConfig.stageData);
            var characters = allPlayers.Select(p => ((ECharacter)p.Character).ToString()).ToList();

            // Matchmaker telemetry; skipped on Steam to avoid an unobserved Task exception.
            if (!Configuration.ModConfig.UseSteamTransport.Value)
            {
                _ = websocketClientService.SendRunStatistics(playerCount, mapName, stageIndex + 1, characters);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapController.RestartRun))]
        public static bool RestartRun_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            return false;
        }

        //[HarmonyPrefix]
        //[HarmonyPatch(nameof(MapController.LoadNextStage))]
        //public static bool LoadNextStage_Prefix()
        //{
        //    if (MapController.index == 0)
        //    {
        //        MapController.LoadFinalStage();
        //        return false;
        //    }

        //    return true;
        //}

        //[HarmonyPostfix]
        //[HarmonyPatch(nameof(MapController.LoadNextStage))]
        //public static void LoadNextStage_Postfix()
        //{
        //    MapController.LoadFinalStage();
        //}
    }
}

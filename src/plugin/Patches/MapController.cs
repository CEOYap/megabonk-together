using Assets.Scripts.Game.Other;
using Assets.Scripts.Managers;
using HarmonyLib;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Extensions;
using MegabonkTogether.Helpers;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(MapController))]
    internal static class MapControllerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IUdpClientService udpClientService = Plugin.Services.GetService<IUdpClientService>();
        private static readonly IWebsocketClientService websocketClientService = Plugin.Services.GetService<IWebsocketClientService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        private static bool isWaitingForServerResponse = false;

        /// <summary>
        /// Prevent Host from starting a new map until all players are ready (selected character)
        /// In friendlies, also notify server that game is starting to prevent new players from joining
        /// </summary>
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
            // There is no server to notify on the Steam path, and asking anyway is what produced
            // "Failed to lock lobby" at the end of an otherwise working session: SendGameStarting
            // is a round trip to the matchmaker, and a Steam session never connected to one.
            //
            // Locking exists so nobody joins between the host pressing Start and the run loading.
            // The Steam equivalent is SetLobbyJoinable(false), which ISteamLobbyService does not
            // expose yet - so this is a real if narrow gap rather than a clean no-op, and it is
            // recorded as such in docs/steamworks/00-migration-plan.md rather than left silent.
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

        /// <summary>
        /// Synchronize run start to clients if all players are ready
        /// </summary>
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

            // Skipped rather than left to fail. This is fire-and-forget telemetry to the
            // matchmaker, so on the Steam path it would not break the run - it would throw inside a
            // discarded Task and surface later as an unobserved exception, which is a worse way to
            // learn about it than not sending.
            if (!Configuration.ModConfig.UseSteamTransport.Value)
            {
                _ = websocketClientService.SendRunStatistics(playerCount, mapName, stageIndex + 1, characters);
            }
        }

        /// <summary>
        /// Prevent restarting run in netplay session
        /// </summary>
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

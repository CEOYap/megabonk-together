using Assets.Scripts._Data.MapsAndStages;
using Assets.Scripts.Managers;
using Il2CppInterop.Runtime;
using MegabonkTogether.Configuration;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace MegabonkTogether.Scripts
{
    public class NetworkHandler : MonoBehaviour
    {
        private const float LOBBY_UPDATE_TICK_RATE = 60f;
        private const float lobbyUpdatetickInterval = 1f / LOBBY_UPDATE_TICK_RATE;
        private float lobbyUpdateAccumulator = 0f;

        private const float ENEMY_UPDATE_TICK_RATE = 40f;
        private const float enemyUpdatetickInterval = 1f / ENEMY_UPDATE_TICK_RATE;
        private float enemyUpdateAccumulator = 0f;

        private const float PROJECTILE_UPDATE_TICK_RATE = 20f;
        private const float projectileUpdatetickInterval = 1f / PROJECTILE_UPDATE_TICK_RATE;
        private float projectileUpdateAccumulator = 0f;

        private const float TUMBLEWEED_UPDATE_TICK_RATE = 20f;
        private const float tumbleWeedUpdatetickInterval = 1f / TUMBLEWEED_UPDATE_TICK_RATE;
        private float tumbleWeedUpdateAccumulator = 0f;

        private bool hasStarted = false;
        private bool? hasFoundMatch = null;
        private bool? hasJoinedFriendlyRoom = null;
        private bool? isConnectedToMatchMaker = null;
        private bool IsNetworkInterrupted = false;
        private string matchMakerFailureMessage = string.Empty;

        private bool isHost = false;
        private bool isGameStarted = false;

        private IUdpClientService udpClientService;
        private ISynchronizationService synchronizationService;
        private IWebsocketClientService websocketClientService;
        private IPlayerManagerService playerManagerService;

        public bool? IsConnectedToMatchMaker => isConnectedToMatchMaker;
        public string MatchMakerFailureMessage => matchMakerFailureMessage;
        public bool? HasFoundMatch => hasFoundMatch;

        public bool? HasJoinedFriendlyRoom => hasJoinedFriendlyRoom;
        public bool IsNetworkInterruptedStatus => IsNetworkInterrupted;

        public bool IsHost => isHost;

        public void Awake()
        {
            websocketClientService = Plugin.Services.GetRequiredService<IWebsocketClientService>();
            playerManagerService = Plugin.Services.GetRequiredService<IPlayerManagerService>();

            EventManager.SubscribeGameStartedEvents(OnGameStarted);
            EventManager.SubscribePortalOpenedEvents(OnPortalOpened);
        }

        private void OnPortalOpened()
        {
            isGameStarted = false;
        }

        private void OnGameStarted()
        {
            isGameStarted = true;
        }

        public void Update()
        {
            try
            {
                if (udpClientService == null || synchronizationService == null) return;

                if (hasFoundMatch == null) return;

                if (!hasFoundMatch.HasValue && !hasFoundMatch.Value || synchronizationService.IsLoadingNextLevel()) return;

                udpClientService.Poll();

                if (GameManager.Instance == null || GameManager.Instance.player == null || GameManager.Instance.player.inventory == null) return;

                Services.AllocationDiagnostics.Sample(ModConfig.LogAllocationRate.Value);
                Services.BandwidthDiagnostics.Sample(ModConfig.LogBandwidth.Value, udpClientService, playerManagerService);

                lobbyUpdateAccumulator += Time.deltaTime;

                if (isHost && isGameStarted)
                {
                    enemyUpdateAccumulator += Time.deltaTime;
                    projectileUpdateAccumulator += Time.deltaTime;

                    if (MapController.runConfig.mapData.eMap == EMap.Desert)
                    {
                        tumbleWeedUpdateAccumulator += Time.deltaTime;
                    }
                }

                // UiManager.Instance.GetComponentInChildren<TargetOfInterestUi>().RefreshPrefabs();

                while (lobbyUpdateAccumulator >= lobbyUpdatetickInterval || enemyUpdateAccumulator >= enemyUpdatetickInterval || projectileUpdateAccumulator >= projectileUpdatetickInterval || tumbleWeedUpdateAccumulator >= tumbleWeedUpdatetickInterval)
                {
                    if (lobbyUpdateAccumulator >= lobbyUpdatetickInterval)
                    {
                        lobbyUpdateAccumulator -= lobbyUpdatetickInterval;
                        udpClientService.Update();
                    }

                    if (isHost && enemyUpdateAccumulator >= enemyUpdatetickInterval)
                    {
                        enemyUpdateAccumulator -= enemyUpdatetickInterval;
                        udpClientService.UpdateEnemies();
                    }

                    if (isHost && projectileUpdateAccumulator >= projectileUpdatetickInterval)
                    {
                        projectileUpdateAccumulator -= projectileUpdatetickInterval;
                        udpClientService.UpdateProjectiles();
                    }

                    if (isHost && tumbleWeedUpdateAccumulator >= tumbleWeedUpdatetickInterval)
                    {
                        tumbleWeedUpdateAccumulator -= tumbleWeedUpdatetickInterval;
                        udpClientService.UpdateTumbleWeeds();
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"NetworkHandler Update error: {ex}");
            }
        }

        public int GetLobbySize()
        {
            return playerManagerService.GetAllPlayers().Count();
        }

        public void HandleNetworking()
        {
            try
            {
                Task webSocket = new Task(async () =>
                {
                    try
                    {
                        IL2CPP.il2cpp_thread_attach(IL2CPP.il2cpp_domain_get());

                        hasFoundMatch = null;
                        IsNetworkInterrupted = false;
                        matchMakerFailureMessage = string.Empty;
                        if (isConnectedToMatchMaker.HasValue && !isConnectedToMatchMaker.Value)
                        {
                            isConnectedToMatchMaker = null;
                        }

                        await websocketClientService.ConnectAndMatchAsync(ModConfig.ServerUrl.Value, ModConfig.RDVServerPort.Value, this);
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogError($"WebSocket connection error: {ex.Message}");
                        isConnectedToMatchMaker = false;
                        matchMakerFailureMessage = ex.Message;
                    }
                });

                webSocket.Start();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogError($"WebSocket task error: {ex}");
            }
        }

        public void ResetNetworking()
        {
            isConnectedToMatchMaker = null;
            Plugin.Instance.Mode = new();
            isHost = false;

            // FIX P0-5: clear the match flag on teardown. HasNetplaySessionInitialized() reads
            // this, and ~40 patch sites gate on it — SaveManager most importantly. It was
            // previously only reset in HandleNetworking(), i.e. when STARTING a session, so after
            // any netplay game it stayed true and singleplayer silently kept taking the netplay
            // path — progression not saved — until the game was restarted.
            // (Leaderboards no longer reads this; that block is unconditional. See P0-0.)
            hasFoundMatch = null;

            // Clear transform-fallback counters so each session's diagnostics start from zero,
            // rather than a new session inheriting counts pending from the previous one.
            Patches.Unity.TransformFallbackDiagnostics.Reset();
            Services.TrackerAttributionDiagnostics.Reset();
            Services.UnityExceptionDiagnostics.Reset();

            // PERF 1A: drop switcher registrations from the finished session. Their enemies are
            // gone, so OnDisable may never fire for them.
            Enemies.TargetSwitcherManager.Clear();
            Snapshot.EnemyInterpolatorManager.Clear();

            // Encounter-UI statics hold references from the finished session — a MyButton, a
            // Coroutine handle, a TMP component parented to UI the session destroys. Handing any
            // of those back to the game in the next session is a NullReferenceException on a
            // destroyed Component, which is upstream issue #93's signature.
            Patches.ChestWindowUiPatches.Reset();
            Patches.LevelUpScreenPatches.Reset();
            Patches.Projectiles.ProjectileBasePatches.ClearOpacityCache();
            Helpers.EncounterInputGrace.Reset();
            Services.AllocationDiagnostics.Reset();
            Services.BandwidthDiagnostics.Reset();

            try
            {
                udpClientService?.Reset();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Error resetting UDP client: {ex}");
            }
            finally
            {
                udpClientService = null;
            }

            try
            {
                synchronizationService?.Reset();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Error resetting Synchronization service: {ex}");
            }
            finally
            {
                synchronizationService = null;
            }

            playerManagerService?.Reset();

            if (websocketClientService != null)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await websocketClientService.Reset();
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogError($"Error resetting websocket: {ex}");
                    }
                }).Wait();
            }
        }

        public void OnConnectedToMatchMaker()
        {
            isConnectedToMatchMaker = true;
        }

        public void OnFailedToConnectToMatchMaker(string message)
        {
            isConnectedToMatchMaker = false;
            matchMakerFailureMessage = message;
        }

        public void OnNetworkInterrupted(string message)
        {
            IsNetworkInterrupted = true;
            matchMakerFailureMessage = message;
        }

        public void OnMatchFound(bool success)
        {
            hasFoundMatch = success;
            if (success)
            {
                udpClientService = Plugin.Services.GetRequiredService<IUdpClientService>();
                synchronizationService = Plugin.Services.GetRequiredService<ISynchronizationService>();
                if (Plugin.Instance.Mode.Mode == Common.Models.NetworkModeType.Random)
                {
                    isHost = synchronizationService.IsServerMode() ?? false;
                }
                else
                {
                    isHost = Plugin.Instance.Mode.Role == Common.Models.Role.Host;
                    udpClientService.UpdateMode(isHost);
                }
            }
            else
            {
                if (Plugin.Instance.Mode.Mode == Common.Models.NetworkModeType.Random)
                {
                    matchMakerFailureMessage = "Failed to establish P2P connection.";
                }
                IsNetworkInterrupted = true;
            }
        }

    }
}

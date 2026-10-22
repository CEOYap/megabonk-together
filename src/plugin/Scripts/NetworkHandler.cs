using Assets.Scripts._Data.MapsAndStages;
using Assets.Scripts.Managers;
using Il2CppInterop.Runtime;
using OmniBonk.Configuration;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace OmniBonk.Scripts
{
    public class NetworkHandler : MonoBehaviour
    {
        // Rates live in Configuration/Tunables.cs; the intervals stay derived here.
        private const float lobbyUpdatetickInterval = 1f / Tunables.LOBBY_UPDATE_TICK_RATE;
        private float lobbyUpdateAccumulator = 0f;

        private const float enemyUpdatetickInterval = 1f / Tunables.ENEMY_UPDATE_TICK_RATE;
        private float enemyUpdateAccumulator = 0f;

        private const float projectileUpdatetickInterval = 1f / Tunables.PROJECTILE_UPDATE_TICK_RATE;
        private float projectileUpdateAccumulator = 0f;

        private const float tumbleWeedUpdatetickInterval = 1f / Tunables.TUMBLEWEED_UPDATE_TICK_RATE;
        private float tumbleWeedUpdateAccumulator = 0f;

        /// <summary>OB-13. Latched so a failing diagnostic warns once rather than every frame.</summary>
        private bool encounterPauseDiagnosticsFailed = false;

        private bool hasStarted = false;
        private bool? hasFoundMatch = null;
        private bool? hasJoinedFriendlyRoom = null;
        private bool? isConnectedToMatchMaker = null;
        private bool IsNetworkInterrupted = false;
        private string matchMakerFailureMessage = string.Empty;

        private bool isHost = false;
        private bool isGameStarted = false;

        private IUdpClientService udpClientService;
        private IStateBroadcastService stateBroadcastService;
        private ISteamNetTransport steamNetTransport;
        private INetTransport netTransport;
        private ISynchronizationService synchronizationService;
        private IWebsocketClientService websocketClientService;
        private IPlayerManagerService playerManagerService;
        private IPlayerStatsSyncService playerStatsSyncService;
        private IEnemyManagerService enemyManagerService;
        private IDesyncDetectorService desyncDetectorService;

        /// <summary>OB-1. Polled at 2 Hz rather than hooked: stat changes come through too many paths.</summary>
        private const float statsCheckInterval = 0.5f;
        private float statsCheckAccumulator = 0f;

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

            // OB-1. Subscribed here: DI is lazy, so an unresolved service would never receive anything.
            playerStatsSyncService = Plugin.Services.GetRequiredService<IPlayerStatsSyncService>();
            EventManager.SubscribePlayerStatsChangedEvents(playerStatsSyncService.ApplyRemote);

            desyncDetectorService = Plugin.Services.GetRequiredService<IDesyncDetectorService>();
            EventManager.SubscribeStateDigestEvents(desyncDetectorService.OnDigestReceived);

            EventManager.SubscribeGameStartedEvents(OnGameStarted);
            EventManager.SubscribePortalOpenedEvents(OnPortalOpened);
        }

        private void OnPortalOpened()
        {
            isGameStarted = false;
        }

        /// <summary>Steam-transport counterpart of <see cref="OnMatchFound"/>; the role comes from lobby ownership.</summary>
        public void BeginSteamSession(bool isHost)
        {
            hasFoundMatch = true;
            this.isHost = isHost;

            udpClientService = Plugin.Services.GetRequiredService<IUdpClientService>();
            stateBroadcastService = Plugin.Services.GetRequiredService<IStateBroadcastService>();
            synchronizationService = Plugin.Services.GetRequiredService<ISynchronizationService>();

            // Some callers still read IsHost from the LiteNetLib service.
            udpClientService.UpdateMode(isHost);

            Plugin.Log.LogInfo($"[steam-session] Netplay loops started as {(isHost ? "HOST" : "CLIENT")}.");
        }

        private void OnGameStarted()
        {
            isGameStarted = true;
        }

        public void Update()
        {
            try
            {
                if (udpClientService == null || stateBroadcastService == null || synchronizationService == null) return;

                if (hasFoundMatch == null) return;

                if (!hasFoundMatch.HasValue && !hasFoundMatch.Value || synchronizationService.IsLoadingNextLevel()) return;

                udpClientService.Poll();

                // Pumped here as well as in SteamTicker; in-run receive stalled without it. A second poll is harmless.
                if (ModConfig.UseSteamTransport.Value)
                {
                    steamNetTransport ??= Plugin.Services.GetService<ISteamNetTransport>();
                    steamNetTransport?.Poll();
                }

                if (GameManager.Instance == null || GameManager.Instance.player == null || GameManager.Instance.player.inventory == null) return;

                Services.AllocationDiagnostics.Sample(ModConfig.LogAllocationRate.Value);

                // OB-8. Bosses only, every 10 s.
                enemyManagerService ??= Plugin.Services.GetService<IEnemyManagerService>();
                if (enemyManagerService != null)
                {
                    Services.BossDiagnostics.Sample(enemyManagerService.GetAllRegisteredEnemies(), IsHost);
                }

                if (Services.FrameTimeDiagnostics.Sample(ModConfig.LogFrameTiming.Value))
                {
                    enemyManagerService ??= Plugin.Services.GetService<IEnemyManagerService>();
                    Services.FrameTimeDiagnostics.Report(
                        enemyManagerService?.TrackedEnemyCount ?? 0,
                        playerManagerService.GetAllPlayers().Count(),
                        IsHost);
                }

                // OB-1.
                statsCheckAccumulator += Time.deltaTime;
                if (statsCheckAccumulator >= statsCheckInterval)
                {
                    statsCheckAccumulator = 0f;
                    playerStatsSyncService?.SendIfChanged();
                }

                desyncDetectorService?.Tick(Time.deltaTime);

                // OB-13. Every frame: the pause can flip within one frame. The try must stay here, one
                // level up: an unresolved Physics.simulationMode throws MissingMethodException at JIT time.
                if (!encounterPauseDiagnosticsFailed)
                {
                    try
                    {
                        Services.EncounterPauseDiagnostics.Sample(ModConfig.LogEncounterPause.Value);
                    }
                    catch (System.Exception ex)
                    {
                        encounterPauseDiagnosticsFailed = true;
                        Plugin.Log.LogWarning(
                            $"[pause] OB-13 diagnostic disabled for this session: {ex.Message}");
                    }
                }
                // The active transport, cached.
                netTransport ??= Plugin.Services.GetRequiredService<INetTransport>();
                Services.BandwidthDiagnostics.Sample(
                    ModConfig.LogBandwidth.Value, netTransport, playerManagerService);

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
                        stateBroadcastService.Update();
                    }

                    if (isHost && enemyUpdateAccumulator >= enemyUpdatetickInterval)
                    {
                        enemyUpdateAccumulator -= enemyUpdatetickInterval;

                        // Hits before the enemy delta, so clients replay damage before the HP that includes it.
                        synchronizationService.FlushEnemyDamage();
                        stateBroadcastService.UpdateEnemies();
                    }

                    if (isHost && projectileUpdateAccumulator >= projectileUpdatetickInterval)
                    {
                        projectileUpdateAccumulator -= projectileUpdatetickInterval;
                        stateBroadcastService.UpdateProjectiles();
                    }

                    if (isHost && tumbleWeedUpdateAccumulator >= tumbleWeedUpdatetickInterval)
                    {
                        tumbleWeedUpdateAccumulator -= tumbleWeedUpdatetickInterval;
                        stateBroadcastService.UpdateTumbleWeeds();
                    }
                }

                // One flush for the whole frame's sends; unreliable is Nagled on Steam so they share packets.
                steamNetTransport?.FlushPendingSends();
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

            // Every teardown funnels here; leave the Steam lobby or the next invite is refused.
            // Guarded: on the matchmaker path the lobby belongs to the presence bridge.
            if (ModConfig.UseSteamTransport.Value)
            {
                try
                {
                    Plugin.Services.GetRequiredService<ISteamLobbyService>().LeaveLobby();
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[steam-session] Leaving the Steam lobby threw: {ex.Message}");
                }
            }

            // FIX P0-5: ~40 patches (SaveManager included) gate on this; left set, singleplayer takes netplay paths.
            hasFoundMatch = null;

            Patches.Unity.TransformFallbackDiagnostics.Reset();
            Services.TrackerAttributionDiagnostics.Reset();
            Services.HostGuardDiagnostics.Reset();
            Services.FrameTimeDiagnostics.Reset();
            Services.BossDiagnostics.Reset();
            Patches.RunStatsPatches.ResetDiagnostics();
            Patches.Items.HitItemOwnership.ResetDiagnostics();
            Patches.Items.TeammateCombatStats.ResetDiagnostics();
            Patches.Enemies.DebuffAttribution.Clear();
            Services.PickupLifetimeDiagnostics.Reset();
            playerStatsSyncService?.Reset();
            desyncDetectorService?.Reset();
            Patches.WeaponInventoryPatches.ResetForeignInventoryReport();
            Services.UnityExceptionDiagnostics.RemindWhereTracesAre();
            Services.UnityExceptionDiagnostics.Reset();

            // PERF 1A: OnDisable may never fire for the finished session's enemies.
            Enemies.TargetSwitcherManager.Clear();
            Snapshot.EnemyInterpolatorManager.Clear();

            // Encounter-UI statics hold destroyed components from the finished session.
            Patches.ChestWindowUiPatches.Reset();
            Patches.LevelUpScreenPatches.Reset();
            Patches.Projectiles.ProjectileBasePatches.ClearOpacityCache();
            Helpers.EncounterInputGrace.Reset();
            Services.AllocationDiagnostics.Reset();
            Services.BandwidthDiagnostics.Reset();
            Services.EncounterPauseDiagnostics.Reset();

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
                stateBroadcastService?.Reset();
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"Error resetting state broadcast: {ex}");
            }
            finally
            {
                stateBroadcastService = null;
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
                stateBroadcastService = Plugin.Services.GetRequiredService<IStateBroadcastService>();
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

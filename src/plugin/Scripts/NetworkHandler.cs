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
        private IStateBroadcastService stateBroadcastService;
        private ISteamNetTransport steamNetTransport;
        private INetTransport netTransport;
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

        /// <summary>
        /// The Steam transport's equivalent of <see cref="OnMatchFound"/>: brings the per-frame
        /// loops up for a session that has no matchmaker behind it.
        ///
        /// <para>Separate from <c>OnMatchFound</c> rather than folded into it because the two learn
        /// the same fact from different places and at different moments. A matchmaker session is
        /// told its role by the server in <c>MatchInfo</c>; a Steam session reads it off lobby
        /// ownership, which Steam already arbitrates. Sharing an entry point would mean one of them
        /// inferring a role it was never given.</para>
        /// </summary>
        public void BeginSteamSession(bool isHost)
        {
            hasFoundMatch = true;
            this.isHost = isHost;

            udpClientService = Plugin.Services.GetRequiredService<IUdpClientService>();
            stateBroadcastService = Plugin.Services.GetRequiredService<IStateBroadcastService>();
            synchronizationService = Plugin.Services.GetRequiredService<ISynchronizationService>();

            // Still told, even though it carries nothing here. SynchronizationService asks the
            // LiteNetLib service for IsHost in a couple of places that have not moved to
            // INetTransport yet, and a null there reads as "role undecided".
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

                // The Steam transport's receive pump belongs here, with the netplay loop, and not
                // only on SteamTicker. SteamTicker is the Steam *lobby* ticker: it is fine for the
                // menu, but a session that has loaded into a run depends on it for every inbound
                // message, and the first in-game test of the Steam path stalled with both ends
                // sending and neither receiving - a symmetry that points at the pump rather than
                // the wire. Polling twice a frame is harmless; the second call finds an empty queue.
                if (ModConfig.UseSteamTransport.Value)
                {
                    steamNetTransport ??= Plugin.Services.GetService<ISteamNetTransport>();
                    steamNetTransport?.Poll();
                }

                if (GameManager.Instance == null || GameManager.Instance.player == null || GameManager.Instance.player.inventory == null) return;

                Services.AllocationDiagnostics.Sample(ModConfig.LogAllocationRate.Value);
                // The active transport, whichever it is — a diagnostic that reads the wrong one
                // reports on a socket nobody is using.
                // The active transport, whichever it is — a diagnostic that reads the wrong one
                // reports on a socket nobody is using. Cached, never resolved per frame.
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

            // Leaving the Steam lobby is part of ending a Steam session, and this is the single
            // path every teardown funnels through — cancel, failure, and backing out of the lobby
            // panel all reach here.
            //
            // It became load-bearing when the invite flow stopped leaving the lobby it joined: a
            // player who backed out was still a member of a lobby nothing would release, so the
            // next invite was refused with "Refusing to join a lobby while InLobby" and they were
            // stuck until they restarted the game. Guarded, because on the matchmaker path the
            // Steam lobby belongs to the presence bridge and is not ours to close.
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

            // Separate from the transport's reset, and deliberately not folded into it: the stream
            // pacing is per session but has nothing to do with the socket, and the transport no
            // longer owns it.
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

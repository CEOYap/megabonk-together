using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Managers;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Extensions;
using MegabonkTogether.Helpers;
using MemoryPack;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MegabonkTogether.Services
{
    internal class PeerIntroduction
    {
        public string Name { get; internal set; }
        public uint ConnectionId { get; internal set; }
        public bool IsHost { get; internal set; }
        public bool HasSelected { get; set; }
        public int Latency { get; set; }

        public PeerIntroduction(string name, uint connectionId, bool isHost, bool hasSelected = false)
        {
            Name = name;
            ConnectionId = connectionId;
            IsHost = isHost;
            HasSelected = hasSelected;
            Latency = 0;
        }
    }

    public interface IUdpClientService
    {
        public bool Initialize();
        public void Update();

        public void Poll();

        public Task<bool> HandleMatch(MatchInfo matchInfo, uint selfConnectionId, string rdvServerHost, uint rdvServerPort, bool enabledSharedExperience);
        public bool HasAllPeersConnected();

        public void SendToAllClients<T>(T data, DeliveryMethod deliveryMethod) where T : IGameNetworkMessage;
        public void SendToAllClients(byte[] data, DeliveryMethod deliveryMethod, string messageTypeName = null);

        public void SendToHost<T>(T data, DeliveryMethod? deliveryMethod = null) where T : IGameNetworkMessage;
        public void SendToClient<T>(NetPeer client, T data, uint netPlayerId) where T : IGameNetworkMessage;
        public void SendToAllClientsExcept<T>(int netPlayerId, uint sender, T data) where T : IGameNetworkMessage;
        public bool? IsHost();
        public void UpdateEnemies();
        public void UpdateProjectiles();
        public void UpdateTumbleWeeds();

        public void Reset();
        public void GameOver();

        public int GetNetPeerCount();
        public bool AreAllPeersReady();
        public int GetCurrentReadyPeersCount();
        public int GetLatency(uint connectionId);
        public void UpdateMode(bool isHost);
        public bool IsHandlingConnection();
        public void CancelAnyNatIntroduction();
        public bool HasHandledHost();
        public void ResetHandledHost();
        public void RemovePeer(uint clientConnectionId);
    }
    internal class UdpClientService(
            IPlayerManagerService playerManagerService,
            IEnemyManagerService enemyManagerService,
            IProjectileManagerService projectileManagerService,
            IFinalBossOrbManagerService finalBossOrbManagerService,
            ISpawnedObjectManagerService spawnedObjectManagerService,
            IEncounterService encounterService,
            ManualLogSource logger) : IUdpClientService
    {
        private const int MAX_PACKET_SIZE_BYTES = 1000;

        // Slack left in each chunk of a split stream tick for the message envelope that every chunk
        // repeats — the MemoryPack union tag plus the empty-collection headers for the fields that
        // stream does not populate. See SendStreamUpdate.
        private const int CHUNK_ENVELOPE_HEADROOM_BYTES = 100;
        private const int STARTING_GAME_UDP_PORT = 27015;
        private int GAME_UDP_PORT = STARTING_GAME_UDP_PORT;
        private NetManager netManager;
        private EventBasedNetListener listener;
        private EventBasedNatPunchListener natListener;
        private TaskCompletionSource<bool> natPunchComplete;
        private readonly ConcurrentDictionary<int, NetPeer> gamePeers = [];
        private uint? selfConnectionId;
        private readonly ConcurrentDictionary<int, PeerIntroduction> gamePeersIntroduced = [];
        private readonly ConcurrentDictionary<uint, PeerIntroduction> gamePeersIntroducedByRelay = [];
        private bool? isHost { get; set; } = null;
        private int expectedPeerCount = 0;
        private bool hasStarted = false;
        private bool hasAllPeersConnected = false;
        private bool isHandlingConnection = false;
        private bool hasHandledHost = false;
        private bool isGameOver = false;

        private string rdvServerHost;
        private int rdvServerPort;
        private readonly HashSet<uint> usesRelay = [];
        private NetPeer relayPeer = null;
        private readonly object relayPeerLock = new();

        private ConcurrentDictionary<string, bool> tokens = new();
        private bool hasTriedForceRelay = false;

        private const int POLL_INTERVAL_MS = 5;

        private CancellationTokenSource pollingCancelationTokenSource;

        public bool Initialize()
        {
            if (hasStarted)
            {
                return true;
            }

            listener = new EventBasedNetListener();
            natListener = new EventBasedNatPunchListener();
            netManager = new NetManager(listener)
            {
                IPv6Enabled = true,
                UnconnectedMessagesEnabled = true,
                NatPunchEnabled = true,
                EnableStatistics = true,
                DisconnectTimeout = 15000,
                UpdateTime = POLL_INTERVAL_MS
            };

            bool portInUse = true;
            while (portInUse)
            {
                try
                {
                    hasStarted = netManager.Start(GAME_UDP_PORT);
                    if (hasStarted)
                    {
                        portInUse = false;
                    }
                    else
                    {
                        GAME_UDP_PORT++;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Port {GAME_UDP_PORT} in use, trying next port. Exception: {ex.Message}");
                    GAME_UDP_PORT++;
                }
            }

            if (!hasStarted)
            {
                Plugin.Log.LogError("Failed to start NetManager");
                return false;
            }

            Plugin.Log.LogInfo($"UDPClient listening on port {GAME_UDP_PORT}");

            netManager.NatPunchModule.Init(natListener);

            natListener.NatIntroductionRequest += (local, remote, token) =>
            {
                // ignore on client side
            };

            natListener.NatIntroductionSuccess += (target, natType, token) =>
            {
                try
                {
                    if (!tokens.TryAdd(token, true)) // Atomique!
                    {
                        Plugin.Log.LogWarning($"Duplicate NAT introduction success with token={token}, ignoring.");
                        return;
                    }

                    Plugin.Log.LogInfo($"NAT introduction success, natType={natType}, token={token}");

                    if (netManager != null && netManager.IsRunning)
                    {
                        Plugin.Log.LogInfo($"Connecting...");
                        // The connect key is write-only here: after NAT introduction both peers
                        // Connect at each other, and LiteNetLib reconciles that cross-connect
                        // internally without ever raising ConnectionRequestEvent. A protocol
                        // version sent here is never read. Tried and disproved in-game — see
                        // P1-3 in docs/netplay/01-critical-fixes.md before reaching for this again.
                        netManager.Connect(target, "yourKey");
                    }
                    else
                    {
                        Plugin.Log.LogError("NetManager is not running, cannot connect");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"Error in NAT introduction success handler: {ex}");
                }
            };

            listener.ConnectionRequestEvent += request =>
            {
                // NOTE: this handler does NOT run on the normal NAT-punch path — the cross-connect
                // is resolved inside LiteNetLib. Absence of this log line in a successful session
                // is the evidence. Do not put a version gate here; see P1-3 in
                // docs/netplay/01-critical-fixes.md.
                Plugin.Log.LogInfo($"Got a connection request from remote");
                request.Accept();
            };

            listener.PeerConnectedEvent += peer =>
            {
                if (peer.Address.ToString() != DnsHelper.ResolveDomainToIp(this.rdvServerHost))
                {
                    gamePeers.TryAdd(peer.Id, peer);
                }
                else
                {
                    logger.LogInfo($"Connected to relay server: {peer.Id}");
                    lock (relayPeerLock)
                    {
                        relayPeer = peer;
                    }

                    var writer = new NetDataWriter();
                    writer.Put($"{selfConnectionId}|RELAY_BIND");
                    peer.Send(writer, DeliveryMethod.ReliableOrdered);
                }

                if (isHost == null)
                {
                    Plugin.Log.LogError("IsHost not set?!");
                    return;
                }

                if (isHost.HasValue && isHost.Value)
                {
                    Plugin.Log.LogInfo($"Host: Client connected ({gamePeers.Count + usesRelay.Count}/{expectedPeerCount})");
                }
                else
                {
                    Plugin.Log.LogInfo($"Client: Connected to host");
                    IGameNetworkMessage introduced = new Introduced
                    {
                        ConnectionId = selfConnectionId.Value,
                        Name = Configuration.ModConfig.PlayerName.Value,
                        IsHost =
                            Plugin.Instance.Mode.Mode == NetworkModeType.Random && isHost.HasValue && isHost.Value
                            || Plugin.Instance.Mode.Mode == NetworkModeType.Friendlies && Plugin.Instance.Mode.Role == Role.Host
                    };

                    SendToHost(introduced);
                }
            };

            listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
            {
                try
                {
                    byte[] data = reader.GetRemainingBytes();

                    IGameNetworkMessage deserializedMsg;
                    try
                    {
                        deserializedMsg = MemoryPackSerializer.Deserialize<IGameNetworkMessage>(data);
                    }
                    catch (MemoryPackSerializationException)
                    {
                        logger.LogDebug($"Corrupted packet from {peer.Address}, discarding");
                        return;
                    }

                    if (deserializedMsg == null)
                    {
                        logger.LogDebug($"Failed to deserialize message from {peer.Address}");
                        return;
                    }

                    HandleMessage(deserializedMsg, peer.Id);
                }
                catch (MemoryPackSerializationException)
                {
                    logger.LogDebug($"Packet corruption detected from {peer.Address}, discarding");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Unexpected error handling network : {ex}");
                }
            };

            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                logger.LogInfo($"Peer disconnected: {info.Reason}");

                if (peer == null)
                {
                    return;
                }

                HandleDisconnectedPeer(peer);
            };

            listener.NetworkErrorEvent += (endPoint, socketError) =>
            {
                Plugin.Log.LogError($"Network error to {endPoint}: {socketError}");
            };

            listener.NetworkLatencyUpdateEvent += (peer, latency) =>
            {
                var peerIntro = gamePeersIntroduced.FirstOrDefault(p => p.Key == peer.Id);
                if (peerIntro.Value != null)
                {
                    peerIntro.Value.Latency = latency;
                    gamePeersIntroduced[peerIntro.Key] = peerIntro.Value;
                }
            };

            listener.NetworkReceiveUnconnectedEvent += (endPoint, reader, messageType) =>
            {
                var msg = reader.GetString();

                (var mode, var remoteConnectionId, var remoteEndpoint) = msg.Split('|') switch
                {
                    [var m, var rcid, var rep] => (m, uint.Parse(rcid), rep),
                    _ => (null, 0u, null)
                };

                if (mode == null)
                {
                    Plugin.Log.LogWarning($"Invalid unconnected message format: {msg}");
                    return;
                }

                if (mode == "USE_RELAY")
                {
                    logger.LogInfo($"Received USE_RELAY instruction, connecting to relay server...");
                    usesRelay.Add(remoteConnectionId);

                    bool alreadyConnected;
                    lock (relayPeerLock)
                    {
                        alreadyConnected = relayPeer != null;
                    }
                    if (alreadyConnected)
                    {
                        logger.LogInfo($"Already connected to relay server.");
                        return;
                    }

                    var connectionKey = $"{selfConnectionId.Value}|{remoteEndpoint}|RELAY";
                    netManager.Connect(rdvServerHost, rdvServerPort, connectionKey);
                }
            };

            return hasStarted;
        }

        public int GetNetPeerCount()
        {
            return gamePeers.Count;
        }

        private void HandleDisconnectedPeer(NetPeer peer)
        {
            if (usesRelay.Any())
            {
                bool isRelayPeer;
                lock (relayPeerLock)
                {
                    isRelayPeer = relayPeer == peer;
                }
                if (isRelayPeer)
                {
                    Plugin.Log.LogInfo($"Relay peer disconnected.");
                    lock (relayPeerLock)
                    {
                        relayPeer = null;
                    }
                    usesRelay.Clear();

                    var host = gamePeersIntroducedByRelay.FirstOrDefault(p => p.Value.IsHost);
                    gamePeersIntroducedByRelay.Clear();

                    Plugin.StartNotification(
                    ("MegabonkTogether", "ClientDisconnected"),
                    ("MegabonkTogether", "ClientDisconnected_Description"),
                    [],
                    AudioManager.Instance.uiAbort,
                    item: EItem.BobDead);

                    Plugin.GoToMainMenu();
                    return;
                }

            }


            if (!gamePeers.TryRemove(peer.Id, out _))
            {
                Plugin.Log.LogWarning($"Disconnected peer {peer.Id} not found in gamePeers");
                return;
            }

            if (!gamePeersIntroduced.TryRemove(peer.Id, out var introInfo))
            {
                Plugin.Log.LogWarning($"Disconnected peer {peer.Id} introduction info not found");
                return;
            }


            if (usesRelay.Any())
            {
                if (!isHost.Value && usesRelay.Count == 1)
                {
                    usesRelay.Clear();
                }
                else if (isHost.Value)
                {
                    var info = gamePeersIntroduced.FirstOrDefault(p => p.Value.ConnectionId == peer.Id);
                    if (info.Value != null)
                    {
                        usesRelay.Remove(info.Value.ConnectionId);
                    }
                }
            }


            if (introInfo.IsHost)
            {
                Plugin.StartNotification(
                    ("MegabonkTogether", "HostDisconnected"),
                    ("MegabonkTogether", "HostDisconnected_Description"),
                    [introInfo.Name],
                    AudioManager.Instance.uiAbort,
                    item: EItem.BobDead
                );
                Plugin.GoToMainMenu();
            }
            else
            {
                if (gamePeers.IsEmpty && (Plugin.Instance.Mode.Mode == NetworkModeType.Random || Plugin.Instance.Mode.Mode == NetworkModeType.Friendlies && GameManager.Instance?.player != null))
                {
                    Plugin.Log.LogInfo($"All players disconnected, returning to main menu. : is host : {isHost.Value}");

                    Plugin.StartNotification(
                        ("MegabonkTogether", "AllPlayerDisconnected"),
                        ("MegabonkTogether", "AllPlayerDisconnected_Description"),
                        [introInfo.Name],
                        AudioManager.Instance.uiAbort,
                        item: EItem.BobDead
                    );
                    Plugin.GoToMainMenu();
                }
                else
                {
                    if (isHost.HasValue && isHost.Value)
                    {
                        IGameNetworkMessage disconnectedPlayer = new PlayerDisconnected
                        {
                            ConnectionId = introInfo.ConnectionId
                        };

                        EventManager.OnPlayerDisconnected(disconnectedPlayer as PlayerDisconnected);
                        SendToAllClients(disconnectedPlayer, DeliveryMethod.ReliableOrdered);
                    }
                }
            }
        }

        public bool AreAllPeersReady()
        {
            if (!isHost.HasValue || !isHost.Value)
            {
                return false;
            }

            var areAllRelayReady = true;
            var areGamePeersReady = true;

            if (usesRelay.Any())
            {
                areAllRelayReady = gamePeersIntroducedByRelay.Values.All(p => p.HasSelected);
            }

            areGamePeersReady = gamePeersIntroduced.Values.All(p => p.HasSelected);

            return areAllRelayReady && areGamePeersReady;
        }

        public int GetCurrentReadyPeersCount()
        {
            if (!isHost.HasValue || !isHost.Value)
            {
                return 0;
            }

            int readyCount = 0;

            if (usesRelay.Any())
            {
                readyCount += gamePeersIntroducedByRelay.Values.Count(p => p.HasSelected);
            }

            readyCount += gamePeersIntroduced.Values.Count(p => p.HasSelected);

            return readyCount;
        }

        private void HandleMessage(IGameNetworkMessage message, int netPeerId)
        {
            if (!isHost.Value)
            {
                switch (message)
                {
                    case Introduced introduced:
                        if (relayPeer != null && netPeerId == relayPeer.Id)
                        {
                            if (!gamePeersIntroducedByRelay.TryAdd(introduced.ConnectionId, new PeerIntroduction(introduced.Name, introduced.ConnectionId, introduced.IsHost)))
                            {
                                Plugin.Log.LogWarning($"Duplicate introduction from relay for host={netPeerId}, ignoring.");
                            }

                            var playerByRelay = playerManagerService.GetPlayer(introduced.ConnectionId);
                            if (playerByRelay != null)
                            {
                                playerByRelay.Name = introduced.Name;
                                playerManagerService.UpdatePlayer(playerByRelay);
                            }

                            return;
                        }

                        if (!gamePeersIntroduced.TryAdd(netPeerId, new PeerIntroduction(introduced.Name, introduced.ConnectionId, introduced.IsHost)))
                        {
                            Plugin.Log.LogWarning($"Duplicate introduction from host={netPeerId}, ignoring.");
                            return;
                        }

                        var player = playerManagerService.GetPlayer(introduced.ConnectionId);
                        if (player != null)
                        {
                            player.Name = introduced.Name;
                            playerManagerService.UpdatePlayer(player);
                        }

                        break;
                    case PlayerDisconnected playerDisconnected:
                        if (usesRelay.Any())
                        {
                            var disconnectedPeerByRelay = gamePeersIntroducedByRelay.FirstOrDefault(p => p.Value.ConnectionId == playerDisconnected.ConnectionId);
                            if (disconnectedPeerByRelay.Value != null)
                            {
                                if (disconnectedPeerByRelay.Value.IsHost)
                                {
                                    logger.LogWarning($"Host disconnected via relay.");
                                    HandleDisconnectedPeer(relayPeer);
                                }
                                else
                                {
                                    EventManager.OnPlayerDisconnected(playerDisconnected);
                                }

                                return;
                            }
                        }

                        var disconnectedPeer = gamePeersIntroduced.FirstOrDefault(p => p.Value.ConnectionId == playerDisconnected.ConnectionId);

                        if (disconnectedPeer.Value == null) //Disonnected peer not a host
                        {
                            EventManager.OnPlayerDisconnected(playerDisconnected);
                            return;
                        }

                        //Host disconnected
                        var peer = gamePeers.FirstOrDefault(p => p.Value.Id == disconnectedPeer.Key).Value;
                        HandleDisconnectedPeer(peer);

                        break;
                    case LobbyUpdates lobbyUpdate:
                        OnLobbyUpdate(lobbyUpdate);
                        break;
                    case ProjectilesUpdate projectilesUpdate:
                        EventManager.OnProjectilesUpdate(projectilesUpdate.Projectiles);
                        break;
                    case SpawnedObject spawnedObject:
                        EventManager.OnSpawnedObject(spawnedObject);
                        break;
                    case SpawnedEnemy spawnedEnemy:
                        EventManager.OnSpawnedEnemy(spawnedEnemy);
                        break;
                    case AbstractSpawnedProjectile spawnedProjectile:
                        EventManager.OnSpawnedProjectile(spawnedProjectile);
                        break;
                    case SelectedCharacter selectedCharacter:
                        EventManager.OnSelectedCharacter(selectedCharacter);
                        break;
                    case EnemyDied enemyDied:
                        EventManager.OnEnemyDied(enemyDied);
                        break;
                    case ProjectileDone projectileDone:
                        EventManager.OnProjectileDone(projectileDone);
                        break;
                    case SpawnedPickupOrb spawnedPickup:
                        EventManager.OnSpawnedPickupOrb(spawnedPickup);
                        break;
                    case SpawnedPickup spawnedPickupItem:
                        EventManager.OnSpawnedPickup(spawnedPickupItem);
                        break;
                    case PickupFollowingPlayer pickupFollowingPlayer:
                        EventManager.OnPickupFollowingPlayer(pickupFollowingPlayer);
                        break;
                    case PickupApplied pickupApplied:
                        EventManager.OnPickupApplied(pickupApplied);
                        break;
                    case SpawnedChest spawnedChest:
                        EventManager.OnSpawnedChest(spawnedChest);
                        break;
                    case ChestOpened chestOpened:
                        EventManager.OnChestOpened(chestOpened);
                        break;
                    case WeaponAdded weaponAdded:
                        EventManager.OnWeaponAdded(weaponAdded);
                        break;
                    case InteractableUsed interactableUsed:
                        EventManager.OnInteractableUsed(interactableUsed);
                        break;
                    case StartingChargingShrine startingChargingShrine:
                        EventManager.OnStartingChargingShrine(startingChargingShrine);
                        break;
                    case StoppingChargingShrine stoppingChargingShrine:
                        EventManager.OnStoppingChargingShrine(stoppingChargingShrine);
                        break;
                    case EnemyExploder enemyExploder:
                        EventManager.OnEnemyExploder(enemyExploder);
                        break;
                    case EnemyDamaged enemyDamaged:
                        EventManager.OnEnemyDamaged(enemyDamaged);
                        break;
                    case SpawnedEnemySpecialAttack spawnedEnemySpecialAttack:
                        EventManager.OnSpawnedEnemySpecialAttack(spawnedEnemySpecialAttack);
                        break;
                    case StartingChargingPylon startingChargingPylon:
                        EventManager.OnStartingChargingPylon(startingChargingPylon);
                        break;
                    case StoppingChargingPylon stoppingChargingPylon:
                        EventManager.OnStoppingChargingPylon(stoppingChargingPylon);
                        break;
                    case FinalBossOrbSpawned finalBossOrbSpawned:
                        EventManager.OnFinalBossOrbSpawned(finalBossOrbSpawned);
                        break;
                    case FinalBossOrbDestroyed finalBossOrbDestroyed:
                        EventManager.OnFinalBossOrbDestroyed(finalBossOrbDestroyed);
                        break;
                    case StartedSwarmEvent startedSwarmEvent:
                        EventManager.OnStartedSwarmEvent(startedSwarmEvent);
                        break;
                    case GameOver gameOver:
                        EventManager.OnGameOver(gameOver);
                        break;
                    case RetargetedEnemies retargetedEnemies:
                        EventManager.OnRetargetedEnemies(retargetedEnemies);
                        break;
                    case RunStarted runStarted:
                        EventManager.OnRunStarted(runStarted);
                        break;
                    case TomeAdded tomeAdded:
                        EventManager.OnTomeAdded(tomeAdded);
                        break;
                    case LightningStrike lightningStrike:
                        EventManager.OnLightningStrike(lightningStrike);
                        break;
                    case TornadoesSpawned tornadoesSpawned:
                        EventManager.OnTornadoesSpawned(tornadoesSpawned);
                        break;
                    case StormStarted stormStarted:
                        EventManager.OnStormStarted(stormStarted);
                        break;
                    case StormStopped stormStopped:
                        EventManager.OnStormStopped(stormStopped);
                        break;
                    case TumbleWeedSpawned tumbleWeedSpawned:
                        EventManager.OnTumbleWeedSpawned(tumbleWeedSpawned);
                        break;
                    case TumbleWeedsUpdate tumbleWeedsUpdate:
                        EventManager.OnTumbleWeedsUpdate(tumbleWeedsUpdate.TumbleWeeds);
                        break;
                    case TumbleWeedDespawned tumbleWeedDespawned:
                        EventManager.OnTumbleWeedDespawned(tumbleWeedDespawned);
                        break;
                    case ItemAdded itemAdded:
                        EventManager.OnItemAdded(itemAdded);
                        break;
                    case ItemRemoved itemRemoved:
                        EventManager.OnItemRemoved(itemRemoved);
                        break;
                    case WeaponToggled weaponToggled:
                        EventManager.OnWeaponToggled(weaponToggled);
                        break;
                    case SpawnedObjectInCrypt spawnedObjectInCrypt:
                        EventManager.OnSpawnedObjectInCrypt(spawnedObjectInCrypt);
                        break;
                    case StartingChargingLamp startingChargingLamp:
                        EventManager.OnStartingChargingLamp(startingChargingLamp);
                        break;
                    case StoppingChargingLamp stoppingChargingLamp:
                        EventManager.OnStoppingChargingLamp(stoppingChargingLamp);
                        break;
                    case TimerStarted timerStarted:
                        EventManager.OnTimerStarted(timerStarted);
                        break;
                    case HatChanged hatChanged:
                        EventManager.OnHatChanged(hatChanged);
                        break;
                    case SpawnedReviver spawnedReviver:
                        EventManager.OnSpawnedReviver(spawnedReviver);
                        break;
                    case PlayerRespawned playerRespawned:
                        EventManager.OnPlayerRespawned(playerRespawned);
                        break;
                    case PlayerDied playerDied:
                        EventManager.OnPlayerDied(playerDied);
                        break;
                    case AddXp addXp:
                        EventManager.OnAddXp(addXp);
                        break;
                    case CloseEncounter closeEncounter:
                        EventManager.OnCloseEncounter(closeEncounter);
                        break;
                    case GoldChanged goldChanged:
                        EventManager.OnGoldChanged(goldChanged);
                        break;
                    default:
                        Plugin.Log.LogWarning($"Unknown message type received. message={message}");
                        break;
                }
            }
            else
            {
                switch (message)
                {
                    case Introduced introduced:
                        if (relayPeer != null && netPeerId == relayPeer.Id)
                        {
                            if (!gamePeersIntroducedByRelay.TryAdd(introduced.ConnectionId, new PeerIntroduction(introduced.Name, introduced.ConnectionId, introduced.IsHost)))
                            {
                                Plugin.Log.LogWarning($"Duplicate introduction from netPlayerId={netPeerId} via relay, ignoring.");
                            }

                            if (Plugin.Instance.Mode.Mode == Common.Models.NetworkModeType.Friendlies)
                            {
                                Plugin.StartNotification(("MegabonkTogether", "FriendliesClientJoinSuccess"), ("MegabonkTogether", "FriendliesClientJoinSuccessDesc"), [introduced.Name]);
                            }

                            return;
                        }
                        else
                        {
                            if (!gamePeersIntroduced.TryAdd(netPeerId, new PeerIntroduction(introduced.Name, introduced.ConnectionId, introduced.IsHost)))
                            {
                                Plugin.Log.LogWarning($"Duplicate introduction from netPlayerId={netPeerId}, ignoring.");
                                return;
                            }
                        }

                        if (Plugin.Instance.Mode.Mode == Common.Models.NetworkModeType.Friendlies)
                        {
                            Plugin.StartNotification(("MegabonkTogether", "FriendliesClientJoinSuccess"), ("MegabonkTogether", "FriendliesClientJoinSuccessDesc"), [introduced.Name]);
                        }

                        IGameNetworkMessage introducedResponse = new Introduced
                        {
                            ConnectionId = selfConnectionId.Value,
                            Name = Configuration.ModConfig.PlayerName.Value,
                            IsHost = isHost.Value
                        };

                        var peer = gamePeers.FirstOrDefault(p => p.Value.Id == netPeerId).Value;
                        if (peer != null)
                        {
                            SendToClient(peer, introducedResponse, introduced.ConnectionId);

                            var playerModel = playerManagerService.GetPlayer(introduced.ConnectionId);
                            if (playerModel != null)
                            {
                                playerModel.Name = introduced.Name;
                                playerManagerService.UpdatePlayer(playerModel);
                            }
                        }

                        break;
                    case ClientInGameReady clientInGameReady:
                        var clientReadyId = clientInGameReady.ConnectionId;
                        var player = playerManagerService.GetPlayer(clientReadyId);
                        if (player == null)
                        {
                            Plugin.Log.LogWarning($"Received ClientReady from unknown player with connection ID {clientReadyId}.");
                            return;
                        }
                        player.IsReady = true;
                        playerManagerService.UpdatePlayer(player);

                        Plugin.Log.LogInfo($"Player {clientReadyId} is ready.");

                        break;
                    case PlayerUpdate playerUpdate:
                        var playerUpdateId = playerUpdate.ConnectionId;
                        var playerToUpdate = playerManagerService.GetPlayer(playerUpdateId);
                        if (playerToUpdate == null)
                        {
                            Plugin.Log.LogWarning($"Received PlayerUpdate from unknown player with connection ID {playerUpdateId}.");
                            return;
                        }

                        playerToUpdate.Position = Quantizer.Quantize(playerUpdate.Position.ToUnityVector3());
                        playerToUpdate.MovementState = playerUpdate.MovementState;
                        playerToUpdate.AnimatorState = playerUpdate.AnimatorState;
                        playerToUpdate.ConnectionId = playerUpdate.ConnectionId;
                        //if (playerToUpdate.Hp != 0)
                        //{
                        playerToUpdate.Hp = playerUpdate.Hp;
                        playerToUpdate.Shield = playerUpdate.Shield;
                        //}
                        playerToUpdate.MaxHp = playerUpdate.MaxHp;
                        playerToUpdate.MaxShield = playerUpdate.MaxShield;
                        //playerToUpdate.Xp = playerUpdate.Xp;
                        playerToUpdate.Inventory = playerUpdate.Inventory;
                        playerToUpdate.Name = playerUpdate.Name;

                        playerManagerService.UpdatePlayer(playerToUpdate);

                        EventManager.OnPlayerUpdate(playerUpdate);
                        break;
                    case SelectedCharacter selectedCharacter:

                        if (gamePeersIntroducedByRelay.TryGetValue(selectedCharacter.ConnectionId, out var introInfoByRelay))
                        {
                            introInfoByRelay.HasSelected = true;
                            gamePeersIntroducedByRelay[selectedCharacter.ConnectionId] = introInfoByRelay;
                        }

                        if (gamePeersIntroduced.TryGetValue(netPeerId, out var introInfo))
                        {
                            introInfo.HasSelected = true;
                            gamePeersIntroduced[netPeerId] = introInfo;
                        }

                        var toUpdate = playerManagerService.GetPlayer(selectedCharacter.ConnectionId); //We could technically use EventManager.OnSelectedCharacter but the metrics RunStatistics sent later will miss the update
                        if (toUpdate == null)
                        {
                            logger.LogWarning($"Player not found for ConnectionId: {selectedCharacter.ConnectionId}");
                            return;
                        }

                        toUpdate.Character = selectedCharacter.Character;
                        toUpdate.Skin = selectedCharacter.Skin;
                        playerManagerService.UpdatePlayer(toUpdate);

                        SendToAllClientsExcept(netPeerId, selectedCharacter.ConnectionId, selectedCharacter);

                        if (AreAllPeersReady() && playerManagerService.HasSelectedCharacter() && Plugin.Instance.IS_HOST_READY)
                        {
                            var runConfig = WindowManager.activeWindow.GetComponentInChildren<MapSelectionUi>().runConfig;
                            MapController.StartNewMap(runConfig);
                        }
                        break;
                    case AbstractSpawnedProjectile spawnedProjectile:
                        EventManager.OnSpawnedProjectile(spawnedProjectile);
                        SendToAllClientsExcept(netPeerId, spawnedProjectile.OwnerId, spawnedProjectile);
                        break;
                    case ProjectileDone projectileDone:
                        EventManager.OnProjectileDone(projectileDone);
                        SendToAllClientsExcept(netPeerId, projectileDone.SenderConnectionId, projectileDone);
                        break;
                    case EnemyDied enemyDied:
                        EventManager.OnEnemyDied(enemyDied);
                        SendToAllClientsExcept(netPeerId, enemyDied.DiedByOwnerId, enemyDied);
                        break;
                    case PickupApplied pickupApplied:
                        EventManager.OnPickupApplied(pickupApplied);
                        SendToAllClientsExcept(netPeerId, pickupApplied.OwnerId, pickupApplied);
                        break;
                    case PickupFollowingPlayer pickupFollowingPlayer:
                        EventManager.OnPickupFollowingPlayer(pickupFollowingPlayer);
                        SendToAllClientsExcept(netPeerId, pickupFollowingPlayer.PlayerId, pickupFollowingPlayer);
                        break;
                    case ChestOpened chestOpened:
                        EventManager.OnChestOpened(chestOpened);
                        SendToAllClientsExcept(netPeerId, chestOpened.OwnerId, chestOpened);
                        break;
                    case WeaponAdded weaponAdded:
                        EventManager.OnWeaponAdded(weaponAdded);
                        SendToAllClientsExcept(netPeerId, weaponAdded.OwnerId, weaponAdded);
                        break;
                    case InteractableUsed interactableUsed:
                        EventManager.OnInteractableUsed(interactableUsed);
                        SendToAllClientsExcept(netPeerId, interactableUsed.OwnerId, interactableUsed);
                        break;
                    case StartingChargingShrine startingChargingShrine:
                        EventManager.OnStartingChargingShrine(startingChargingShrine);
                        break;
                    case StoppingChargingShrine stoppingChargingShrine:
                        EventManager.OnStoppingChargingShrine(stoppingChargingShrine);
                        break;
                    case EnemyExploder enemyExploder:
                        EventManager.OnEnemyExploder(enemyExploder);
                        SendToAllClientsExcept(netPeerId, enemyExploder.SenderId, enemyExploder);
                        break;
                    case EnemyDamaged enemyDamaged:
                        EventManager.OnEnemyDamaged(enemyDamaged);
                        SendToAllClientsExcept(netPeerId, enemyDamaged.AttackerId, enemyDamaged);
                        break;
                    //case SpawnedEnemySpecialAttack spawnedEnemySpecialAttack:
                    //    EventManager.OnSpawnedEnemySpecialAttack(spawnedEnemySpecialAttack);
                    //    SendToAllClientsExcept(netPlayerId, spawnedEnemySpecialAttack);
                    //    break;
                    case StartingChargingPylon startingChargingPylon:
                        EventManager.OnStartingChargingPylon(startingChargingPylon);
                        SendToAllClientsExcept(netPeerId, startingChargingPylon.PlayerChargingId, startingChargingPylon);
                        break;
                    case StoppingChargingPylon stoppingChargingPylon:
                        EventManager.OnStoppingChargingPylon(stoppingChargingPylon);
                        SendToAllClientsExcept(netPeerId, stoppingChargingPylon.PlayerChargingId, stoppingChargingPylon);
                        break;
                    //case FinalBossOrbSpawned finalBossOrbSpawned:
                    //    EventManager.OnFinalBossOrbSpawned(finalBossOrbSpawned);
                    //    SendToAllClientsExcept(netPlayerId, finalBossOrbSpawned);
                    //    break;
                    case FinalBossOrbDestroyed finalBossOrbDestroyed:
                        EventManager.OnFinalBossOrbDestroyed(finalBossOrbDestroyed);
                        SendToAllClientsExcept(netPeerId, finalBossOrbDestroyed.SenderId, finalBossOrbDestroyed);
                        break;
                    case PlayerDied playerDied:
                        EventManager.OnPlayerDied(playerDied);
                        break;
                    case TomeAdded tomeAdded:
                        EventManager.OnTomeAdded(tomeAdded);
                        SendToAllClientsExcept(netPeerId, tomeAdded.OwnerId, tomeAdded);
                        break;
                    case InteractableCharacterFightEnemySpawned interactableCharacterFightEnemySpawned:
                        EventManager.OnInteractableCharacterFightEnemySpawned(interactableCharacterFightEnemySpawned);
                        break;
                    case WantToStartFollowingPickup wantToStartFollowingPickup:
                        EventManager.OnWantToStartFollowingPickup(wantToStartFollowingPickup);
                        break;
                    case ItemAdded itemAdded:
                        EventManager.OnItemAdded(itemAdded);
                        SendToAllClientsExcept(netPeerId, itemAdded.OwnerId, itemAdded);
                        break;
                    case ItemRemoved itemRemoved:
                        EventManager.OnItemRemoved(itemRemoved);
                        SendToAllClientsExcept(netPeerId, itemRemoved.OwnerId, itemRemoved);
                        break;
                    case WeaponToggled weaponToggled:
                        EventManager.OnWeaponToggled(weaponToggled);
                        SendToAllClientsExcept(netPeerId, weaponToggled.OwnerId, weaponToggled);
                        break;
                    //case SpawnedObjectInCrypt spawnedObjectInCrypt:
                    //    EventManager.OnSpawnedObjectInCrypt(spawnedObjectInCrypt);
                    //    SendToAllClientsExcept(netPlayerId, spawnedObjectInCrypt);
                    //    break;
                    case StartingChargingLamp startingChargingLamp:
                        EventManager.OnStartingChargingLamp(startingChargingLamp);
                        SendToAllClientsExcept(netPeerId, startingChargingLamp.PlayerChargingId, startingChargingLamp);
                        break;
                    case StoppingChargingLamp stoppingChargingLamp:
                        EventManager.OnStoppingChargingLamp(stoppingChargingLamp);
                        SendToAllClientsExcept(netPeerId, stoppingChargingLamp.PlayerChargingId, stoppingChargingLamp);
                        break;
                    case TimerStarted timerStarted:
                        EventManager.OnTimerStarted(timerStarted);
                        SendToAllClientsExcept(netPeerId, timerStarted.SenderId, timerStarted);
                        break;
                    case HatChanged hatChanged:
                        EventManager.OnHatChanged(hatChanged);
                        SendToAllClientsExcept(netPeerId, hatChanged.OwnerId, hatChanged);
                        break;
                    case PlayerDisconnected playerDisconnected: //Host only receives this message by the rdv server, normally its handled in LiteNet's PeerDisconnectedEvent
                        if (!usesRelay.Remove(playerDisconnected.ConnectionId))
                        {
                            logger.LogInfo($"PlayerDisconnected: ConnectionId {playerDisconnected.ConnectionId} was not using relay.");
                            return;
                        }

                        EventManager.OnPlayerDisconnected(playerDisconnected);
                        SendToAllClients(playerDisconnected, DeliveryMethod.ReliableOrdered);

                        break;
                    case AddXp addXp:
                        EventManager.OnAddXp(addXp);
                        SendToAllClientsExcept(netPeerId, addXp.OwnerId, addXp);
                        break;
                    case EncounterClosed encounterClosed:
                        encounterService.AddClosedEncounterForPlayer(encounterClosed.OwnerId);

                        if (encounterService.IsClosable())
                        {
                            IGameNetworkMessage closeMessage = new CloseEncounter
                            {
                            };

                            SendToAllClients(closeMessage, DeliveryMethod.ReliableOrdered);
                            EventManager.OnCloseEncounter(closeMessage as CloseEncounter);
                        }

                        break;
                    case GoldChanged goldChanged:
                        EventManager.OnGoldChanged(goldChanged);
                        SendToAllClientsExcept(netPeerId, goldChanged.OwnerId, goldChanged);
                        break;
                    default:
                        Plugin.Log.LogWarning($"Unknown message type received {message}");
                        break;
                }
            }
        }

        public void Reset()
        {
            hasHandledHost = false;
            selfConnectionId = null;
            isHost = null;
            expectedPeerCount = 0;
            hasAllPeersConnected = false;
            isGameOver = false;
            gamePeers.Clear();
            netManager?.Stop();
            hasStarted = false;
            gamePeersIntroduced.Clear();
            gamePeersIntroducedByRelay.Clear();
            hasTriedForceRelay = false;

            lock (relayPeerLock)
            {
                relayPeer?.Disconnect();
                relayPeer = null;
            }
            usesRelay.Clear();

            netManager.DisconnectAll();
        }

        public void GameOver()
        {
            isGameOver = true;
        }

        private void OnLobbyUpdate(LobbyUpdates lobbyUpdate) //TODO: move to synchronizationService
        {

            foreach (var player in lobbyUpdate.Players)
            {
                var existingPlayer = playerManagerService.GetPlayer(player.ConnectionId);
                if (existingPlayer != null)
                {
                    //var previousHp = existingPlayer.Hp;
                    //if (previousHp == 0)
                    //{
                    //    player.Hp = 0;
                    //}
                    playerManagerService.UpdatePlayer(player);

                    var playerUpdate = new PlayerUpdate
                    {
                        Position = Quantizer.Dequantize(player.Position).ToNumericsVector3(),
                        MovementState = player.MovementState,
                        AnimatorState = player.AnimatorState,
                        ConnectionId = player.ConnectionId,
                        Hp = player.Hp,
                        MaxHp = player.MaxHp,
                        Shield = player.Shield,
                        MaxShield = player.MaxShield,
                        //Xp = player.Xp,
                        Name = player.Name,
                        Inventory = player.Inventory,
                    };

                    //if (previousHp == 0)
                    //{
                    //    playerUpdate.Hp = 0;
                    //}

                    EventManager.OnPlayerUpdate(playerUpdate);
                }
            }

            EventManager.OnEnemiesUpdate(lobbyUpdate.Enemies);
            EventManager.OnFinalBossOrbsUpdate(lobbyUpdate.BossOrbs);
        }

        public async Task<bool> HandleMatch(MatchInfo matchInfo, uint selfConnectionId, string rdvServerHost, uint rdvServerPort, bool enabledSharedExperience)
        {
            if (hasHandledHost)
            {
                Plugin.Log.LogWarning("Already handled first connection,skipping");
                return false;
            }

            if (!this.selfConnectionId.HasValue) this.selfConnectionId = selfConnectionId;
            if (!this.isHost.HasValue) this.isHost = matchInfo.Peers.FirstOrDefault(p => p.ConnectionId == selfConnectionId)?.IsHost ?? false;

            if (!this.isHost.Value)
            {
                hasHandledHost = true; //Prevent further host connection
            }

            if (!Plugin.Instance.Mode.EnabledSharedExperience.HasValue)
            {
                Plugin.Instance.Mode.EnabledSharedExperience = enabledSharedExperience;
            }

            var allPlayers = playerManagerService.GetAllPlayers();
            foreach (var peer in matchInfo.Peers)
            {
                if (!allPlayers.Any(p => p.ConnectionId == peer.ConnectionId))
                {
                    playerManagerService.AddPlayer(peer.ConnectionId, peer.IsHost.Value, peer.ConnectionId == selfConnectionId);
                }
            }

            playerManagerService.SetSeed((int)matchInfo.Seed);

            Plugin.Log.LogInfo($"I am {(isHost.Value ? "HOST" : "CLIENT")}");

            if (isHost.Value)
            {
                expectedPeerCount = matchInfo.Peers.Count() - 1; // All clients except myself
                Plugin.Log.LogInfo($"Host expecting {expectedPeerCount} client connections");
            }
            else
            {
                expectedPeerCount = 1; // Only the host
                Plugin.Log.LogInfo("Client expecting connection to host");
            }

            var role = isHost.Value ? "host" : "client";
            var hostId = matchInfo.Peers.First(p => p.IsHost == true).ConnectionId.ToString();

            var uniqueToken = $"{role}|{hostId}|{selfConnectionId}";

            Plugin.Log.LogInfo($"Sending NAT punch request to rendezvous server with token {uniqueToken}");

            this.rdvServerHost = rdvServerHost;
            this.rdvServerPort = (int)rdvServerPort;

            netManager.NatPunchModule.SendNatIntroduceRequest(rdvServerHost, (int)rdvServerPort, uniqueToken);

            Plugin.Log.LogInfo("Waiting for NAT introductions and P2P connections...");

            natPunchComplete = new TaskCompletionSource<bool>();

            pollingCancelationTokenSource = new CancellationTokenSource();
            var token = pollingCancelationTokenSource.Token;

            var initialPollCts = new CancellationTokenSource();

            var pollTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && !initialPollCts.IsCancellationRequested)
                {
                    bool relayConnected;
                    lock (relayPeerLock)
                    {
                        relayConnected = relayPeer != null;
                    }
                    if (expectedPeerCount > 0 && gamePeers.Count + usesRelay.Count >= expectedPeerCount && (!usesRelay.Any() || relayConnected)) break;
                    Poll();
                    await Task.Delay(POLL_INTERVAL_MS);
                }

                if (token.IsCancellationRequested || initialPollCts.IsCancellationRequested)
                {
                    hasAllPeersConnected = false;
                    natPunchComplete.TrySetResult(false);
                    return;
                }

                hasAllPeersConnected = expectedPeerCount > 0 && gamePeers.Count + usesRelay.Count >= expectedPeerCount && (!usesRelay.Any() || relayPeer != null);
                natPunchComplete.TrySetResult(hasAllPeersConnected);
            });

            isHandlingConnection = true;

            var timeoutTask = Task.Delay(10000);
            var completedTask = await Task.WhenAny(natPunchComplete.Task, timeoutTask);

            if (token.IsCancellationRequested)
            {
                logger.LogWarning("P2P connection handling was cancelled.");
                isHandlingConnection = false;
                return false;
            }

            if (completedTask == natPunchComplete.Task && await natPunchComplete.Task)
            {
                logger.LogInfo($"P2P connections successful! Connected to {gamePeers.Count + usesRelay.Count} peers");
                isHandlingConnection = false;
                return true;
            }
            else
            {
                if (!hasTriedForceRelay && expectedPeerCount > 0)
                {
                    if (token.IsCancellationRequested)
                    {
                        logger.LogWarning("P2P connection handling was cancelled, no relay attempt");
                        return false;
                    }

                    logger.LogWarning($"P2P connection timeout - only {gamePeers.Count + usesRelay.Count}/{expectedPeerCount} peers connected, retrying with forced relay mode...");
                    hasTriedForceRelay = true;

                    initialPollCts.Cancel();

                    var forceRelayToken = $"{role}|{hostId}|{selfConnectionId}|force_relay";
                    logger.LogInfo($"Sending NAT punch request with force relay: {forceRelayToken}");
                    netManager.NatPunchModule.SendNatIntroduceRequest(rdvServerHost, (int)rdvServerPort, forceRelayToken);

                    natPunchComplete = new TaskCompletionSource<bool>();

                    var retryPollTask = Task.Run(async () =>
                    {
                        while (!token.IsCancellationRequested)
                        {
                            bool relayConnected;
                            lock (relayPeerLock)
                            {
                                relayConnected = relayPeer != null;
                            }
                            if (expectedPeerCount > 0 && gamePeers.Count + usesRelay.Count >= expectedPeerCount && (!usesRelay.Any() || relayConnected))
                                break;
                            Poll();
                            await Task.Delay(POLL_INTERVAL_MS);
                        }

                        if (token.IsCancellationRequested)
                        {
                            hasAllPeersConnected = false;
                            natPunchComplete.TrySetResult(false);
                            return;
                        }

                        hasAllPeersConnected = expectedPeerCount > 0 && gamePeers.Count + usesRelay.Count >= expectedPeerCount && (!usesRelay.Any() || relayPeer != null);
                        natPunchComplete.TrySetResult(hasAllPeersConnected);
                    });

                    var retryTimeoutTask = Task.Delay(10000);
                    var retryCompletedTask = await Task.WhenAny(natPunchComplete.Task, retryTimeoutTask);

                    if (token.IsCancellationRequested)
                    {
                        logger.LogWarning("P2P connection handling was cancelled during force relay retry.");
                        isHandlingConnection = false;
                        return false;
                    }

                    if (retryCompletedTask == natPunchComplete.Task && await natPunchComplete.Task)
                    {
                        logger.LogInfo($"P2P connections successful with forced relay! Connected to {gamePeers.Count + usesRelay.Count} peers");
                        isHandlingConnection = false;
                        return true;
                    }
                }

                logger.LogError($"P2P connection timeout - only {gamePeers.Count + usesRelay.Count}/{expectedPeerCount} peers connected");
                isHandlingConnection = false;
                return gamePeers.Count + usesRelay.Count > 0;
            }
        }

        public void Poll()
        {
            if (hasStarted)
            {
                netManager?.PollEvents();
                netManager?.NatPunchModule.PollEvents();
            }
        }

        public void Update()
        {
            UpdateLocalPlayer();

            if (isGameOver)
            {
                return;
            }

            if (isHost.HasValue && isHost.Value)
            {
                if (playerManagerService.IsGameOver())
                {
                    isGameOver = true;
                    IGameNetworkMessage wsMessage = new GameOver();
                    SendToAllClients(wsMessage, DeliveryMethod.ReliableOrdered);
                    EventManager.OnGameOver(wsMessage as GameOver);

                    return;
                }

                SendLobbyUpdate();
            }
            else
            {
                IGameNetworkMessage playerUpdate = playerManagerService.GetLocalPlayerUpdate();
                if (playerUpdate == null)
                {
                    Plugin.Log.LogWarning("Local player update is null, cannot send to host");
                    return;
                }
                SendToHost(playerUpdate, DeliveryMethod.Unreliable);
            }
        }

        public void UpdateEnemies()
        {
            if (isGameOver)
            {
                return;
            }

            if (!EnsureIsHost())
            {
                return;
            }

            SendEnemiesUpdate();
        }

        public void UpdateProjectiles()
        {
            if (isGameOver)
            {
                return;
            }

            if (!EnsureIsHost())
            {
                return;
            }

            SendProjectilesUpdate();
        }

        public void UpdateTumbleWeeds()
        {
            if (isGameOver)
            {
                return;
            }

            if (!EnsureIsHost())
            {
                return;
            }

            SendTumbleWeedsUpdate();
        }

        private void UpdateLocalPlayer()
        {
            var player = playerManagerService.GetLocalPlayer();
            var localPlayer = playerManagerService.GetLocalPlayerUpdate();

            if (player == null)
            {
                Plugin.Log.LogWarning("Local player is null");
                return;
            }

            if (localPlayer == null)
            {
                Plugin.Log.LogWarning("Local player update is null");
                return;
            }

            player.Position = Quantizer.Quantize(localPlayer.Position.ToUnityVector3());
            player.MovementState = localPlayer.MovementState;
            player.AnimatorState = localPlayer.AnimatorState;
            player.Hp = localPlayer.Hp;
            player.MaxHp = localPlayer.MaxHp;
            player.Shield = localPlayer.Shield;
            player.MaxShield = localPlayer.MaxShield;
            //player.Xp = localPlayer.Xp;
            player.Inventory = localPlayer.Inventory;
            player.Name = localPlayer.Name;

            playerManagerService.UpdatePlayer(player);
        }

        private void SendLobbyUpdate()
        {
            // Labelled by hand, not by GetType().Name: this send and SendEnemiesUpdate both use the
            // LobbyUpdates type, so the type name merged the player stream and the enemy stream into
            // one bucket. That bucket has been reported as 65-90% of host traffic and named as where
            // bandwidth work belongs — a conclusion the counter could not actually support, because
            // the two scale with completely different things (player count vs. enemies alive). A
            // level-159 capture peaked at 199.50 KB/s under the merged name with no way to say which.
            //
            // Chunked despite being the smallest of the four streams: a Player carries a name string
            // and a full inventory snapshot, so at six players late in a run this can cross the cap
            // even though it rarely does at two.
            SendStreamUpdate(
                [.. playerManagerService.GetAllPlayers()],
                (chunk, _) => new LobbyUpdates { Players = chunk },
                "LobbyUpdates(players)");
        }

        private void SendEnemiesUpdate()
        {
            List<EnemyModel> enemies = [.. enemyManagerService.GetAllEnemiesDeltaAndUpdate()];
            List<BossOrbModel> bossFinalOrb = [.. finalBossOrbManagerService.GetAllOrbs()];

            if (enemies.Count == 0 && bossFinalOrb.Count == 0)
            {
                return;
            }

            // The other half of the LobbyUpdates bucket — see the note in SendLobbyUpdate. Enemies
            // and boss orbs stay together here deliberately: they are one delta stream sharing one
            // send, so splitting them further would report a size neither of them has on the wire.
            //
            // This is the stream the chunking is really aimed at. It is a delta, so a big tick means
            // "many enemies moved" — and a moving enemy is re-sent next tick regardless, which is
            // exactly the state that does not need a reliable channel. The old size promotion paid
            // acks and retransmits to redeliver positions that were already stale on arrival.
            //
            // The orbs ride on chunk 0 only: there are a handful of them, they are not what makes a
            // tick oversized, and repeating them per chunk would be duplicated payload. Chunk 0
            // exists whether or not the tick splits, so the single-message path is unchanged.
            if (enemies.Count == 0)
            {
                SendStreamUpdate(
                    bossFinalOrb,
                    (chunk, _) => new LobbyUpdates { BossOrbs = chunk },
                    "LobbyUpdates(enemies)");
                return;
            }

            SendStreamUpdate(
                enemies,
                (chunk, chunkIndex) => new LobbyUpdates
                {
                    Enemies = chunk,
                    BossOrbs = chunkIndex == 0 ? bossFinalOrb : [],
                },
                "LobbyUpdates(enemies)");
        }

        private void SendProjectilesUpdate()
        {
            // The worst offender by size in the level-159 capture: 4761 B/send at 20 Hz, i.e. four
            // fragments of a reliable-ordered message per tick during a swarm.
            SendStreamUpdate(
                [.. projectileManagerService.GetAllProjectilesDeltaAndUpdate()],
                (chunk, _) => new ProjectilesUpdate { Projectiles = chunk },
                nameof(ProjectilesUpdate));
        }

        private void SendTumbleWeedsUpdate()
        {
            SendStreamUpdate(
                [.. spawnedObjectManagerService.GetAllTumbleWeedsDeltaAndUpdate()],
                // TumbleWeeds is an array on the wire, so the chunk is copied. Leaving the field a
                // List would be a wire change; this stream is Desert-only at 20 Hz and rarely
                // splits, so the copy is the cheaper of the two.
                (chunk, _) => new TumbleWeedsUpdate { TumbleWeeds = [.. chunk] },
                nameof(TumbleWeedsUpdate));
        }

        /// <summary>
        /// Sends one tick of a per-entity stream, splitting an oversized tick into several sub-MTU
        /// <see cref="DeliveryMethod.Unreliable"/> datagrams instead of promoting the whole tick to
        /// <see cref="DeliveryMethod.ReliableOrdered"/>.
        ///
        /// <para><b>What this replaces.</b> Each of these senders used to do
        /// <c>if (serialized.Length >= MAX_PACKET_SIZE_BYTES) deliveryMethod = ReliableOrdered</c>.
        /// That is not wrong — LiteNetLib fragments reliable channels only, so an oversized
        /// unreliable send silently fails and promoting is the only way to get it there in one
        /// message. It is just the expensive answer to the wrong question. The tick does not need to
        /// arrive in one message; it needs to arrive.</para>
        ///
        /// <para><b>Why it matters at scale.</b> A level-159 capture put the merged enemy/player
        /// bucket at 2100 B/send at 97.3/s and <c>ProjectilesUpdate</c> at 4761 B/send at 20 Hz —
        /// so the hot streams were spending most of their time on a multi-fragment reliable channel,
        /// paying acks and retransmits, and head-of-line blocking every later entity update behind
        /// any one stalled fragment. Splitting by a byte budget keeps every datagram on the
        /// unreliable path, where a loss costs one entity one tick and the next tick corrects it.</para>
        ///
        /// <para><b>No wire change.</b> <c>OnLobbyUpdate</c> and the other receive handlers iterate
        /// per item and nothing depends on a list being complete, so N messages deserialize exactly
        /// as one did. Same types, same union tags, same handlers — a peer on the old build cannot
        /// tell the difference.</para>
        ///
        /// <para><b>The fallback still promotes.</b> The chunk count is derived from the whole
        /// tick's average bytes-per-item, so an unusually large single item (a player with a big
        /// inventory) can still overflow its chunk. That chunk promotes exactly as before, which
        /// makes the worst case no worse than today rather than a silent dropped send.</para>
        ///
        /// <para>Chunk 0 is passed to <paramref name="buildChunk"/> as index 0 whether or not the
        /// tick was split, so a caller can attach ride-along state (the enemy stream's boss orbs) to
        /// the first message only and have it behave identically in both paths.</para>
        ///
        /// <para><b>Reading the counters after this lands:</b> a split tick records once per chunk,
        /// so sends/s rises and B/send falls while KB/s stays put. That is the change working, not a
        /// regression.</para>
        /// </summary>
        private void SendStreamUpdate<TItem>(
            List<TItem> items,
            Func<List<TItem>, int, IGameNetworkMessage> buildChunk,
            string label)
        {
            if (items.Count == 0)
            {
                return;
            }

            // Serialized through IGameNetworkMessage explicitly, never the concrete type: the union
            // tag comes from the interface, and serializing as the concrete message would omit it
            // and put an undecodable payload on the wire.
            byte[] serialized = MemoryPackSerializer.Serialize<IGameNetworkMessage>(buildChunk(items, 0));

            // The common case, and the one that must stay cheap: the whole tick fits, so it goes out
            // exactly as it did before — one serialize, one send, no extra allocation.
            if (serialized.Length < MAX_PACKET_SIZE_BYTES)
            {
                SendToAllClients(serialized, DeliveryMethod.Unreliable, label);
                return;
            }

            // Budget below the cap rather than at it: every chunk repeats the message envelope (the
            // union tag and the empty-collection headers for the fields this stream does not set),
            // so sizing purely on the whole tick's bytes-per-item would land the last chunk just
            // over. Undershooting costs one extra datagram; overshooting costs a promotion.
            var bytesPerItem = Math.Max(1, serialized.Length / items.Count);
            var itemsPerChunk = Math.Max(1, (MAX_PACKET_SIZE_BYTES - CHUNK_ENVELOPE_HEADROOM_BYTES) / bytesPerItem);

            for (int start = 0, chunkIndex = 0; start < items.Count; start += itemsPerChunk, chunkIndex++)
            {
                var count = Math.Min(itemsPerChunk, items.Count - start);
                var chunk = items.GetRange(start, count);

                byte[] chunkBytes = MemoryPackSerializer.Serialize<IGameNetworkMessage>(buildChunk(chunk, chunkIndex));

                // Only reachable when the average underestimated this chunk, or when one item is
                // bigger than the whole budget. Reliable is the sole way such a message arrives.
                var deliveryMethod = chunkBytes.Length >= MAX_PACKET_SIZE_BYTES
                    ? DeliveryMethod.ReliableOrdered
                    : DeliveryMethod.Unreliable;

                SendToAllClients(chunkBytes, deliveryMethod, label);
            }
        }

        public void SendToAllClients<T>(T data, DeliveryMethod deliveryMethod) where T : IGameNetworkMessage
        {
            if (!EnsureIsHost())
            {
                return;
            }

            var msgBytes = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);

            if (usesRelay.Any())
            {
                RelayEnvelope relayEnvelope = new()
                {
                    Payload = msgBytes,
                };
                var relayMsgBytes = MemoryPackSerializer.Serialize(relayEnvelope);
                lock (relayPeerLock)
                {
                    relayPeer?.Send(relayMsgBytes, deliveryMethod);
                }
            }

            if (gamePeers.Count == 0)
            {
                //Plugin.Log.LogWarning("No other clients connected");
                return;
            }

            NetDataWriter writer = new();
            writer.Put(msgBytes);

            foreach (var (_, peer) in gamePeers)
            {
                peer.Send(writer, deliveryMethod);
            }

            // Counted after the fan-out, not before: a broadcast costs its payload once per peer.
            BandwidthDiagnostics.Record(data.GetType().Name, msgBytes.Length, gamePeers.Count + (usesRelay.Any() ? 1 : 0));
        }

        public void SendToHost<T>(T data, DeliveryMethod? overrideDeliveryMethod = null) where T : IGameNetworkMessage
        {
            if (!EnsureIsClient())
            {
                return;
            }

            var msgBytes = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);

            var deliveryMethod = overrideDeliveryMethod ?? DeliveryMethod.ReliableSequenced;

            if (msgBytes.Length >= MAX_PACKET_SIZE_BYTES)
            {
                deliveryMethod = DeliveryMethod.ReliableOrdered;
            }

            if (usesRelay.Any())
            {
                RelayEnvelope relayEnvelope = new()
                {
                    Payload = msgBytes,
                };
                var relayMsgBytes = MemoryPackSerializer.Serialize(relayEnvelope);
                lock (relayPeerLock)
                {
                    relayPeer?.Send(relayMsgBytes, DeliveryMethod.ReliableOrdered);
                }

                BandwidthDiagnostics.Record(data.GetType().Name, msgBytes.Length, 1);
                return;
            }

            NetDataWriter writer = new();
            writer.Put(msgBytes);

            if (gamePeers.Count == 0)
            {
                Plugin.Log.LogWarning("Not connected to host");
                return;
            }

            gamePeers[0].Send(writer, deliveryMethod);

            BandwidthDiagnostics.Record(data.GetType().Name, msgBytes.Length, 1);
        }

        public void SendToClient<T>(NetPeer client, T data, uint connectionId) where T : IGameNetworkMessage
        {
            if (!EnsureIsHost())
            {
                return;
            }

            var msgBytes = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);

            if (usesRelay.Contains(connectionId))
            {
                RelayEnvelope relayEnvelope = new()
                {
                    TargetConnectionId = connectionId,
                    HaveTarget = true,
                    Payload = msgBytes,
                };
                var relayMsgBytes = MemoryPackSerializer.Serialize(relayEnvelope);
                lock (relayPeerLock)
                {
                    relayPeer?.Send(relayMsgBytes, DeliveryMethod.ReliableOrdered);
                }

                BandwidthDiagnostics.Record(data.GetType().Name, msgBytes.Length, 1);
                return;
            }

            NetDataWriter writer = new NetDataWriter();
            writer.Put(msgBytes);
            client.Send(writer, DeliveryMethod.ReliableOrdered);

            BandwidthDiagnostics.Record(data.GetType().Name, msgBytes.Length, 1);
        }

        private bool EnsureIsHost()
        {
            if (isHost == null)
            {
                Plugin.Log.LogWarning("IsHost not set yet");
                return false;
            }
            if (!isHost.Value)
            {
                Plugin.Log.LogWarning("Only host can perform this action");
                return false;
            }
            return true;
        }

        private bool EnsureIsClient()
        {
            if (isHost == null)
            {
                Plugin.Log.LogWarning("IsHost not set yet");
                return false;
            }
            if (isHost.Value)
            {
                Plugin.Log.LogWarning("Only client can perform this action");
                return false;
            }
            return true;
        }

        public bool? IsHost()
        {
            return isHost;
        }

        public bool HasAllPeersConnected()
        {
            return hasAllPeersConnected;
        }

        public int GetLatency(uint connectionId)
        {
            if (isHost.HasValue && isHost.Value)
            {
                var peerIntro = gamePeersIntroduced.FirstOrDefault(p => p.Value.ConnectionId == connectionId);
                if (peerIntro.Value != null)
                {
                    return peerIntro.Value.Latency;
                }

                var peerIntroByRelay = gamePeersIntroducedByRelay.FirstOrDefault(p => p.Value.ConnectionId == connectionId);
                if (peerIntroByRelay.Value != null)
                {
                    return peerIntroByRelay.Value.Latency;
                }
            }
            else
            {
                var peerIntro = gamePeersIntroduced.FirstOrDefault(p => p.Value.ConnectionId == connectionId);
                if (peerIntro.Value != null)
                {
                    return peerIntro.Value.Latency;
                }

                var peerIntroByRelay = gamePeersIntroducedByRelay.FirstOrDefault(p => p.Value.ConnectionId == connectionId);
                if (peerIntroByRelay.Value != null)
                {
                    return peerIntroByRelay.Value.Latency;
                }
            }

            return -1;
        }

        public void SendToAllClientsExcept<T>(int netPlayerId, uint sender, T data) where T : IGameNetworkMessage
        {
            if (!EnsureIsHost())
            {
                return;
            }

            var msgBytes = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);

            if (usesRelay.Any())
            {
                bool found = false;
                uint toExcept = 0;
                if (gamePeersIntroducedByRelay.ContainsKey(sender))
                {
                    toExcept = gamePeersIntroducedByRelay[sender].ConnectionId;
                    found = true;
                }

                if (toExcept == 0)
                {
                    var normalPeer = gamePeersIntroducedByRelay.FirstOrDefault(p => p.Value.ConnectionId == sender).Value;

                    if (normalPeer != null)
                    {
                        toExcept = normalPeer.ConnectionId;
                        found = true;
                    }
                }


                RelayEnvelope relayEnvelope = new()
                {
                    ToFilters = found ? [toExcept] : [],
                    Payload = msgBytes,
                };

                var relayMsgBytes = MemoryPackSerializer.Serialize(relayEnvelope);
                lock (relayPeerLock)
                {
                    relayPeer?.Send(relayMsgBytes, DeliveryMethod.ReliableOrdered);
                }
            }

            if (gamePeers.Count == 0)
            {
                //Plugin.Log.LogWarning("No clients connected");
                return;
            }

            NetDataWriter writer = new NetDataWriter();
            writer.Put(msgBytes);

            var sent = 0;
            var filteredPeers = gamePeers.Where(p => p.Value.Id != netPlayerId);
            foreach (var (_, peer) in filteredPeers)
            {
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
                sent++;
            }

            // Counted from the loop rather than gamePeers.Count, because this is the exclusion path:
            // netPlayerId is a LiteNetLib NetPeer.Id and `sender` is a game connection id, and the
            // migration plan calls transposing them an easy and silent mistake. Counting what the
            // loop actually sent cannot inherit that confusion.
            BandwidthDiagnostics.Record(data.GetType().Name, msgBytes.Length, sent + (usesRelay.Any() ? 1 : 0));
        }

        /// <summary>
        /// <paramref name="messageTypeName"/> exists only for the bandwidth counters: this overload
        /// takes bytes, so the message type is gone by the time it arrives, and the first baseline
        /// reported 95% of all host traffic under a single "(pre-serialized)" bucket — a split that
        /// could not split the thing that mattered. Every caller is inside this class and already
        /// holds the typed message, so the name is passed down rather than inferred. Optional so a
        /// future caller cannot fail to compile, but it should always be supplied.
        /// </summary>
        public void SendToAllClients(byte[] data, DeliveryMethod deliveryMethod, string messageTypeName = null)
        {
            if (!EnsureIsHost())
            {
                return;
            }

            if (usesRelay.Any())
            {
                RelayEnvelope relayEnvelope = new()
                {
                    Payload = data,
                };
                var relayMsgBytes = MemoryPackSerializer.Serialize(relayEnvelope);
                lock (relayPeerLock)
                {
                    relayPeer?.Send(relayMsgBytes, deliveryMethod);
                }
            }

            if (gamePeers.Count == 0)
            {
                //Plugin.Log.LogWarning("No clients connected");
                return;
            }

            // Bucketed as "(pre-serialized)" because this overload takes bytes, so the message type
            // is already gone by the time it gets here. Counted anyway — leaving it out would make
            // the reported total quietly lower than the real one, which is worse for a baseline than
            // an unnamed bucket.
            BandwidthDiagnostics.Record(messageTypeName ?? "(pre-serialized)", data.Length, gamePeers.Count + (usesRelay.Any() ? 1 : 0));

            NetDataWriter writer = new NetDataWriter();
            writer.Put(data);

            try
            {
                foreach (var (_, peer) in gamePeers)
                {
                    peer.Send(writer, deliveryMethod);
                }
            }
            catch (LiteNetLib.TooBigPacketException ex)
            {
                Plugin.Log.LogError($"Failed to send message: {ex.Message}");
            }
        }

        public void UpdateMode(bool isHost)
        {
            this.isHost = isHost;
        }

        public bool IsHandlingConnection()
        {
            return isHandlingConnection;
        }

        public void CancelAnyNatIntroduction()
        {
            pollingCancelationTokenSource?.Cancel();
        }

        public bool HasHandledHost()
        {
            return hasHandledHost;
        }

        public void RemovePeer(uint clientConnectionId)
        {
            var peerIntro = gamePeersIntroduced.FirstOrDefault(p => p.Value.ConnectionId == clientConnectionId);
            if (peerIntro.Value != null)
            {
                gamePeersIntroduced.Remove(peerIntro.Key, out _);
            }
            var peerIntroByRelay = gamePeersIntroducedByRelay.FirstOrDefault(p => p.Value.ConnectionId == clientConnectionId);
            if (peerIntroByRelay.Value != null)
            {
                gamePeersIntroducedByRelay.Remove(peerIntroByRelay.Key, out _);
            }
        }

        public void ResetHandledHost()
        {
            hasHandledHost = false;
        }
    }
}

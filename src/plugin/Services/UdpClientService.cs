using OmniBonk.Configuration;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Managers;
using BepInEx.Logging;
using LiteNetLib;
using LiteNetLib.Utils;
using OmniBonk.Common.Messages;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Common.Models;
using OmniBonk.Extensions;
using OmniBonk.Helpers;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OmniBonk.Services
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

    /// <summary>
    /// LiteNetLib session: NAT introduction, relay fallback, sending. Gameplay code should depend on
    /// <see cref="INetTransport"/>, not this.
    /// </summary>
    public interface IUdpClientService : INetTransport
    {
        public bool Initialize();

        public void Poll();

        public Task<bool> HandleMatch(MatchInfo matchInfo, uint selfConnectionId, string rdvServerHost, uint rdvServerPort, bool enabledSharedExperience);

        public void Reset();

        public int GetNetPeerCount();
        public void UpdateMode(bool isHost);
        public void CancelAnyNatIntroduction();
        public bool HasHandledHost();
        public void ResetHandledHost();
        public void RemovePeer(uint clientConnectionId);
    }
    internal class UdpClientService(
            IPlayerManagerService playerManagerService,
            ManualLogSource logger) : IUdpClientService
    {
        private const int MAX_PACKET_SIZE_BYTES = Tunables.MAX_PACKET_SIZE_BYTES;

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

        private string rdvServerHost;
        private int rdvServerPort;
        private readonly HashSet<uint> usesRelay = [];
        private NetPeer relayPeer = null;
        private readonly object relayPeerLock = new();

        private ConcurrentDictionary<string, bool> tokens = new();
        private bool hasTriedForceRelay = false;

        /// <summary>Resolved lazily; see <see cref="HandleMessage"/>.</summary>
        private INetMessageRouter netMessageRouter;

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
                        // P1-3: this key is never read (cross-connect skips ConnectionRequestEvent),
                        // so no version gate can go here.
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
                // P1-3: not run on the NAT-punch path. Do not put a version gate here.
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

                    // Explicit: the default ReliableSequenced is wrong for a one-shot handshake.
                    SendToHost(introduced, NetDelivery.ReliableOrdered);
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
                    ("OmniBonk", "ClientDisconnected"),
                    ("OmniBonk", "ClientDisconnected_Description"),
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
                    ("OmniBonk", "HostDisconnected"),
                    ("OmniBonk", "HostDisconnected_Description"),
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
                        ("OmniBonk", "AllPlayerDisconnected"),
                        ("OmniBonk", "AllPlayerDisconnected_Description"),
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
                        SendToAllClients(disconnectedPlayer, NetDelivery.ReliableOrdered);
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

        /// <summary>
        /// Handles peer-scoped messages (keyed on <c>NetPeer.Id</c>) here; everything else goes to
        /// the transport-neutral <see cref="INetMessageRouter"/>.
        /// </summary>
        private void HandleMessage(IGameNetworkMessage message, int netPeerId)
        {
            if (TryHandlePeerScopedMessage(message, netPeerId))
            {
                return;
            }

            // Lazy: constructor injection would be a DI cycle (router needs INetTransport, which is us).
            netMessageRouter ??= Plugin.Services.GetService<INetMessageRouter>();

            if (!netMessageRouter.Route(message, isHost.Value))
            {
                Plugin.Log.LogWarning($"Unknown message type received. message={message}");
            }
        }

        /// <summary>Connection-level messages. Returns true when handled.</summary>
        private bool TryHandlePeerScopedMessage(IGameNetworkMessage message, int netPeerId)
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

                            return true;
                        }

                        if (!gamePeersIntroduced.TryAdd(netPeerId, new PeerIntroduction(introduced.Name, introduced.ConnectionId, introduced.IsHost)))
                        {
                            Plugin.Log.LogWarning($"Duplicate introduction from host={netPeerId}, ignoring.");
                            return true;
                        }

                        var player = playerManagerService.GetPlayer(introduced.ConnectionId);
                        if (player != null)
                        {
                            player.Name = introduced.Name;
                            playerManagerService.UpdatePlayer(player);
                        }

                        return true;

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

                                return true;
                            }
                        }

                        var disconnectedPeer = gamePeersIntroduced.FirstOrDefault(p => p.Value.ConnectionId == playerDisconnected.ConnectionId);

                        if (disconnectedPeer.Value == null) //Disonnected peer not a host
                        {
                            EventManager.OnPlayerDisconnected(playerDisconnected);
                            return true;
                        }

                        //Host disconnected
                        var peer = gamePeers.FirstOrDefault(p => p.Value.Id == disconnectedPeer.Key).Value;
                        HandleDisconnectedPeer(peer);

                        return true;

                    default:
                        return false;
                }
            }

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
                            Plugin.StartNotification(("OmniBonk", "FriendliesClientJoinSuccess"), ("OmniBonk", "FriendliesClientJoinSuccessDesc"), [introduced.Name]);
                        }

                        return true;
                    }
                    else
                    {
                        if (!gamePeersIntroduced.TryAdd(netPeerId, new PeerIntroduction(introduced.Name, introduced.ConnectionId, introduced.IsHost)))
                        {
                            Plugin.Log.LogWarning($"Duplicate introduction from netPlayerId={netPeerId}, ignoring.");
                            return true;
                        }
                    }

                    if (Plugin.Instance.Mode.Mode == Common.Models.NetworkModeType.Friendlies)
                    {
                        Plugin.StartNotification(("OmniBonk", "FriendliesClientJoinSuccess"), ("OmniBonk", "FriendliesClientJoinSuccessDesc"), [introduced.Name]);
                    }

                    IGameNetworkMessage introducedResponse = new Introduced
                    {
                        ConnectionId = selfConnectionId.Value,
                        Name = Configuration.ModConfig.PlayerName.Value,
                        IsHost = isHost.Value
                    };

                    {
                        SendToClient(introduced.ConnectionId, introducedResponse);

                        var playerModel = playerManagerService.GetPlayer(introduced.ConnectionId);
                        if (playerModel != null)
                        {
                            playerModel.Name = introduced.Name;
                            playerManagerService.UpdatePlayer(playerModel);
                        }
                    }

                    return true;

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
                        return true;
                    }

                    toUpdate.Character = selectedCharacter.Character;
                    toUpdate.Skin = selectedCharacter.Skin;
                    playerManagerService.UpdatePlayer(toUpdate);

                    SendToAllClientsExcept(selectedCharacter.ConnectionId, selectedCharacter);

                    if (AreAllPeersReady() && playerManagerService.HasSelectedCharacter() && Plugin.Instance.IS_HOST_READY)
                    {
                        var runConfig = WindowManager.activeWindow.GetComponentInChildren<MapSelectionUi>().runConfig;
                        MapController.StartNewMap(runConfig);
                    }

                    return true;

                case PlayerDisconnected playerDisconnected: //Host only receives this message by the rdv server, normally its handled in LiteNet's PeerDisconnectedEvent
                    if (!usesRelay.Remove(playerDisconnected.ConnectionId))
                    {
                        logger.LogInfo($"PlayerDisconnected: ConnectionId {playerDisconnected.ConnectionId} was not using relay.");
                        return true;
                    }

                    EventManager.OnPlayerDisconnected(playerDisconnected);
                    SendToAllClients(playerDisconnected, NetDelivery.ReliableOrdered);

                    return true;

                default:
                    return false;
            }
        }

        public void Reset()
        {
            hasHandledHost = false;
            selfConnectionId = null;
            isHost = null;
            expectedPeerCount = 0;
            hasAllPeersConnected = false;
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

            // netManager is null on Steam sessions (never started).
            netManager?.DisconnectAll();
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

        private readonly HashSet<string> misroutedTypesReported = new();

        /// <summary>
        /// True (warning once per type) when sending here under the Steam transport, where it would
        /// silently go nowhere. The caller should depend on INetTransport instead.
        /// </summary>
        private bool IsMisroutedUnderSteam(string messageTypeName)
        {
            if (!ModConfig.UseSteamTransport.Value)
            {
                return false;
            }

            bool firstTime;
            lock (misroutedTypesReported)
            {
                firstTime = misroutedTypesReported.Add(messageTypeName ?? "unknown");
            }

            if (firstTime)
            {
                Plugin.Log.LogWarning(
                    $"[transport] {messageTypeName} was sent through IUdpClientService while the Steam " +
                    "transport is active, so it went nowhere. Whatever sent it should depend on " +
                    "INetTransport instead. Reported once per message type.");
            }

            return true;
        }

        public void SendToAllClients<T>(T data, NetDelivery delivery) where T : IGameNetworkMessage
        {
            if (IsMisroutedUnderSteam(typeof(T).Name))
            {
                return;
            }

            var deliveryMethod = ToLiteNetLib(delivery);

            if (!EnsureIsHost(HostGuardMessageName(data)))
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

            BandwidthDiagnostics.Record(data.GetType().Name, msgBytes.Length, gamePeers.Count + (usesRelay.Any() ? 1 : 0));
        }

        public void SendToHost<T>(T data, NetDelivery? delivery = null) where T : IGameNetworkMessage
        {
            if (IsMisroutedUnderSteam(typeof(T).Name))
            {
                return;
            }

            if (!EnsureIsClient())
            {
                return;
            }

            var msgBytes = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);

            var deliveryMethod = delivery.HasValue ? ToLiteNetLib(delivery.Value) : DeliveryMethod.ReliableSequenced;

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

        public void SendToClient<T>(uint connectionId, T data) where T : IGameNetworkMessage
        {
            if (IsMisroutedUnderSteam(typeof(T).Name))
            {
                return;
            }

            if (!EnsureIsHost(HostGuardMessageName(data)))
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

            if (!TryResolvePeer(connectionId, out var client))
            {
                Plugin.Log.LogWarning($"SendToClient: no connected peer for connection id {connectionId}; message dropped.");
                return;
            }

            NetDataWriter writer = new NetDataWriter();
            writer.Put(msgBytes);
            client.Send(writer, DeliveryMethod.ReliableOrdered);

            BandwidthDiagnostics.Record(data.GetType().Name, msgBytes.Length, 1);
        }


        /// <summary>OB-10: name to record a rejected host-only send under.</summary>
        private static string HostGuardMessageName(object data, string messageTypeName = null)
        {
            return !string.IsNullOrEmpty(messageTypeName) ? messageTypeName : data?.GetType().Name;
        }
        /// <summary>OB-10.</summary>
        private bool EnsureIsHost(
            string messageTypeName = null,
            [System.Runtime.CompilerServices.CallerMemberName] string sendMethod = null)
        {
            if (isHost == null)
            {
                HostGuardDiagnostics.Record("udp/is-host-unset", sendMethod, messageTypeName);
                return false;
            }
            if (!isHost.Value)
            {
                HostGuardDiagnostics.Record("udp", sendMethod, messageTypeName);
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

        /// <summary>RTT only; this transport is slated for removal.</summary>
        public string DescribeLink(uint connectionId) => $"rtt {GetLatency(connectionId)} ms";

        /// <summary>P1-1: excludes the originating player (game connection id), not the arrival peer.</summary>
        public void SendToAllClientsExcept<T>(uint excludedConnectionId, T data) where T : IGameNetworkMessage
        {
            if (IsMisroutedUnderSteam(typeof(T).Name))
            {
                return;
            }

            if (!EnsureIsHost(HostGuardMessageName(data)))
            {
                return;
            }

            var msgBytes = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);

            if (usesRelay.Any())
            {
                // Not in the relay map means a direct peer, which the relay never sends to anyway.
                RelayEnvelope relayEnvelope = new()
                {
                    ToFilters = gamePeersIntroducedByRelay.ContainsKey(excludedConnectionId)
                        ? [excludedConnectionId]
                        : [],
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

            var excludedPeerId = ResolvePeerId(excludedConnectionId);

            var sent = 0;
            foreach (var (peerId, peer) in gamePeers)
            {
                if (excludedPeerId.HasValue && peerId == excludedPeerId.Value)
                {
                    continue;
                }

                peer.Send(writer, DeliveryMethod.ReliableOrdered);
                sent++;
            }

            BandwidthDiagnostics.Record(data.GetType().Name, msgBytes.Length, sent + (usesRelay.Any() ? 1 : 0));
        }

        /// <summary>Connection id to LiteNetLib peer id, or null. A scan over six peers cannot go stale.</summary>
        private int? ResolvePeerId(uint connectionId)
        {
            foreach (var (peerId, intro) in gamePeersIntroduced)
            {
                if (intro.ConnectionId == connectionId)
                {
                    return peerId;
                }
            }

            return null;
        }

        private bool TryResolvePeer(uint connectionId, out NetPeer peer)
        {
            var peerId = ResolvePeerId(connectionId);

            if (peerId.HasValue && gamePeers.TryGetValue(peerId.Value, out peer))
            {
                return true;
            }

            peer = null;
            return false;
        }

        private static DeliveryMethod ToLiteNetLib(NetDelivery delivery) => delivery switch
        {
            NetDelivery.Unreliable => DeliveryMethod.Unreliable,
            NetDelivery.ReliableUnordered => DeliveryMethod.ReliableUnordered,
            NetDelivery.ReliableSequenced => DeliveryMethod.ReliableSequenced,
            _ => DeliveryMethod.ReliableOrdered,
        };

        /// <summary><paramref name="messageTypeName"/> labels the bandwidth counters; always supply it.</summary>
        public void SendToAllClients(byte[] data, NetDelivery delivery, string messageTypeName = null)
        {
            if (IsMisroutedUnderSteam(messageTypeName ?? "raw payload"))
            {
                return;
            }

            var deliveryMethod = ToLiteNetLib(delivery);

            if (!EnsureIsHost(HostGuardMessageName(data, messageTypeName)))
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

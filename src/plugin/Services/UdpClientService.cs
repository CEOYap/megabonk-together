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
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// The LiteNetLib session: matchmaking handshake, NAT introduction, relay fallback and the
    /// per-tick send loop. The transport half of this — everything that puts bytes on a wire — is
    /// <see cref="INetTransport"/>, which is what gameplay code depends on; the members declared
    /// here are session lifecycle and are expected to change shape during the Steamworks migration.
    /// </summary>
    public interface IUdpClientService : INetTransport
    {
        public bool Initialize();
        public void Update();

        public void Poll();

        public Task<bool> HandleMatch(MatchInfo matchInfo, uint selfConnectionId, string rdvServerHost, uint rdvServerPort, bool enabledSharedExperience);
        public bool HasAllPeersConnected();

        public void UpdateEnemies();
        public void UpdateProjectiles();
        public void UpdateTumbleWeeds();

        public void Reset();
        public void GameOver();

        public int GetNetPeerCount();
        public bool AreAllPeersReady();
        public int GetCurrentReadyPeersCount();
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
            IReadinessService readinessService,
            ManualLogSource logger) : IUdpClientService
    {
        private const int MAX_PACKET_SIZE_BYTES = 1000;

        // Slack left in each chunk of a split stream tick for the message envelope that every chunk
        // repeats — the MemoryPack union tag plus the empty-collection headers for the fields that
        // stream does not populate. See SendStreamUpdate.
        private const int CHUNK_ENVELOPE_HEADROOM_BYTES = 100;

        // The full Player record goes out every Nth 60 Hz tick — 5 Hz — plus immediately on any
        // IsReady change. See SendPlayerStreams for how this number was chosen.
        private const int FULL_PLAYER_RECORD_EVERY_N_TICKS = 12;
        private int playerRecordTick;
        private readonly Dictionary<uint, bool> lastSentReadiness = [];
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

        /// <summary>
        /// Everything the receive switch used to do that was not about the connection. Resolved
        /// lazily rather than injected — see <see cref="HandleMessage"/>.
        /// </summary>
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

                    // Explicit, and this is the last caller that was not. The implicit default is
                    // ReliableSequenced — a channel that guarantees only the newest packet — which
                    // is the wrong guarantee for a one-shot handshake and has no Steam equivalent
                    // at all. For a message sent exactly once the two channels behave identically
                    // on LiteNetLib, so naming the one that was always meant costs nothing here and
                    // stops Phase 4 having to invent a translation for a semantic nobody wanted.
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
        /// The receive path's front door. Handles the messages whose meaning depends on <b>which
        /// peer they arrived on</b>, and hands everything else to <see cref="INetMessageRouter"/>.
        ///
        /// <para><b>That split is the whole point, and it is not "plumbing versus logic".</b> Eight
        /// cases below read or write the peer introduction maps, the relay peer, or tear a
        /// connection down — none of which has a transport-neutral meaning, and all of which are
        /// keyed on a LiteNetLib <c>NetPeer.Id</c>. Every other message is a function of its own
        /// contents, so it can be applied identically no matter what carried it. Only the second
        /// group could ever be shared with a second transport, and it is all but eight of them.
        /// </para>
        /// </summary>
        private void HandleMessage(IGameNetworkMessage message, int netPeerId)
        {
            if (TryHandlePeerScopedMessage(message, netPeerId))
            {
                return;
            }

            // Resolved once, on first use. Constructor injection would be a cycle: the router needs
            // INetTransport, and INetTransport is this object.
            netMessageRouter ??= Plugin.Services.GetService<INetMessageRouter>();

            if (!netMessageRouter.Route(message, isHost.Value))
            {
                Plugin.Log.LogWarning($"Unknown message type received. message={message}");
            }
        }

        /// <summary>
        /// The messages that cannot leave this class, because they are about the connection rather
        /// than about the game. Returns true when handled.
        /// </summary>
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
                            Plugin.StartNotification(("MegabonkTogether", "FriendliesClientJoinSuccess"), ("MegabonkTogether", "FriendliesClientJoinSuccessDesc"), [introduced.Name]);
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
                        Plugin.StartNotification(("MegabonkTogether", "FriendliesClientJoinSuccess"), ("MegabonkTogether", "FriendliesClientJoinSuccessDesc"), [introduced.Name]);
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
            isGameOver = false;
            // Player-stream pacing is per session: a stale readiness snapshot here would suppress
            // the forced full record that a genuine change is supposed to trigger, if a reused
            // connection id happened to carry the same flag.
            playerRecordTick = 0;
            lastSentReadiness.Clear();
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
                    SendToAllClients(wsMessage, NetDelivery.ReliableOrdered);
                    EventManager.OnGameOver(wsMessage as GameOver);

                    return;
                }

                SendPlayerStreams();
            }
            else
            {
                IGameNetworkMessage playerUpdate = playerManagerService.GetLocalPlayerUpdate();
                if (playerUpdate == null)
                {
                    Plugin.Log.LogWarning("Local player update is null, cannot send to host");
                    return;
                }
                SendToHost(playerUpdate, NetDelivery.Unreliable);
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

        /// <summary>
        /// Splits the host's 60 Hz player broadcast into the half that changes every frame and the
        /// half that does not.
        ///
        /// <para>Two captures measured the old single stream at a flat ~20 KB/s at two players —
        /// 65-90% of all host egress — re-sending names, skins, character ids and inventories sixty
        /// times a second. Measured against the real serializer, splitting it saves 62% at two
        /// players early and 79% at six players late (95.39 -> 19.70 KB/s). The six-player full
        /// record is 1628 B, which is over MAX_PACKET_SIZE_BYTES: today that promotes to
        /// ReliableOrdered every single tick, and after the split only the low-rate record does.</para>
        ///
        /// <para><b>Readiness never waits for the heartbeat.</b> Any change to a player's
        /// <c>IsReady</c> sends a full record immediately. This matters: the lobby-ready barrier has
        /// four open defects where a single lost or late readiness is a permanent hang, and slowing
        /// that field down to save bandwidth would be the wrong trade. The check is one bool compare
        /// per player per tick.</para>
        ///
        /// <para><b>Why 5 Hz for the heartbeat.</b> With readiness handled separately, everything
        /// left in the full record is cosmetic on a sub-second scale: inventory display, health-bar
        /// maxima, and identity (name, skin, character), which is fixed after join. 5 Hz bounds the
        /// worst case at 200 ms — under the threshold where a joining player would notice a default
        /// name — while halving the heartbeat's cost against 10 Hz, which is worth 8 KB/s at six
        /// players. This is the knob to turn if any of that ever proves to want lower latency.</para>
        /// </summary>
        private void SendPlayerStreams()
        {
            SendPlayersStateUpdate();

            playerRecordTick++;

            if (playerRecordTick < FULL_PLAYER_RECORD_EVERY_N_TICKS && !HasReadinessChanged())
            {
                return;
            }

            playerRecordTick = 0;
            SendLobbyUpdate();
        }

        /// <summary>
        /// True when any player's <c>IsReady</c> differs from the last full record we sent, which
        /// forces one out immediately. Deliberately only <c>IsReady</c>: it is the sole field in the
        /// slow half where arriving 100 ms late has a correctness consequence rather than a cosmetic
        /// one.
        /// </summary>
        private bool HasReadinessChanged()
        {
            var changed = false;

            foreach (var player in playerManagerService.GetAllPlayers())
            {
                if (!lastSentReadiness.TryGetValue(player.ConnectionId, out var wasReady) || wasReady != player.IsReady)
                {
                    lastSentReadiness[player.ConnectionId] = player.IsReady;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// The continuous half, every tick. Chunked like the other per-entity streams so a six-player
        /// lobby cannot push it over the MTU and back onto a reliable channel.
        /// </summary>
        private void SendPlayersStateUpdate()
        {
            var states = new List<PlayerState>();

            foreach (var player in playerManagerService.GetAllPlayers())
            {
                states.Add(new PlayerState
                {
                    ConnectionId = player.ConnectionId,
                    Position = player.Position,
                    AnimatorState = player.AnimatorState,
                    MovementState = player.MovementState,
                    Hp = player.Hp,
                    Shield = player.Shield,
                });
            }

            SendStreamUpdate(
                states,
                (chunk, _) => new PlayersStateUpdate { States = chunk },
                nameof(PlayersStateUpdate));
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
        /// <see cref="NetDelivery.Unreliable"/> datagrams instead of promoting the whole tick to
        /// <see cref="NetDelivery.ReliableOrdered"/>.
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
                SendToAllClients(serialized, NetDelivery.Unreliable, label);
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
                    ? NetDelivery.ReliableOrdered
                    : NetDelivery.Unreliable;

                SendToAllClients(chunkBytes, deliveryMethod, label);
            }
        }

        public void SendToAllClients<T>(T data, NetDelivery delivery) where T : IGameNetworkMessage
        {
            var deliveryMethod = ToLiteNetLib(delivery);

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

        public void SendToHost<T>(T data, NetDelivery? delivery = null) where T : IGameNetworkMessage
        {
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

        /// <summary>
        /// Takes a game connection id rather than a <c>NetPeer</c>, per <see cref="INetTransport"/>.
        /// The peer lookup that the single caller used to do inline now happens here, which is the
        /// point of the seam: no caller outside this class names a LiteNetLib handle.
        /// </summary>
        public void SendToClient<T>(uint connectionId, T data) where T : IGameNetworkMessage
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

            if (!TryResolvePeer(connectionId, out var client))
            {
                // Previously impossible to reach: the one caller resolved the peer itself and
                // skipped the send when it came back null, so a miss was silent. Now that the
                // lookup lives here it can say so.
                Plugin.Log.LogWarning($"SendToClient: no connected peer for connection id {connectionId}; message dropped.");
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

        /// <summary>
        /// One id, one space — see <see cref="INetTransport.SendToAllClientsExcept"/>. The two-id
        /// signature this replaces took a LiteNetLib <c>NetPeer.Id</c> for the direct path and a
        /// game connection id for the relay path.
        ///
        /// <para><b>This is the one deliberate behaviour change in Phase 1.</b> The old direct path
        /// excluded <i>the peer the packet arrived on</i>; this excludes <i>the player that
        /// originated it</i>. In the star topology the mod actually runs, the host receives a
        /// client's message directly from that client and the two coincide, which is why the old
        /// code worked. They are not the same concept, though, and the originator is the one that
        /// must not receive its own delta back — that is what the exclusion is for (P1-1). Where
        /// they ever diverge, the new behaviour is the correct one.</para>
        ///
        /// <para>Both defects the Phase 0 trace found are collapsed here rather than carried across
        /// the seam, as the migration plan requires: the old relay branch looked <c>sender</c> up in
        /// <c>gamePeersIntroducedByRelay</c> and read back <c>.ConnectionId</c> — which equals the
        /// key by construction, so it was an identity function — and then re-scanned the same
        /// dictionary on that same field whenever the result was <c>0</c>, a fallback that could
        /// never produce a different answer and that existed only because <c>0</c> was doing double
        /// duty as "not found" and as a legal connection id.</para>
        /// </summary>
        public void SendToAllClientsExcept<T>(uint excludedConnectionId, T data) where T : IGameNetworkMessage
        {
            if (!EnsureIsHost())
            {
                return;
            }

            var msgBytes = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);

            if (usesRelay.Any())
            {
                // Membership, not a lookup: the value's ConnectionId is the key. A relayed peer that
                // is not in the map is a direct peer, and direct peers are not in the relay
                // session's client list, so an empty filter cannot echo to them.
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

            // Resolved once, outside the loop. A miss means the excluded player is not a direct peer
            // — relayed, or already gone — in which case nothing here needs excluding.
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

            // Counted from the loop rather than gamePeers.Count, because this is the exclusion path
            // and the count must reflect what was actually sent.
            BandwidthDiagnostics.Record(data.GetType().Name, msgBytes.Length, sent + (usesRelay.Any() ? 1 : 0));
        }

        /// <summary>
        /// Game connection id → LiteNetLib peer id, the mapping the seam moved inside the transport.
        /// Null when the id is not a currently-connected direct peer.
        ///
        /// <para>A linear scan over at most six peers, on paths that already allocate and serialize.
        /// A maintained reverse map would be faster and would be a second thing to keep in sync
        /// across connect, disconnect and relay promotion — the scan cannot go stale.</para>
        /// </summary>
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

        /// <summary>Game connection id → connected <c>NetPeer</c>. See <see cref="ResolvePeerId"/>.</summary>
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

        /// <summary>
        /// The single place the mod's delivery vocabulary meets LiteNetLib's. Phase 4 adds the
        /// Steam equivalent beside it rather than editing any call site.
        /// </summary>
        private static DeliveryMethod ToLiteNetLib(NetDelivery delivery) => delivery switch
        {
            NetDelivery.Unreliable => DeliveryMethod.Unreliable,
            NetDelivery.ReliableUnordered => DeliveryMethod.ReliableUnordered,
            NetDelivery.ReliableSequenced => DeliveryMethod.ReliableSequenced,
            _ => DeliveryMethod.ReliableOrdered,
        };

        /// <summary>
        /// <paramref name="messageTypeName"/> exists only for the bandwidth counters: this overload
        /// takes bytes, so the message type is gone by the time it arrives, and the first baseline
        /// reported 95% of all host traffic under a single "(pre-serialized)" bucket — a split that
        /// could not split the thing that mattered. Every caller is inside this class and already
        /// holds the typed message, so the name is passed down rather than inferred. Optional so a
        /// future caller cannot fail to compile, but it should always be supplied.
        /// </summary>
        public void SendToAllClients(byte[] data, NetDelivery delivery, string messageTypeName = null)
        {
            var deliveryMethod = ToLiteNetLib(delivery);

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

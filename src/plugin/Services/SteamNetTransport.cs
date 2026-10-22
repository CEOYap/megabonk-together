using OmniBonk.Configuration;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Managers;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OmniBonk.Common.Messages;
using OmniBonk.Common.Messages.GameNetworkMessages;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OmniBonk.Services
{
    /// <summary>
    /// <c>ISteamNetworkingSockets</c> behind <see cref="INetTransport"/>, deliberately parallel to the LiteNetLib transport.
    /// <para>IL2CPP: <c>GetConnectionRealTimeStatus</c>/<c>GetConnectionInfo</c> interop structs have
    /// <c>Il2CppStructArray</c> where native is inline and crash; see docs/steamworks/05-interop-struct-shapes.md.
    /// Status callbacks come through the game's dispatcher on the main thread; never poll <c>GetAPICallResult</c>.</para>
    /// </summary>
    internal class SteamNetTransport(
        ISteamService steamService,
        ISteamLobbyService steamLobbyService,
        IPlayerManagerService playerManagerService) : ISteamNetTransport
    {
        /// <summary>Resolved lazily; injecting it would be a DI cycle.</summary>
        private INetMessageRouter netMessageRouter;

        /// <summary>Application-level port; only has to match between host and client.</summary>
        private const int VirtualPort = 0;

        private const int ReceiveBatchSize = 256;

        /// <summary>Caps drain loops per frame so a flooding peer cannot stall the frame.</summary>
        private const int MaxDrainIterationsPerFrame = 4;

        /// <summary>Pinned once; larger payloads fall back to a one-off pin.</summary>
        private const int SendScratchBytes = 64 * 1024;

        private const int SendFailureLogIntervalMs = 1000;

        /// <summary>
        /// Largest single unreliable datagram; above it Steam fragments unreliably and one lost
        /// fragment drops the message, so larger sends are promoted to reliable. A backstop only:
        /// all current unreliable sends are well below it. Intentionally 200 B looser than LiteNetLib's.
        /// </summary>
        private const int MaxUnreliableBytes = Tunables.MAX_UNRELIABLE_BYTES;

        private const int PromotionLogIntervalMs = 10000;

        private bool? isHost;
        private SteamNetTransportState state = SteamNetTransportState.Idle;
        private string statusDetail = string.Empty;

        private HSteamListenSocket listenSocket = HSteamListenSocket.Invalid;
        private HSteamNetPollGroup pollGroup = HSteamNetPollGroup.Invalid;

        /// <summary>The client's single connection to the host. Invalid on a host.</summary>
        private HSteamNetConnection hostConnection = HSteamNetConnection.Invalid;

        /// <summary>Must stay referenced while registered, or the IL2CPP-side callback crashes later.</summary>
        private SteamCallback statusCallback;

        /// <summary>Peer handle to SteamID, introduced or not.</summary>
        private readonly Dictionary<uint, ulong> peerSteamIds = [];

        private readonly Dictionary<uint, uint> peerHandlesByConnectionId = [];

        /// <summary>Peers that have introduced themselves (connected is not enough).</summary>
        private readonly Dictionary<uint, PeerIntroduction> peerIntroductions = [];

        /// <summary>Derived from our SteamID; see <see cref="Common.SteamConnectionId"/>.</summary>
        private uint selfConnectionId;

        private Il2CppStructArray<IntPtr> receiveBuffer;
        private Il2CppStructArray<SteamNetworkingConfigValue_t> noOptions;

        private byte[] sendScratch;
        private GCHandle sendScratchPin;

        private long nextSendFailureLogTick;
        private long nextNotRunningLogTick;
        private int suppressedSendFailures;

        private long nextPromotionLogTick;
        private int suppressedPromotions;

        /// <summary>Latched after a Steam call throws; retrying across the IL2CPP boundary risks a crash.</summary>
        private bool disabled;

        public SteamNetTransportState State => state;

        public event Action<uint, ulong> PeerConnected;
        public event Action<uint, string> PeerDisconnected;

        public bool? IsHost() => isHost;

        // ---------------------------------------------------------------- lifecycle

        public bool StartHost()
        {
            if (state == SteamNetTransportState.Running && isHost == true)
            {
                return true;
            }

            if (!PrepareToConnect())
            {
                return false;
            }

            isHost = true;

            try
            {
                // Zero-length, not null: IL2CPP reads the data pointer without a null check.
                listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, noOptions);
                if (listenSocket == HSteamListenSocket.Invalid)
                {
                    return Fail("Steam refused to open a listen socket.");
                }

                pollGroup = SteamNetworkingSockets.CreatePollGroup();
                if (pollGroup == HSteamNetPollGroup.Invalid)
                {
                    return Fail("Steam refused to create a poll group.");
                }
            }
            catch (Exception ex)
            {
                return Disable("CreateListenSocketP2P/CreatePollGroup", ex);
            }

            state = SteamNetTransportState.Running;

            SeedLocalPlayer(isLocalHost: true);

            Plugin.Log.LogInfo($"[steam-net] Listening on virtual port {VirtualPort}.");
            return true;
        }

        public bool ConnectToHost(ulong hostSteamId)
        {
            if (hostSteamId == 0UL)
            {
                return Fail("No host SteamID to connect to.");
            }

            if (hostSteamId == steamService.LocalSteamId)
            {
                return Fail("Cannot connect to yourself - Steam peer-to-peer has no loopback.");
            }

            if (!PrepareToConnect())
            {
                return false;
            }

            isHost = false;

            if (!SteamNetLayout.TryMakeSteamIdentity(hostSteamId, out var identity))
            {
                return Fail("The Steam identity for the host did not round-trip; see the error above.");
            }

            try
            {
                hostConnection = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, noOptions);
            }
            catch (Exception ex)
            {
                return Disable("ConnectP2P", ex);
            }

            if (hostConnection == HSteamNetConnection.Invalid)
            {
                return Fail($"Steam refused to start a connection to {hostSteamId}.");
            }

            state = SteamNetTransportState.Starting;
            SeedLocalPlayer(isLocalHost: false);

            Plugin.Log.LogInfo($"[steam-net] Connecting to host {hostSteamId}.");
            return true;
        }

        /// <summary>
        /// Shared start gate. Registers the status callback before any socket call, or an early
        /// <c>Connecting</c> is never accepted.
        /// </summary>
        private bool PrepareToConnect()
        {
            if (disabled)
            {
                return false;
            }

            if (!steamService.IsAvailable)
            {
                return Fail("Steam is not initialised. Launch the game through Steam.");
            }

            if (steamService.Readiness != SteamReadiness.Ready)
            {
                // Gotcha 6a: connecting before relay+auth are up fails like a NAT problem.
                return Fail($"Steam is not ready yet: {steamService.DescribeStatus()}");
            }

            if (!SteamNetLayout.ProbeIdentityLayout(steamService.LocalSteamId))
            {
                return Fail("The Steam identity layout could not be verified on this install.");
            }

            selfConnectionId = Common.SteamConnectionId.FromSteamId(steamService.LocalSteamId);
            if (selfConnectionId == 0U)
            {
                return Fail($"SteamID {steamService.LocalSteamId} has no usable account id.");
            }

            try
            {
                receiveBuffer ??= new Il2CppStructArray<IntPtr>(ReceiveBatchSize);
                noOptions ??= new Il2CppStructArray<SteamNetworkingConfigValue_t>(0);
            }
            catch (Exception ex)
            {
                return Disable("allocating the Steam interop buffers", ex);
            }

            if (sendScratch == null)
            {
                sendScratch = new byte[SendScratchBytes];
                sendScratchPin = GCHandle.Alloc(sendScratch, GCHandleType.Pinned);
            }

            if (!RegisterStatusCallback())
            {
                return Fail("Could not register for Steam's connection-status callback.");
            }

            statusDetail = string.Empty;
            state = SteamNetTransportState.Starting;
            return true;
        }

        private bool RegisterStatusCallback()
        {
            if (statusCallback != null)
            {
                return true;
            }

            try
            {
                statusCallback = new SteamCallback
                {
                    // The dispatcher keys on this type's [CallbackIdentity]; a wrong type never fires.
                    CallbackType = Il2CppType.Of<SteamNetConnectionStatusChangedCallback_t>(),
                    Handler = OnConnectionStatusChanged,
                };
                CallbackDispatcher.Register(statusCallback);
                return true;
            }
            catch (Exception ex)
            {
                statusCallback = null;
                Plugin.Log.LogError(
                    $"[steam-net] Registering the connection-status callback failed, so no "
                    + $"connection would ever establish: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // ---------------------------------------------------------------- connection lifecycle

        /// <summary>Main thread (game's callback pump). <c>payload</c> is freed on return; copy out first.</summary>
        private void OnConnectionStatusChanged(IntPtr payload)
        {
            if (!SteamNetLayout.TryReadStatusChange(payload, out var change))
            {
                return;
            }

            Plugin.Log.LogInfo(
                $"[steam-net] Connection {change.Connection} to {change.RemoteSteamId} is now "
                + $"{change.State}.");

            switch (change.State)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    // Host only: a client sees this for its own outgoing connection and must not close it.
                    if (isHost == true)
                    {
                        OnIncomingConnection(change);
                    }

                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute:
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    OnConnectionEstablished(change);
                    break;

                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    OnConnectionEnded(change);
                    break;
            }
        }

        private void OnIncomingConnection(SteamConnectionStatusChange change)
        {
            if (state != SteamNetTransportState.Running)
            {
                Plugin.Log.LogWarning(
                    $"[steam-net] Refusing a connection from {change.RemoteSteamId}: the listen "
                    + $"socket is {state}, not Running.");
                CloseRaw(change.Connection, SteamNetEndReason.SessionClosed, "not listening");
                return;
            }

            try
            {
                var accepted = SteamNetworkingSockets.AcceptConnection(new HSteamNetConnection(change.Connection));
                if (accepted != EResult.k_EResultOK)
                {
                    Plugin.Log.LogWarning(
                        $"[steam-net] AcceptConnection for {change.RemoteSteamId} returned {accepted}; "
                        + "closing it.");
                    CloseRaw(change.Connection, SteamNetEndReason.SessionClosed, accepted.ToString());
                    return;
                }

                if (!SteamNetworkingSockets.SetConnectionPollGroup(new HSteamNetConnection(change.Connection), pollGroup))
                {
                    Plugin.Log.LogError(
                        $"[steam-net] Could not put {change.RemoteSteamId}'s connection in the poll "
                        + "group, so nothing it sends would ever be read. Closing it.");
                    CloseRaw(change.Connection, SteamNetEndReason.SessionClosed, "poll group");
                    return;
                }
            }
            catch (Exception ex)
            {
                Disable("AcceptConnection/SetConnectionPollGroup", ex);
                return;
            }

            Plugin.Log.LogInfo($"[steam-net] Accepted a connection from {change.RemoteSteamId}.");
        }

        private void OnConnectionEstablished(SteamConnectionStatusChange change)
        {
            peerSteamIds[change.Connection] = change.RemoteSteamId;

            if (isHost == false)
            {
                hostConnection = new HSteamNetConnection(change.Connection);
                state = SteamNetTransportState.Running;
            }

            Plugin.Log.LogInfo($"[steam-net] Connected to {change.RemoteSteamId}.");
            PeerConnected?.Invoke(change.Connection, change.RemoteSteamId);

            // Client speaks first. Reliable: one-shot that everything else is gated behind.
            if (isHost == false)
            {
                SendToHost(new Introduced
                {
                    ConnectionId = selfConnectionId,
                    Name = Configuration.ModConfig.PlayerName.Value,
                    IsHost = false,
                }, NetDelivery.ReliableOrdered);
            }
        }

        private void OnConnectionEnded(SteamConnectionStatusChange change)
        {
            var description = SteamNetLayout.DescribeEndReason(change.EndReason);

            peerIntroductions.TryGetValue(change.Connection, out var introduction);

            peerSteamIds.Remove(change.Connection);
            peerIntroductions.Remove(change.Connection);
            ForgetPeerHandle(change.Connection);

            if (isHost == false && change.Connection == hostConnection.m_HSteamNetConnection)
            {
                hostConnection = HSteamNetConnection.Invalid;
            }

            // Always close our own side, even when the peer closed first, or the handle leaks.
            CloseRaw(change.Connection, 0, string.Empty);

            Plugin.Log.LogInfo($"[steam-net] Connection to {change.RemoteSteamId} ended: {description}.");
            PeerDisconnected?.Invoke(change.Connection, description);

            if (introduction == null)
            {
                return;
            }

            // Losing the host ends the session; nothing else on the Steam path reports it.
            if (introduction.IsHost)
            {
                Plugin.StartNotification(
                    ("OmniBonk", "HostDisconnected"),
                    ("OmniBonk", "HostDisconnected_Description"),
                    [introduction.Name],
                    AudioManager.Instance.uiAbort,
                    item: EItem.BobDead);

                Plugin.GoToMainMenu();
                return;
            }

            // Only the host announces departures.
            if (isHost == true)
            {
                var departure = new PlayerDisconnected { ConnectionId = introduction.ConnectionId };
                EventManager.OnPlayerDisconnected(departure);
                SendToAllClients(departure, NetDelivery.ReliableOrdered);
            }

            if (peerIntroductions.Count == 0
                && (Plugin.Instance.Mode.Mode == Common.Models.NetworkModeType.Random
                    || Plugin.Instance.Mode.Mode == Common.Models.NetworkModeType.Friendlies
                        && GameManager.Instance?.player != null))
            {
                Plugin.StartNotification(
                    ("OmniBonk", "AllPlayerDisconnected"),
                    ("OmniBonk", "AllPlayerDisconnected_Description"),
                    [introduction.Name],
                    AudioManager.Instance.uiAbort,
                    item: EItem.BobDead);

                Plugin.GoToMainMenu();
            }
        }

        // ---------------------------------------------------------------- receive

        public void Poll()
        {
            if (disabled || state != SteamNetTransportState.Running)
            {
                return;
            }

            if (isHost == true)
            {
                DrainPollGroup();
            }
            else if (hostConnection != HSteamNetConnection.Invalid)
            {
                DrainConnection();
            }
        }

        private void DrainPollGroup()
        {
            for (var iteration = 0; iteration < MaxDrainIterationsPerFrame; iteration++)
            {
                int received;
                try
                {
                    received = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(
                        pollGroup, receiveBuffer, ReceiveBatchSize);
                }
                catch (Exception ex)
                {
                    Disable("ReceiveMessagesOnPollGroup", ex);
                    return;
                }

                if (received <= 0)
                {
                    return;
                }

                DispatchReceived(received);

                if (received < ReceiveBatchSize)
                {
                    return;
                }
            }
        }

        private void DrainConnection()
        {
            for (var iteration = 0; iteration < MaxDrainIterationsPerFrame; iteration++)
            {
                int received;
                try
                {
                    received = SteamNetworkingSockets.ReceiveMessagesOnConnection(
                        hostConnection, receiveBuffer, ReceiveBatchSize);
                }
                catch (Exception ex)
                {
                    Disable("ReceiveMessagesOnConnection", ex);
                    return;
                }

                if (received <= 0)
                {
                    return;
                }

                DispatchReceived(received);

                if (received < ReceiveBatchSize)
                {
                    return;
                }
            }
        }

        private void DispatchReceived(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var pointer = receiveBuffer[i];
                if (pointer == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    // Safe to read as a struct: all fields inline (unlike SteamNetConnectionInfo_t).
                    var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                    HandleRawMessage(message);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[steam-net] Dropped a message that could not be read: {ex.Message}");
                }
                finally
                {
                    // Mandatory, or the native message leaks.
                    SteamNetworkingMessage_t.Release(pointer);
                    receiveBuffer[i] = IntPtr.Zero;
                }
            }
        }

        private void HandleRawMessage(SteamNetworkingMessage_t message)
        {
            var size = message.m_cbSize;
            if (size <= 0 || message.m_pData == IntPtr.Zero)
            {
                return;
            }

            // Copied: the native buffer is released on return. See docs/netplay/04-performance-and-gc.md before pooling.
            var payload = new byte[size];
            Marshal.Copy(message.m_pData, payload, 0, size);

            IGameNetworkMessage deserialized;
            try
            {
                deserialized = MemoryPackSerializer.Deserialize<IGameNetworkMessage>(payload);
            }
            catch (MemoryPackSerializationException)
            {
                // Corrupt or cross-version packet: drop quietly.
                return;
            }

            if (deserialized == null)
            {
                return;
            }

            var peerHandle = message.m_conn.m_HSteamNetConnection;

            if (TryHandlePeerScopedMessage(deserialized, peerHandle))
            {
                return;
            }

            netMessageRouter ??= Plugin.Services.GetService<INetMessageRouter>();

            if (!netMessageRouter.Route(deserialized, isHost == true))
            {
                Plugin.Log.LogWarning($"[steam-net] Unknown message type received. message={deserialized}");
            }
        }

        /// <summary>
        /// Messages whose meaning depends on the arriving peer. No relay cases (SDR is invisible).
        /// The host ignores <c>PlayerDisconnected</c>: departures arrive as status callbacks, and a client has no authority over third parties.
        /// </summary>
        private bool TryHandlePeerScopedMessage(IGameNetworkMessage message, uint peerHandle)
        {
            switch (message)
            {
                case Introduced introduced:
                    OnIntroduced(introduced, peerHandle);
                    return true;

                case PlayerDisconnected playerDisconnected when isHost == false:
                    OnPeerReportedDisconnected(playerDisconnected);
                    return true;

                case SelectedCharacter selectedCharacter when isHost == true:
                    OnSelectedCharacter(selectedCharacter, peerHandle);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Maps the peer handle to its game connection id.</summary>
        private void OnIntroduced(Introduced introduced, uint peerHandle)
        {
            // The claimed id must match its SteamID derivation, or it could impersonate someone.
            var steamId = GetPeerSteamId(peerHandle);
            var expected = Common.SteamConnectionId.FromSteamId(steamId);

            if (expected == 0U || introduced.ConnectionId != expected)
            {
                Plugin.Log.LogError(
                    $"[steam-net] {steamId} introduced itself as connection {introduced.ConnectionId} "
                    + $"but its SteamID derives to {expected}. Refusing the connection.");
                CloseRaw(peerHandle, SteamNetEndReason.ProtocolMismatch, "connection id mismatch");
                return;
            }

            if (!peerIntroductions.TryAdd(peerHandle, new PeerIntroduction(introduced.Name, introduced.ConnectionId, introduced.IsHost)))
            {
                Plugin.Log.LogWarning($"[steam-net] Duplicate introduction from {steamId}, ignoring.");
                return;
            }

            AssignConnectionId(peerHandle, introduced.ConnectionId);

            // Added, not just renamed: unlike rendezvous, there is no MatchInfo pre-populating the roster.
            var player = playerManagerService.GetPlayer(introduced.ConnectionId);
            if (player == null)
            {
                playerManagerService.AddPlayer(
                    introduced.ConnectionId, introduced.IsHost, isSelf: false, introduced.Name);
            }
            else
            {
                player.Name = introduced.Name;
                playerManagerService.UpdatePlayer(player);
            }

            Plugin.Log.LogInfo(
                $"[steam-net] {introduced.Name} introduced as connection {introduced.ConnectionId} "
                + $"(host: {introduced.IsHost}).");

            if (isHost != true)
            {
                return;
            }

            if (Plugin.Instance.Mode.Mode == Common.Models.NetworkModeType.Friendlies)
            {
                Plugin.StartNotification(
                    ("OmniBonk", "FriendliesClientJoinSuccess"),
                    ("OmniBonk", "FriendliesClientJoinSuccessDesc"),
                    [introduced.Name]);
            }

            SendToClient(introduced.ConnectionId, new Introduced
            {
                ConnectionId = selfConnectionId,
                Name = Configuration.ModConfig.PlayerName.Value,
                IsHost = true,
            });
        }

        /// <summary>Always a third party; host loss arrives via <see cref="OnConnectionEnded"/>.</summary>
        private void OnPeerReportedDisconnected(PlayerDisconnected playerDisconnected)
        {
            EventManager.OnPlayerDisconnected(playerDisconnected);
        }

        /// <summary>Host only. Records and forwards a choice; starts the run once everyone has chosen.</summary>
        private void OnSelectedCharacter(SelectedCharacter selectedCharacter, uint peerHandle)
        {
            if (peerIntroductions.TryGetValue(peerHandle, out var introduction))
            {
                introduction.HasSelected = true;
            }

            // Written to the record: RunStatistics reads it later.
            var player = playerManagerService.GetPlayer(selectedCharacter.ConnectionId);
            if (player == null)
            {
                Plugin.Log.LogWarning(
                    $"[steam-net] SelectedCharacter for unknown connection {selectedCharacter.ConnectionId}.");
                return;
            }

            player.Character = selectedCharacter.Character;
            player.Skin = selectedCharacter.Skin;
            playerManagerService.UpdatePlayer(player);

            Plugin.Log.LogInfo(
                $"[steam-net] {selectedCharacter.ConnectionId} selected character "
                + $"{selectedCharacter.Character}, skin '{selectedCharacter.Skin}'.");

            SendToAllClientsExcept(selectedCharacter.ConnectionId, selectedCharacter);

            if (AreAllPeersReady() && playerManagerService.HasSelectedCharacter() && Plugin.Instance.IS_HOST_READY)
            {
                var runConfig = WindowManager.activeWindow.GetComponentInChildren<MapSelectionUi>().runConfig;
                MapController.StartNewMap(runConfig);
            }
        }

        private void SeedLocalPlayer(bool isLocalHost)
        {
            if (playerManagerService.GetPlayer(selfConnectionId) != null)
            {
                return;
            }

            playerManagerService.AddPlayer(
                selfConnectionId, isLocalHost, isSelf: true, Configuration.ModConfig.PlayerName.Value);
        }

        /// <summary>Counted against the Steam lobby, not the roster (which is built from introductions).</summary>
        public bool HasAllPeersConnected()
        {
            if (state != SteamNetTransportState.Running)
            {
                return false;
            }

            var members = steamLobbyService.GetMembers().Count;
            if (members <= 1)
            {
                return true;
            }

            return peerIntroductions.Count >= members - 1;
        }

        /// <summary>True between ConnectP2P and Connected.</summary>
        public bool IsHandlingConnection() => state == SteamNetTransportState.Starting;

        public bool AreAllPeersReady()
        {
            if (isHost != true)
            {
                return false;
            }

            foreach (var (_, introduction) in peerIntroductions)
            {
                if (!introduction.HasSelected)
                {
                    return false;
                }
            }

            return true;
        }

        public int GetCurrentReadyPeersCount()
        {
            if (isHost != true)
            {
                return 0;
            }

            var count = 0;
            foreach (var (_, introduction) in peerIntroductions)
            {
                if (introduction.HasSelected)
                {
                    count++;
                }
            }

            return count;
        }

        // ---------------------------------------------------------------- send

        public void SendToAllClients<T>(T data, NetDelivery delivery) where T : IGameNetworkMessage
        {
            if (!EnsureIsHost(HostGuardMessageName(data)))
            {
                return;
            }

            var payload = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);
            var sent = SendToEveryPeer(payload, delivery, data.GetType().Name, excludedHandle: null);
            BandwidthDiagnostics.Record(data.GetType().Name, payload.Length, sent);
        }

        public void SendToAllClients(byte[] data, NetDelivery delivery, string messageTypeName = null)
        {
            if (!EnsureIsHost(HostGuardMessageName(data, messageTypeName)))
            {
                return;
            }

            var label = messageTypeName ?? "(pre-serialized)";
            var sent = SendToEveryPeer(data, delivery, label, excludedHandle: null);
            BandwidthDiagnostics.Record(label, data.Length, sent);
        }

        public void SendToAllClientsExcept<T>(uint excludedConnectionId, T data) where T : IGameNetworkMessage
        {
            if (!EnsureIsHost(HostGuardMessageName(data)))
            {
                return;
            }

            var payload = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);

            uint? excluded = peerHandlesByConnectionId.TryGetValue(excludedConnectionId, out var handle)
                ? handle
                : null;

            var sent = SendToEveryPeer(payload, NetDelivery.ReliableOrdered, data.GetType().Name, excluded);
            BandwidthDiagnostics.Record(data.GetType().Name, payload.Length, sent);
        }

        public void SendToClient<T>(uint connectionId, T data) where T : IGameNetworkMessage
        {
            if (!EnsureIsHost(HostGuardMessageName(data)))
            {
                return;
            }

            if (!peerHandlesByConnectionId.TryGetValue(connectionId, out var handle))
            {
                Plugin.Log.LogWarning(
                    $"[steam-net] SendToClient: no connected peer for connection id {connectionId}; "
                    + "message dropped.");
                return;
            }

            var payload = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);
            if (SendRaw(handle, payload, NetDelivery.ReliableOrdered, data.GetType().Name))
            {
                BandwidthDiagnostics.Record(data.GetType().Name, payload.Length, 1);
            }
        }

        public void SendToHost<T>(T data, NetDelivery? delivery = null) where T : IGameNetworkMessage
        {
            if (!EnsureIsClient())
            {
                return;
            }

            if (hostConnection == HSteamNetConnection.Invalid)
            {
                Plugin.Log.LogWarning("[steam-net] Not connected to a host.");
                return;
            }

            var payload = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);

            // Default is ReliableOrdered: Steam has no ReliableSequenced, and the handshake is a one-shot.
            var resolved = delivery ?? NetDelivery.ReliableOrdered;

            if (SendRaw(hostConnection.m_HSteamNetConnection, payload, resolved, data.GetType().Name))
            {
                BandwidthDiagnostics.Record(data.GetType().Name, payload.Length, 1);
            }
        }

        private int SendToEveryPeer(byte[] payload, NetDelivery delivery, string label, uint? excludedHandle)
        {
            var sent = 0;

            foreach (var handle in peerSteamIds.Keys)
            {
                if (excludedHandle.HasValue && handle == excludedHandle.Value)
                {
                    continue;
                }

                if (SendRaw(handle, payload, delivery, label))
                {
                    sent++;
                }
            }

            return sent;
        }

        /// <summary>Copies into the pre-pinned scratch buffer; per-send GCHandles would dominate at ~233 sends/s.</summary>
        private bool SendRaw(uint peerHandle, byte[] payload, NetDelivery delivery, string label)
        {
            if (disabled || payload == null || payload.Length == 0)
            {
                return false;
            }

            // Oversized unreliable payloads are promoted to reliable; see MaxUnreliableBytes.
            var effective = delivery;

            if (delivery == NetDelivery.Unreliable && payload.Length > MaxUnreliableBytes)
            {
                effective = NetDelivery.ReliableOrdered;

                LogPromotionThrottled(label, payload.Length);
            }

            EResult result;
            try
            {
                if (payload.Length <= sendScratch.Length)
                {
                    Buffer.BlockCopy(payload, 0, sendScratch, 0, payload.Length);
                    result = SteamNetworkingSockets.SendMessageToConnection(
                        new HSteamNetConnection(peerHandle),
                        sendScratchPin.AddrOfPinnedObject(),
                        (uint)payload.Length,
                        ToSteamFlags(effective),
                        out long _);
                }
                else
                {
                    // Past the scratch buffer, so reliable by construction.
                    var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
                    try
                    {
                        result = SteamNetworkingSockets.SendMessageToConnection(
                            new HSteamNetConnection(peerHandle),
                            pin.AddrOfPinnedObject(),
                            (uint)payload.Length,
                            ToSteamFlags(NetDelivery.ReliableOrdered),
                            out long _);
                    }
                    finally
                    {
                        pin.Free();
                    }
                }
            }
            catch (Exception ex)
            {
                Disable("SendMessageToConnection", ex);
                return false;
            }

            if (result == EResult.k_EResultOK)
            {
                return true;
            }

            LogSendFailureThrottled(result, label, peerHandle, payload.Length);
            return false;
        }

        /// <summary>Own throttle clock so it cannot hide send failures, or vice versa.</summary>
        private void LogPromotionThrottled(string label, int bytes)
        {
            var now = Environment.TickCount64;
            if (now < nextPromotionLogTick)
            {
                suppressedPromotions++;
                return;
            }

            nextPromotionLogTick = now + PromotionLogIntervalMs;
            var suppressed = suppressedPromotions;
            suppressedPromotions = 0;

            Plugin.Log.LogWarning(
                $"[steam-net] Promoted an unreliable send to reliable: {label} at {bytes} B is over "
                + $"the {MaxUnreliableBytes} B single-datagram limit. It arrives, but ordered and "
                + "acked, so it is no longer superseded by the next tick - this message wants "
                + "splitting at the sender."
                + (suppressed > 0 ? $" (+{suppressed} more in the last {PromotionLogIntervalMs / 1000}s)" : string.Empty));
        }

        /// <summary>Throttled: a broken connection fails hundreds of sends per second.</summary>
        private void LogSendFailureThrottled(EResult result, string label, uint peerHandle, int bytes)
        {
            var now = Environment.TickCount64;
            if (now < nextSendFailureLogTick)
            {
                suppressedSendFailures++;
                return;
            }

            nextSendFailureLogTick = now + SendFailureLogIntervalMs;
            var suppressed = suppressedSendFailures;
            suppressedSendFailures = 0;

            var steamId = peerSteamIds.TryGetValue(peerHandle, out var id) ? id : 0UL;
            Plugin.Log.LogWarning(
                $"[steam-net] Send failed: {result} ({label}, {bytes} B, to {steamId})"
                + (suppressed > 0 ? $" (+{suppressed} more in the last second)" : string.Empty));
        }

        /// <summary>
        /// Steam's reliable channel is always ordered, so every reliable kind maps to it.
        /// NoNagle on unreliable only: superseded state should not wait to coalesce.
        /// </summary>
        private static int ToSteamFlags(NetDelivery delivery) => delivery switch
        {
            NetDelivery.Unreliable => Constants.k_nSteamNetworkingSend_UnreliableNoNagle,
            _ => Constants.k_nSteamNetworkingSend_Reliable,
        };

        // ---------------------------------------------------------------- peers

        public void AssignConnectionId(uint peerHandle, uint connectionId)
        {
            peerHandlesByConnectionId[connectionId] = peerHandle;
        }

        public void ForgetConnectionId(uint connectionId)
        {
            peerHandlesByConnectionId.Remove(connectionId);
        }

        /// <summary>Finds then removes, to avoid mutating the dictionary mid-enumeration.</summary>
        private void ForgetPeerHandle(uint peerHandle)
        {
            uint? found = null;

            foreach (var (connectionId, handle) in peerHandlesByConnectionId)
            {
                if (handle == peerHandle)
                {
                    found = connectionId;
                    break;
                }
            }

            if (found.HasValue)
            {
                peerHandlesByConnectionId.Remove(found.Value);
            }
        }

        public ulong GetPeerSteamId(uint peerHandle) =>
            peerSteamIds.TryGetValue(peerHandle, out var steamId) ? steamId : 0UL;

        public IReadOnlyList<uint> GetPeerHandles() => new List<uint>(peerSteamIds.Keys);

        /// <summary>
        /// RTT or -1. Via <see cref="SteamNetNative"/>: the interop signature's struct layout crashes.
        /// </summary>
        public int GetLatency(uint connectionId)
        {
            if (!peerHandlesByConnectionId.TryGetValue(connectionId, out var peerHandle))
            {
                return -1;
            }

            return SteamNetNative.TryGetPing(peerHandle);
        }

        /// <summary>Link diagnostics; climbing pending/unacked reliable bytes indicate head-of-line blocking.</summary>
        public string DescribeLink(uint connectionId)
        {
            if (!peerHandlesByConnectionId.TryGetValue(connectionId, out var peerHandle))
            {
                return "no connection";
            }

            if (!SteamNetNative.TryGetLinkStatus(peerHandle, out var status))
            {
                return "status unavailable";
            }

            return $"rtt {status.PingMs} ms  quality {Quality(status.QualityLocal)}/{Quality(status.QualityRemote)} (local/remote)  "
                + $"pending {status.PendingReliableBytes} B reliable, {status.PendingUnreliableBytes} B unreliable  "
                + $"unacked {status.SentUnackedReliableBytes} B  queue {status.QueueTimeMs:F1} ms";
        }

        /// <summary>Steam reports an unknown quality as -1.</summary>
        private static string Quality(float ratio) =>
            ratio < 0f ? "unknown" : $"{ratio * 100f:F1}%";

        // ---------------------------------------------------------------- teardown

        public void Disconnect(uint connectionId, int endReason, string debugText)
        {
            if (!peerHandlesByConnectionId.TryGetValue(connectionId, out var handle))
            {
                return;
            }

            CloseRaw(handle, endReason, debugText);
            peerSteamIds.Remove(handle);
            peerHandlesByConnectionId.Remove(connectionId);
        }

        /// <summary>The pinned scratch buffer is deliberately kept: freeing it under an in-flight send is worse.</summary>
        public void Shutdown()
        {
            if (state == SteamNetTransportState.Idle && statusCallback == null)
            {
                return;
            }

            // A never-Connected host connection is not in peerSteamIds; close it here or it leaks.
            if (hostConnection != HSteamNetConnection.Invalid
                && !peerSteamIds.ContainsKey(hostConnection.m_HSteamNetConnection))
            {
                CloseRaw(hostConnection.m_HSteamNetConnection, SteamNetEndReason.SessionClosed,
                    "session ended", linger: false);
            }

            // Linger off: nothing queued matters, and lingering sockets leak into the next session.
            foreach (var handle in peerSteamIds.Keys)
            {
                CloseRaw(handle, SteamNetEndReason.SessionClosed, "session ended", linger: false);
            }

            peerSteamIds.Clear();
            peerHandlesByConnectionId.Clear();
            peerIntroductions.Clear();
            selfConnectionId = 0U;

            try
            {
                if (pollGroup != HSteamNetPollGroup.Invalid)
                {
                    SteamNetworkingSockets.DestroyPollGroup(pollGroup);
                }

                if (listenSocket != HSteamListenSocket.Invalid)
                {
                    SteamNetworkingSockets.CloseListenSocket(listenSocket);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-net] Tearing down the Steam sockets threw: {ex.Message}");
            }

            hostConnection = HSteamNetConnection.Invalid;
            pollGroup = HSteamNetPollGroup.Invalid;
            listenSocket = HSteamListenSocket.Invalid;

            UnregisterStatusCallback();

            isHost = null;
            state = SteamNetTransportState.Idle;
            statusDetail = string.Empty;

            Plugin.Log.LogInfo("[steam-net] Shut down.");
        }

        private void UnregisterStatusCallback()
        {
            if (statusCallback == null)
            {
                return;
            }

            try
            {
                CallbackDispatcher.Unregister(statusCallback);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-net] Unregistering the status callback threw: {ex.Message}");
            }

            statusCallback = null;
        }

        /// <summary><paramref name="linger"/> flushes queued sends first; off for session teardown.</summary>
        private void CloseRaw(uint peerHandle, int endReason, string debugText, bool linger = true)
        {
            try
            {
                SteamNetworkingSockets.CloseConnection(
                    new HSteamNetConnection(peerHandle), endReason, debugText ?? string.Empty, linger);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-net] CloseConnection threw: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- status

        public string DescribeStatus()
        {
            if (disabled)
            {
                return "The Steam transport is disabled after a failed call; see the earlier error.";
            }

            return state switch
            {
                SteamNetTransportState.Idle => "Not started.",
                SteamNetTransportState.Failed => $"Failed: {statusDetail}",
                _ => $"{state}, {(isHost == true ? "hosting" : "connected as a client")}, "
                    + $"{peerSteamIds.Count} peer(s).",
            };
        }


        /// <summary>OB-10: name to record a rejected host-only send under.</summary>
        private static string HostGuardMessageName(object data, string messageTypeName = null)
        {
            return !string.IsNullOrEmpty(messageTypeName) ? messageTypeName : data?.GetType().Name;
        }
        /// <summary>OB-10: <paramref name="messageTypeName"/> identifies which path lacks an ownership check.</summary>
        private bool EnsureIsHost(
            string messageTypeName = null,
            [System.Runtime.CompilerServices.CallerMemberName] string sendMethod = null)
        {
            if (isHost != true)
            {
                HostGuardDiagnostics.Record("steam-net", sendMethod, messageTypeName);
                return false;
            }

            return EnsureRunning();
        }

        private bool EnsureIsClient()
        {
            if (isHost != false)
            {
                Plugin.Log.LogWarning("[steam-net] Only a client can perform this action.");
                return false;
            }

            return EnsureRunning();
        }

        /// <summary>Logs (throttled) when refusing, so dropped sends are never silent.</summary>
        private bool EnsureRunning()
        {
            if (state == SteamNetTransportState.Running)
            {
                return true;
            }

            var now = Environment.TickCount64;
            if (now >= nextNotRunningLogTick)
            {
                nextNotRunningLogTick = now + SendFailureLogIntervalMs;
                Plugin.Log.LogWarning(
                    $"[steam-net] Dropping sends: the transport is {state}, not Running.");
            }

            return false;
        }

        private bool Fail(string detail)
        {
            statusDetail = detail;
            state = SteamNetTransportState.Failed;
            Plugin.Log.LogWarning($"[steam-net] {detail}");
            return false;
        }

        private bool Disable(string call, Exception ex)
        {
            if (!disabled)
            {
                disabled = true;
                statusDetail = $"{call} threw {ex.GetType().Name}";
                state = SteamNetTransportState.Failed;
                Plugin.Log.LogError(
                    $"[steam-net] {call} failed, so the Steam transport is now disabled: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }

            return false;
        }
    }
}

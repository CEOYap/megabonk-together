using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Managers;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// <c>ISteamNetworkingSockets</c> behind <see cref="INetTransport"/>. Phase 4.
    ///
    /// <para><b>Structurally parallel to the LiteNetLib transport on purpose.</b> A peer handle is
    /// the raw <c>HSteamNetConnection</c> and stands where <c>NetPeer.Id</c> stands; the game's
    /// connection id is learned from the introduction handshake and mapped, exactly as
    /// <c>gamePeersIntroduced</c> does. That symmetry is what lets the session layer be moved over
    /// in a separate step instead of rewritten alongside this.</para>
    ///
    /// <para><b>Which Steamworks calls this may make is not a matter of taste.</b> Every by-ref
    /// struct on this path was audited against the game's interop assembly first — see
    /// <c>docs/steamworks/05-interop-struct-shapes.md</c>. <c>GetConnectionRealTimeStatus</c> and
    /// <c>GetConnectionInfo</c> are unreachable because their structs carry
    /// <c>Il2CppStructArray</c> fields where native has inline bytes, which is the shape that has
    /// crashed this project twice. The visible cost is <see cref="GetLatency"/>, which returns -1
    /// here.</para>
    ///
    /// <para><b>The connection lifecycle joins the game's callback pump rather than racing it.</b>
    /// <see cref="SteamCallback"/> is registered with the game's own dispatcher, so
    /// <c>SteamNetConnectionStatusChangedCallback_t</c> is delivered to us on the main thread. The
    /// config-value function pointer the migration plan proposed is not needed, and polling
    /// <c>GetAPICallResult</c> — which loses to that pump silently — is not used anywhere here.</para>
    /// </summary>
    internal class SteamNetTransport(
        ISteamService steamService,
        IPlayerManagerService playerManagerService) : ISteamNetTransport
    {
        /// <summary>
        /// Everything received that is not about the connection. Resolved lazily rather than
        /// injected — see <see cref="HandleRawMessage"/>.
        /// </summary>
        private INetMessageRouter netMessageRouter;

        /// <summary>
        /// An application-level port, not a UDP one. It only has to match between host and client;
        /// 0 is what a shipping implementation for this game uses and there is no reason to differ.
        /// </summary>
        private const int VirtualPort = 0;

        /// <summary>
        /// How many message pointers one <c>ReceiveMessagesOnPollGroup</c> call can return. Sized
        /// generously because the array is allocated once and reused — a short one costs extra
        /// interop calls, not memory.
        /// </summary>
        private const int ReceiveBatchSize = 256;

        /// <summary>
        /// Cap on drain iterations per frame. A peer that floods cannot stall the frame
        /// indefinitely; the backlog waits. The loop only repeats when the batch came back full,
        /// which is the signal there is more queued.
        /// </summary>
        private const int MaxDrainIterationsPerFrame = 4;

        /// <summary>
        /// Scratch for outgoing payloads, pinned once. Sized above the reliable path's practical
        /// ceiling; a payload larger than this falls back to a one-off pin rather than truncating.
        /// </summary>
        private const int SendScratchBytes = 64 * 1024;

        private const int SendFailureLogIntervalMs = 1000;

        /// <summary>
        /// The largest payload Steam will carry on a single unreliable datagram. Above this it
        /// fragments <b>unreliably</b>, so losing any one fragment discards the whole message —
        /// which is worse than the unreliable channel's normal failure mode, because a stream that
        /// expects the next tick to supersede a loss instead loses every tick that is oversized.
        /// The LiteNetLib path has the same cliff at its own MTU and handles it the same way.
        /// </summary>
        private const int MaxUnreliableBytes = 1200;

        private bool? isHost;
        private SteamNetTransportState state = SteamNetTransportState.Idle;
        private string statusDetail = string.Empty;

        private HSteamListenSocket listenSocket = HSteamListenSocket.Invalid;
        private HSteamNetPollGroup pollGroup = HSteamNetPollGroup.Invalid;

        /// <summary>The client's single connection to the host. Invalid on a host.</summary>
        private HSteamNetConnection hostConnection = HSteamNetConnection.Invalid;

        /// <summary>
        /// Held for as long as it is registered: the dispatcher keeps a reference on the IL2CPP
        /// side, and letting the managed wrapper go while that is true is how a callback becomes a
        /// crash later.
        /// </summary>
        private SteamCallback statusCallback;

        /// <summary>Peer handle → SteamID. Every connected peer, introduced or not.</summary>
        private readonly Dictionary<uint, ulong> peerSteamIds = [];

        /// <summary>Game connection id → peer handle. Populated by the introduction handshake.</summary>
        private readonly Dictionary<uint, uint> peerHandlesByConnectionId = [];

        /// <summary>
        /// Peer handle → what that peer told us about itself. The Steam equivalent of
        /// <c>gamePeersIntroduced</c>, and deliberately the same shape: a peer is *connected* as
        /// soon as Steam says so, and *introduced* only once it has sent its own details.
        /// </summary>
        private readonly Dictionary<uint, PeerIntroduction> peerIntroductions = [];

        /// <summary>
        /// Our own connection id, derived from our SteamID rather than assigned. Set when a session
        /// starts. See <see cref="Common.SteamConnectionId"/> for why deriving it is not the
        /// independent-decision mistake it superficially resembles.
        /// </summary>
        private uint selfConnectionId;

        private Il2CppStructArray<IntPtr> receiveBuffer;
        private Il2CppStructArray<SteamNetworkingConfigValue_t> noOptions;

        private byte[] sendScratch;
        private GCHandle sendScratchPin;

        private long nextSendFailureLogTick;
        private int suppressedSendFailures;

        /// <summary>
        /// Latches the transport off after a Steam call throws. Repeating a call that just failed
        /// across the IL2CPP boundary is how a recoverable error becomes a crash — the same
        /// discipline <see cref="SteamService"/> uses.
        /// </summary>
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
                // A zero-length array rather than null: the interop wrapper reads the array's data
                // pointer, and IL2CPP has no null check in front of that.
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

            // Seeds the roster with ourselves. On the rendezvous path HandleMatch does this for
            // every peer at once out of MatchInfo; here there is no such message, so each side adds
            // itself and learns the others from their introductions.
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
                // Steam P2P does not loop back. Saying so is worth a line, because the two-instance
                // test harness is the obvious way somebody arrives here.
                return Fail("Cannot connect to yourself — Steam peer-to-peer has no loopback.");
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
        /// The gate every start path shares: Steam usable, buffers allocated, the status callback
        /// registered, and the identity layout proved.
        ///
        /// <para><b>The callback is registered before the first socket call, not after.</b> It
        /// reports transitions, and a connection that reaches <c>Connecting</c> before we are
        /// listening for it is one the host never accepts.</para>
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
                // Gating on both relay access and authentication, per Gotcha 6a. Connecting with
                // one of them still coming up fails in a way that looks like a NAT problem.
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
                    // Load-bearing, unlike on a CallResult: the dispatcher derives the callback id
                    // from the [CallbackIdentity] attribute on whatever this returns, so a wrong
                    // type is filed under the wrong id and simply never fires.
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

        /// <summary>
        /// Delivered by the game's own callback pump, so this is the main thread.
        ///
        /// <para><c>payload</c> belongs to the dispatcher and is freed when this returns — see
        /// <see cref="SteamNetLayout.TryReadStatusChange"/>, which copies out everything needed.</para>
        /// </summary>
        private void OnConnectionStatusChanged(IntPtr payload)
        {
            if (!SteamNetLayout.TryReadStatusChange(payload, out var change))
            {
                return;
            }

            switch (change.State)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    OnIncomingConnection(change);
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

        /// <summary>
        /// A peer is asking to connect. Only a host sees this — a client's outgoing connection goes
        /// straight from <c>Connecting</c> to <c>Connected</c> without asking us anything.
        /// </summary>
        private void OnIncomingConnection(SteamConnectionStatusChange change)
        {
            if (isHost != true || state != SteamNetTransportState.Running)
            {
                CloseRaw(change.Connection, SteamNetEndReason.SessionClosed, "not hosting");
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
                    // Not fatal to the connection, but it would then never be drained, which looks
                    // exactly like a peer that connects and says nothing.
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

            // The client speaks first, as it does on the rendezvous path. Reliable and ordered
            // because it is a one-shot that everything else is gated behind: until the host has
            // this, it cannot address us by connection id at all.
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

            // A peer that never introduced itself has no connection id, so nothing downstream knows
            // it existed and there is nothing to announce or to end the session over.
            if (introduction == null)
            {
                return;
            }

            // Losing the host ends the session, and something has to say so — otherwise a client
            // whose host quit sits in a run that no longer exists, receiving nothing, with no
            // indication anything is wrong. The rendezvous transport does this in
            // HandleDisconnectedPeer; it is not optional and it has no equivalent anywhere else in
            // the Steam path.
            if (introduction.IsHost)
            {
                Plugin.StartNotification(
                    ("MegabonkTogether", "HostDisconnected"),
                    ("MegabonkTogether", "HostDisconnected_Description"),
                    [introduction.Name],
                    AudioManager.Instance.uiAbort,
                    item: EItem.BobDead);

                Plugin.GoToMainMenu();
                return;
            }

            // The host is the authority on who is in the session, so it tells the others. A client
            // does not: it has no standing to report a third party, and everyone else is being told
            // by the host anyway.
            if (isHost == true)
            {
                var departure = new PlayerDisconnected { ConnectionId = introduction.ConnectionId };
                EventManager.OnPlayerDisconnected(departure);
                SendToAllClients(departure, NetDelivery.ReliableOrdered);
            }

            // Everyone has gone and there is a run in progress. Mirrors the rendezvous transport's
            // AllPlayerDisconnected path: a session of one is over regardless of which side is left.
            if (peerIntroductions.Count == 0
                && (Plugin.Instance.Mode.Mode == Common.Models.NetworkModeType.Random
                    || Plugin.Instance.Mode.Mode == Common.Models.NetworkModeType.Friendlies
                        && GameManager.Instance?.player != null))
            {
                Plugin.StartNotification(
                    ("MegabonkTogether", "AllPlayerDisconnected"),
                    ("MegabonkTogether", "AllPlayerDisconnected_Description"),
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

            // The host drains every peer in one call through the poll group; a client has exactly
            // one connection and no poll group to drain.
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

                // A batch that came back short is the whole queue; anything else and there may be
                // more waiting.
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
                    // FromIntPtr copies the native struct out. Its layout matches — every field is
                    // inline, including the nested identity — which is why this one may be read as
                    // a struct while SteamNetConnectionInfo_t may not.
                    var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                    HandleRawMessage(message);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[steam-net] Dropped a message that could not be read: {ex.Message}");
                }
                finally
                {
                    // Mandatory. Each received message is a native allocation and leaks otherwise.
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

            // Copied rather than deserialized in place: MemoryPack wants a span over managed
            // memory, and the native buffer is freed by Release the moment this returns. Pooling
            // this is the obvious next optimisation and is deliberately not done yet — see
            // docs/netplay/04-performance-and-gc.md before changing it, because the receive path is
            // hot enough that getting it wrong costs more than the allocation.
            var payload = new byte[size];
            Marshal.Copy(message.m_pData, payload, 0, size);

            IGameNetworkMessage deserialized;
            try
            {
                deserialized = MemoryPackSerializer.Deserialize<IGameNetworkMessage>(payload);
            }
            catch (MemoryPackSerializationException)
            {
                // Same posture as the LiteNetLib path: a corrupt or cross-version packet is dropped
                // quietly rather than logged per message.
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

            // Resolved once, on first use. Constructor injection would be a cycle: the router needs
            // INetTransport, and this is one.
            netMessageRouter ??= Plugin.Services.GetService<INetMessageRouter>();

            if (!netMessageRouter.Route(deserialized, isHost == true))
            {
                Plugin.Log.LogWarning($"[steam-net] Unknown message type received. message={deserialized}");
            }
        }

        /// <summary>
        /// The Steam counterpart of <c>UdpClientService.TryHandlePeerScopedMessage</c>: the messages
        /// whose meaning depends on which peer they arrived on. Returns true when handled.
        ///
        /// <para><b>Three cases rather than the LiteNetLib side's eight</b>, and the missing five are
        /// all relay. There is no relay peer here — SDR is the relay, and it is invisible above the
        /// socket — so every branch that existed to tell a relayed peer from a direct one is simply
        /// absent rather than ported and left dead.</para>
        ///
        /// <para><b>The host does not handle <c>PlayerDisconnected</c>.</b> On the rendezvous path
        /// that message is how a host learns about a relayed peer leaving, because no socket event
        /// fires for one. Here every departure arrives as a connection-status callback, so a
        /// <c>PlayerDisconnected</c> reaching a host would be a client asserting something about a
        /// third party — which it has no authority to do.</para>
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

        /// <summary>
        /// A peer said who it is. This is where a connection handle acquires a game connection id,
        /// and until it does, nothing addressed by connection id can reach that peer.
        /// </summary>
        private void OnIntroduced(Introduced introduced, uint peerHandle)
        {
            // The id the peer claims must be the one its SteamID derives to. Both ends compute it
            // from the same immutable input, so a mismatch is not a disagreement to reconcile — it
            // is a peer on a different derivation or an outright forgery, and either way indexing
            // it under an id of its choosing would let it impersonate somebody.
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

            // Added, not just renamed. On the rendezvous path every peer is already in the roster
            // by the time it introduces itself, because MatchInfo listed them all up front. Here the
            // introduction is the first this side has heard of them, so a GetPlayer-and-rename would
            // silently do nothing and the peer would never exist.
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
                    ("MegabonkTogether", "FriendliesClientJoinSuccess"),
                    ("MegabonkTogether", "FriendliesClientJoinSuccessDesc"),
                    [introduced.Name]);
            }

            // The host answers with its own details, exactly as the rendezvous path does. Addressed
            // by connection id, which the AssignConnectionId above has just made resolvable.
            SendToClient(introduced.ConnectionId, new Introduced
            {
                ConnectionId = selfConnectionId,
                Name = Configuration.ModConfig.PlayerName.Value,
                IsHost = true,
            });
        }

        /// <summary>
        /// A client was told by the host that another client left.
        ///
        /// <para><b>This is only ever about a third party</b>, which is why it does nothing but
        /// republish. On the rendezvous path the same message could also mean "the host is gone",
        /// because a relayed peer's departure produced no socket event and had to be announced. Here
        /// losing the host is a connection-status callback on our own connection — see
        /// <see cref="OnConnectionEnded"/> — so there is no case to disambiguate.</para>
        /// </summary>
        private void OnPeerReportedDisconnected(PlayerDisconnected playerDisconnected)
        {
            EventManager.OnPlayerDisconnected(playerDisconnected);
        }

        /// <summary>
        /// Host only. Records that a peer has chosen, forwards the choice, and starts the run once
        /// everyone has.
        /// </summary>
        private void OnSelectedCharacter(SelectedCharacter selectedCharacter, uint peerHandle)
        {
            if (peerIntroductions.TryGetValue(peerHandle, out var introduction))
            {
                introduction.HasSelected = true;
            }

            // Applied to the replicated record rather than republished through EventManager: the
            // RunStatistics sent later reads this field, and an event-only update would miss it.
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

            SendToAllClientsExcept(selectedCharacter.ConnectionId, selectedCharacter);

            if (AreAllPeersReady() && playerManagerService.HasSelectedCharacter() && Plugin.Instance.IS_HOST_READY)
            {
                var runConfig = WindowManager.activeWindow.GetComponentInChildren<MapSelectionUi>().runConfig;
                MapController.StartNewMap(runConfig);
            }
        }

        /// <summary>
        /// Puts the local player in the roster under the connection id derived from our own SteamID,
        /// so that everything indexing players by connection id has us before any peer arrives.
        /// </summary>
        private void SeedLocalPlayer(bool isLocalHost)
        {
            if (playerManagerService.GetPlayer(selfConnectionId) != null)
            {
                return;
            }

            playerManagerService.AddPlayer(
                selfConnectionId, isLocalHost, isSelf: true, Configuration.ModConfig.PlayerName.Value);
        }

        /// <summary>Host only. Whether every introduced peer has chosen a character.</summary>
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
            if (!EnsureIsHost())
            {
                return;
            }

            var payload = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);
            var sent = SendToEveryPeer(payload, delivery, data.GetType().Name, excludedHandle: null);
            BandwidthDiagnostics.Record(data.GetType().Name, payload.Length, sent);
        }

        public void SendToAllClients(byte[] data, NetDelivery delivery, string messageTypeName = null)
        {
            if (!EnsureIsHost())
            {
                return;
            }

            var label = messageTypeName ?? "(pre-serialized)";
            var sent = SendToEveryPeer(data, delivery, label, excludedHandle: null);
            BandwidthDiagnostics.Record(label, data.Length, sent);
        }

        public void SendToAllClientsExcept<T>(uint excludedConnectionId, T data) where T : IGameNetworkMessage
        {
            if (!EnsureIsHost())
            {
                return;
            }

            var payload = MemoryPackSerializer.Serialize<IGameNetworkMessage>(data);

            // A miss means the excluded player is not a connected peer — already gone, or never
            // introduced — in which case there is nothing here to exclude.
            uint? excluded = peerHandlesByConnectionId.TryGetValue(excludedConnectionId, out var handle)
                ? handle
                : null;

            var sent = SendToEveryPeer(payload, NetDelivery.ReliableOrdered, data.GetType().Name, excluded);
            BandwidthDiagnostics.Record(data.GetType().Name, payload.Length, sent);
        }

        public void SendToClient<T>(uint connectionId, T data) where T : IGameNetworkMessage
        {
            if (!EnsureIsHost())
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

            // ReliableSequenced has no Steam equivalent and exactly one caller — the implicit
            // default on the client's Introduced handshake, which is a one-shot that should never
            // have been on a channel that may drop intermediate packets. Rather than pick a lossy
            // translation, the default here is the guarantee that call site actually needs.
            var resolved = delivery ?? NetDelivery.ReliableOrdered;

            if (SendRaw(hostConnection.m_HSteamNetConnection, payload, resolved, data.GetType().Name))
            {
                BandwidthDiagnostics.Record(data.GetType().Name, payload.Length, 1);
            }
        }

        /// <summary>Returns how many peers the payload actually went to, for the bandwidth counters.</summary>
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

        /// <summary>
        /// One send. Copies into the pinned scratch buffer rather than pinning per message: these
        /// paths reach 233 sends/s, and a <c>GCHandle</c> pair each would be the dominant cost on
        /// them.
        /// </summary>
        private bool SendRaw(uint peerHandle, byte[] payload, NetDelivery delivery, string label)
        {
            if (disabled || payload == null || payload.Length == 0)
            {
                return false;
            }

            // Promoted, not truncated and not dropped. An unreliable payload past the single-datagram
            // limit fragments unreliably, so one lost fragment discards all of it — an outcome the
            // caller did not ask for and cannot see. Reliable fragments properly up to 512 KB. The
            // LiteNetLib transport promotes at its own MTU for the same reason.
            var effective = delivery == NetDelivery.Unreliable && payload.Length > MaxUnreliableBytes
                ? NetDelivery.ReliableOrdered
                : delivery;

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
                    // Over the scratch buffer, which is already far past the unreliable limit, so
                    // this is reliable by construction. Costs a one-off pin, which at this size is
                    // the least of it.
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

        /// <summary>
        /// <see cref="INetTransport"/>'s sends return void, so a Steam implementation has to report
        /// its own failures — and throttle them, because a broken connection fails every send on a
        /// path doing hundreds per second, and an unthrottled log would cost more frame time than
        /// the sends did.
        /// </summary>
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
        /// The mod's delivery vocabulary in Steam's terms. The single place the reliability map
        /// meets <c>ISteamNetworkingSockets</c>; the full policy is
        /// <c>docs/netplay/02-delivery-method-reference.md</c>.
        ///
        /// <para>Two of the four do not survive intact, and both are deliberate rather than
        /// discovered: <see cref="NetDelivery.ReliableUnordered"/> gains ordering it did not ask
        /// for, because Steam's reliable channel is always ordered — correct, and slower under
        /// loss. <see cref="NetDelivery.ReliableSequenced"/> is mapped to reliable too, which
        /// delivers packets its sender thought were droppable; that is the safe direction of the
        /// two, and its one caller no longer takes it.</para>
        ///
        /// <para><b>NoNagle on the unreliable path only.</b> Continuous state is superseded by the
        /// next tick, so holding it to coalesce adds latency for nothing. Event traffic keeps Nagle
        /// on, which is the free batching the migration was promised.</para>
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

        /// <summary>
        /// Drops whichever connection id pointed at this peer handle. The id is found first and
        /// removed after: mutating a dictionary mid-enumeration happens to survive when the loop
        /// exits immediately, and relying on that is the kind of thing that stops being true when
        /// somebody adds a line below it.
        /// </summary>
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
        /// <b>Always -1 on this transport.</b> <c>GetConnectionRealTimeStatus</c> is the only source
        /// of ping and it takes a <c>SteamNetConnectionRealTimeStatus_t</c> by reference, whose
        /// IL2CPP layout carries an <c>Il2CppStructArray</c> where native has 64 inline bytes — the
        /// shape that has crashed this project twice. The route back is direct P/Invoke to the flat
        /// C API, which creates no second callback registry; see
        /// <c>docs/steamworks/05-interop-struct-shapes.md</c>.
        /// </summary>
        public int GetLatency(uint connectionId) => -1;

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

        /// <summary>
        /// <para>The pinned scratch buffer is deliberately <b>not</b> freed here. A session can be
        /// started again in the same process, the buffer is one 64 KB allocation, and freeing a
        /// pinned handle that a send is still inside is a far worse failure than holding it.</para>
        /// </summary>
        public void Shutdown()
        {
            if (state == SteamNetTransportState.Idle && statusCallback == null)
            {
                return;
            }

            // A connection that never reached Connected is not in peerSteamIds — ConnectP2P handed
            // back a handle and the status callback never followed. Closing it first, because the
            // loop below cannot see it and it would otherwise outlive the session.
            if (hostConnection != HSteamNetConnection.Invalid
                && !peerSteamIds.ContainsKey(hostConnection.m_HSteamNetConnection))
            {
                CloseRaw(hostConnection.m_HSteamNetConnection, SteamNetEndReason.SessionClosed,
                    "session ended", linger: false);
            }

            // Linger off here, unlike an individual disconnect: the session is over, nothing queued
            // is worth waiting for, and holding sockets open past teardown is how the next session
            // inherits a handle. A client's host connection is one of these entries, so closing it
            // separately afterwards would be closing it twice.
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

        /// <summary>
        /// <paramref name="linger"/> lets anything already queued leave before the connection goes.
        /// On for an individual close, off for session teardown — see <see cref="Shutdown"/>.
        /// </summary>
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

        private bool EnsureIsHost()
        {
            if (isHost != true)
            {
                Plugin.Log.LogWarning("[steam-net] Only the host can perform this action.");
                return false;
            }

            return state == SteamNetTransportState.Running;
        }

        private bool EnsureIsClient()
        {
            if (isHost != false)
            {
                Plugin.Log.LogWarning("[steam-net] Only a client can perform this action.");
                return false;
            }

            return state == SteamNetTransportState.Running;
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

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MegabonkTogether.Common.Messages;
using MemoryPack;
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
    internal class SteamNetTransport(ISteamService steamService) : ISteamNetTransport
    {
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
        public event Action<IGameNetworkMessage, uint> MessageReceived;

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
        }

        private void OnConnectionEnded(SteamConnectionStatusChange change)
        {
            var description = SteamNetLayout.DescribeEndReason(change.EndReason);

            peerSteamIds.Remove(change.Connection);
            ForgetPeerHandle(change.Connection);

            if (isHost == false && change.Connection == hostConnection.m_HSteamNetConnection)
            {
                hostConnection = HSteamNetConnection.Invalid;
            }

            // Always close our own side, even when the peer closed first, or the handle leaks.
            CloseRaw(change.Connection, 0, string.Empty);

            Plugin.Log.LogInfo($"[steam-net] Connection to {change.RemoteSteamId} ended: {description}.");
            PeerDisconnected?.Invoke(change.Connection, description);
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

            MessageReceived?.Invoke(deserialized, message.m_conn.m_HSteamNetConnection);
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
                        ToSteamFlags(delivery),
                        out long _);
                }
                else
                {
                    // Over the scratch buffer. Reliable fragments up to 512 KB, so this can still
                    // go — it just costs a one-off pin, which at this size is the least of it.
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

        private void ForgetPeerHandle(uint peerHandle)
        {
            foreach (var (connectionId, handle) in peerHandlesByConnectionId)
            {
                if (handle == peerHandle)
                {
                    peerHandlesByConnectionId.Remove(connectionId);
                    return;
                }
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

        public void Shutdown()
        {
            if (state == SteamNetTransportState.Idle && statusCallback == null)
            {
                return;
            }

            foreach (var handle in peerSteamIds.Keys)
            {
                CloseRaw(handle, SteamNetEndReason.SessionClosed, "session ended");
            }

            peerSteamIds.Clear();
            peerHandlesByConnectionId.Clear();

            try
            {
                if (hostConnection != HSteamNetConnection.Invalid)
                {
                    SteamNetworkingSockets.CloseConnection(hostConnection, SteamNetEndReason.SessionClosed, "session ended", false);
                }

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

        /// <summary>
        /// <para>The pinned scratch buffer is deliberately <b>not</b> freed here. A session can be
        /// started again in the same process, the buffer is one 64 KB allocation, and freeing a
        /// pinned handle that a send is still inside is a far worse failure than holding it.</para>
        /// </summary>
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

        private void CloseRaw(uint peerHandle, int endReason, string debugText)
        {
            try
            {
                // Linger on, so anything already queued gets a chance to leave before the socket
                // goes. Teardown of the whole session is the one place that does not linger.
                SteamNetworkingSockets.CloseConnection(
                    new HSteamNetConnection(peerHandle), endReason, debugText ?? string.Empty, true);
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

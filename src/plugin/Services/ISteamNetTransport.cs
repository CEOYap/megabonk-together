using MegabonkTogether.Common.Messages;
using System;
using System.Collections.Generic;

namespace MegabonkTogether.Services
{
    public enum SteamNetTransportState
    {
        /// <summary>Nothing started. The normal state outside a session.</summary>
        Idle,

        /// <summary>A listen socket is open, or a connection is in progress.</summary>
        Starting,

        /// <summary>Host: listening. Client: connected to the host.</summary>
        Running,

        /// <summary><see cref="ISteamNetTransport.DescribeStatus"/> says what went wrong.</summary>
        Failed,
    }

    /// <summary>
    /// The Steam datagram transport: <c>ISteamNetworkingSockets</c> behind
    /// <see cref="INetTransport"/>. Phase 4 of <c>docs/steamworks/00-migration-plan.md</c>.
    ///
    /// <para><b>Two id spaces, exactly as the LiteNetLib side has.</b> A <i>peer handle</i> is the
    /// raw <c>HSteamNetConnection</c>, and it is the direct analogue of LiteNetLib's
    /// <c>NetPeer.Id</c>: assigned by the transport, meaningful only locally, and the thing a
    /// received message arrives tagged with. A <i>connection id</i> is the game's own <c>uint</c>,
    /// the one that appears in message bodies, and the transport does not learn it until the peer
    /// introduces itself — which is why <see cref="AssignConnectionId"/> exists and is called by
    /// the session layer rather than discovered here. Keeping the two apart is the same discipline
    /// <see cref="INetTransport"/> imposes on its own signatures.</para>
    ///
    /// <para><b>Nothing here starts a session.</b> Deciding who hosts, finding the host's SteamID
    /// and running the handshake belong to the session layer; this owns sockets, connection state
    /// and bytes.</para>
    /// </summary>
    public interface ISteamNetTransport : INetTransport
    {
        SteamNetTransportState State { get; }

        /// <summary>
        /// Opens a listen socket and a poll group. Requires <see cref="SteamReadiness.Ready"/> —
        /// relay access <b>and</b> authentication — because connecting before authentication is
        /// available fails in a way that reads like a NAT problem.
        /// </summary>
        bool StartHost();

        /// <summary>Connects to a host by SteamID. Same readiness requirement as the host side.</summary>
        bool ConnectToHost(ulong hostSteamId);

        /// <summary>
        /// Pumps receive. Must be called every frame from Unity's <c>Update</c>, which is what makes
        /// every message handler main-thread — see the migration plan's "what gets better".
        ///
        /// <para>The connection lifecycle does <b>not</b> come through here. It arrives on the
        /// game's own callback pump, which the mod joins rather than races.</para>
        /// </summary>
        void Poll();

        /// <summary>
        /// Ties a peer handle to the game connection id that peer just introduced itself with.
        /// Until this is called, sends addressed by connection id cannot reach that peer.
        /// </summary>
        void AssignConnectionId(uint peerHandle, uint connectionId);

        /// <summary>Drops a peer's connection-id mapping without closing the connection.</summary>
        void ForgetConnectionId(uint connectionId);

        /// <summary>The SteamID behind a peer handle, or 0.</summary>
        ulong GetPeerSteamId(uint peerHandle);

        /// <summary>Peer handles currently connected.</summary>
        IReadOnlyList<uint> GetPeerHandles();

        /// <summary>Closes one peer's connection with a reason from <c>SteamNetEndReason</c>.</summary>
        void Disconnect(uint connectionId, int endReason, string debugText);

        /// <summary>Closes everything and releases the socket, poll group and callback.</summary>
        void Shutdown();

        /// <summary>
        /// A peer finished connecting. Carries the peer handle and its SteamID; the game connection
        /// id is not known yet.
        /// </summary>
        event Action<uint, ulong> PeerConnected;

        /// <summary>A peer's connection ended. Second argument is a description, not a code.</summary>
        event Action<uint, string> PeerDisconnected;

        /// <summary>
        /// A message arrived, tagged with the peer handle it came from. The handle plays the part
        /// <c>NetPeer.Id</c> plays on the LiteNetLib receive path, so the existing dispatch maps
        /// across unchanged.
        /// </summary>
        event Action<IGameNetworkMessage, uint> MessageReceived;

        string DescribeStatus();
    }
}

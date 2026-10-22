using OmniBonk.Common.Messages;
using System;
using System.Collections.Generic;

namespace OmniBonk.Services
{
    public enum SteamNetTransportState
    {
        Idle,

        Starting,

        /// <summary>Host: listening. Client: connected to the host.</summary>
        Running,

        /// <summary><see cref="ISteamNetTransport.DescribeStatus"/> says what went wrong.</summary>
        Failed,
    }

    /// <summary>
    /// <c>ISteamNetworkingSockets</c> behind <see cref="INetTransport"/>. Two id spaces: the local
    /// peer handle (<c>HSteamNetConnection</c>) and the game connection id, learned only when the
    /// peer introduces itself. Choosing a host is the session layer's job.
    /// </summary>
    public interface ISteamNetTransport : INetTransport
    {
        SteamNetTransportState State { get; }

        /// <summary>
        /// Requires <see cref="SteamReadiness.Ready"/> (relay and auth): missing auth fails like a
        /// NAT problem.
        /// </summary>
        bool StartHost();

        /// <summary>Same readiness requirement as the host side.</summary>
        bool ConnectToHost(ulong hostSteamId);

        /// <summary>
        /// Call every frame from <c>Update</c>, so handlers run on the main thread. Connection
        /// lifecycle arrives on the game's callback pump instead.
        /// </summary>
        void Poll();

        /// <summary>Until called, sends by connection id cannot reach that peer.</summary>
        void AssignConnectionId(uint peerHandle, uint connectionId);

        /// <summary>Does not close the connection.</summary>
        void ForgetConnectionId(uint connectionId);

        /// <summary>0 if unknown.</summary>
        ulong GetPeerSteamId(uint peerHandle);

        /// <summary>Host only. Only introduced peers count.</summary>
        bool AreAllPeersReady();

        /// <summary>Host only. Zero on a client.</summary>
        int GetCurrentReadyPeersCount();

        IReadOnlyList<uint> GetPeerHandles();

        /// <summary><paramref name="endReason"/> from <c>SteamNetEndReason</c>.</summary>
        void Disconnect(uint connectionId, int endReason, string debugText);

        void Shutdown();

        /// <summary>Peer handle and SteamID; the connection id is not known yet.</summary>
        event Action<uint, ulong> PeerConnected;

        /// <summary>Second argument is a description, not a code.</summary>
        event Action<uint, string> PeerDisconnected;

        string DescribeStatus();
    }
}

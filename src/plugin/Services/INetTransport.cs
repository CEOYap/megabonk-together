using OmniBonk.Common.Messages;

namespace OmniBonk.Services
{
    /// <summary>
    /// Transport-neutral send contract (LiteNetLib or Steam).
    /// <para>Peers are always identified by game <c>ConnectionId</c>, never a transport handle: mixing
    /// the two id spaces is silent (P1-1).</para>
    /// <para>Sends return void, so implementations must log failures themselves, throttled, naming the
    /// message type and target. Batching/Nagle is the implementation's business.</para>
    /// </summary>
    public interface INetTransport
    {
        /// <summary>Host only.</summary>
        void SendToAllClients<T>(T data, NetDelivery delivery) where T : IGameNetworkMessage;

        /// <summary>Host only, pre-serialized. Always supply <paramref name="messageTypeName"/> for the bandwidth counters.</summary>
        void SendToAllClients(byte[] data, NetDelivery delivery, string messageTypeName = null);

        /// <summary>Null delivery defaults to ReliableSequenced (newest only); pass one explicitly for one-shots.</summary>
        void SendToHost<T>(T data, NetDelivery? delivery = null) where T : IGameNetworkMessage;

        void SendToClient<T>(uint connectionId, T data) where T : IGameNetworkMessage;

        /// <summary><paramref name="excludedConnectionId"/> is the originating player, not the arrival peer.</summary>
        void SendToAllClientsExcept<T>(uint excludedConnectionId, T data) where T : IGameNetworkMessage;

        /// <summary>RTT in ms, or -1 when unknown.</summary>
        int GetLatency(uint connectionId);

        /// <summary>Free-form link summary for the bandwidth report.</summary>
        string DescribeLink(uint connectionId);

        /// <summary>Null until the role is decided.</summary>
        bool? IsHost();

        /// <summary>Host only; false on a client. Read by the map-confirm gate.</summary>
        bool AreAllPeersReady();

        /// <summary>Host only; zero on a client.</summary>
        int GetCurrentReadyPeersCount();

        bool IsHandlingConnection();

        /// <summary>On the contract because readiness gates on it; asking the wrong transport fails silently.</summary>
        bool HasAllPeersConnected();
    }
}

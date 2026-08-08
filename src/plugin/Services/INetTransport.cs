using MegabonkTogether.Common.Messages;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Everything the mod needs from a network transport, expressed without naming one.
    ///
    /// <para>This is the seam the Steamworks migration is built around: gameplay code sends through
    /// this interface, and swapping LiteNetLib for Steam datagrams becomes a matter of providing a
    /// second implementation rather than editing ~100 call sites again. Phase 1 of
    /// <c>docs/steamworks/00-migration-plan.md</c>.</para>
    ///
    /// <para><b>One id space.</b> Every method here identifies a peer by its game
    /// <c>ConnectionId</c> — the id that appears in message bodies and in <c>Player</c> records.
    /// Transport-native handles (LiteNetLib's <c>NetPeer</c> and its <c>NetPeer.Id</c>, later a
    /// <c>SteamNetworkingIdentity</c>) do not appear in this contract at all, and resolving between
    /// the two is the implementation's job. That is not tidiness: the previous
    /// <c>SendToAllClientsExcept(int netPlayerId, uint sender, …)</c> took one id from each space,
    /// and transposing them is silent — it already cost one wrong fix attempt on P1-1, where the
    /// mistake would have echoed a gold *delta* back to its originator and double-counted it. A
    /// single-id signature makes that unrepresentable.</para>
    ///
    /// <para><b>Host authority is assumed, not enforced here.</b> The <c>SendToAllClients*</c>
    /// methods are host-only and the implementation rejects them on a client; <see cref="SendToHost"/>
    /// is the mirror. See <see cref="IsHost"/>.</para>
    ///
    /// <para><b>Sends return void, and implementations must therefore report their own failures.</b>
    /// This is a deliberate constraint on implementers, not an oversight. LiteNetLib's
    /// <c>NetPeer.Send</c> returns nothing, but Steam's
    /// <see href="https://partner.steamgames.com/doc/api/ISteamNetworkingSockets">SendMessageToConnection</see>
    /// returns an <c>EResult</c> — so a Steam implementation *will* have failures to report that the
    /// LiteNetLib one never had, most obviously <c>k_EResultNoConnection</c> and
    /// <c>k_EResultLimitExceeded</c>. Threading a result type through ~100 call sites that would all
    /// ignore it buys nothing; the obligation instead is:</para>
    ///
    /// <list type="bullet">
    /// <item>A failed send must be logged, not swallowed.</item>
    /// <item>It must be <b>throttled</b>, with a count of what was suppressed. These paths run at up
    /// to 233 sends/s (see <c>PickupFollowingPlayer</c> in the bandwidth baseline), and a broken
    /// connection fails every one of them — an unthrottled log would bury the session in the
    /// symptom and cost more frame time than the sends did. `docs/netplay/04-performance-and-gc.md`
    /// is the standing rule that nothing logs per-message on a hot path.</item>
    /// <item>The log must name the message type and the target, because "send failed" without either
    /// is not actionable — the same reason <c>SendToAllClients(byte[], …)</c> carries
    /// <c>messageTypeName</c>.</item>
    /// </list>
    ///
    /// <para><b>What this interface deliberately does not model.</b> Batching and flush timing are
    /// the implementation's business. Steam applies Nagle by default and exposes <c>NoNagle</c> /
    /// <c>NoDelay</c> per send; LiteNetLib has no equivalent. Putting a "flush now" hint in this
    /// contract would give the LiteNetLib implementation a method it cannot honour and would invite
    /// call sites to tune a knob only one transport has. If a specific stream turns out to need
    /// immediate delivery under Steam, that belongs in the mapping next to
    /// <see cref="NetDelivery"/> — decided once, per delivery class — not at a hundred call
    /// sites.</para>
    /// </summary>
    public interface INetTransport
    {
        /// <summary>Host → every client.</summary>
        void SendToAllClients<T>(T data, NetDelivery delivery) where T : IGameNetworkMessage;

        /// <summary>
        /// Host → every client, pre-serialized. <paramref name="messageTypeName"/> exists only for
        /// the bandwidth counters: this overload takes bytes, so the message type is gone by the
        /// time it arrives, and the first baseline reported 95% of host traffic under a single
        /// "(pre-serialized)" bucket. Optional so a future caller cannot fail to compile, but it
        /// should always be supplied.
        /// </summary>
        void SendToAllClients(byte[] data, NetDelivery delivery, string messageTypeName = null);

        /// <summary>
        /// Client → host. A null <paramref name="delivery"/> means "the transport's default", which
        /// is <see cref="NetDelivery.ReliableSequenced"/> — a channel that guarantees only the most
        /// recent packet. Pass one explicitly for anything one-shot.
        /// </summary>
        void SendToHost<T>(T data, NetDelivery? delivery = null) where T : IGameNetworkMessage;

        /// <summary>Host → one client, identified by game connection id.</summary>
        void SendToClient<T>(uint connectionId, T data) where T : IGameNetworkMessage;

        /// <summary>
        /// Host → every client except the one that originated the message.
        ///
        /// <para><paramref name="excludedConnectionId"/> is the <b>originating player</b>, not the
        /// peer the packet arrived on. Those coincide in a star topology but are not the same
        /// concept, and the originator is the one that must not receive its own delta back.</para>
        /// </summary>
        void SendToAllClientsExcept<T>(uint excludedConnectionId, T data) where T : IGameNetworkMessage;

        /// <summary>Round-trip time to a peer in milliseconds, or -1 when unknown.</summary>
        int GetLatency(uint connectionId);

        /// <summary>Null until the session's role has been decided.</summary>
        bool? IsHost();
    }
}

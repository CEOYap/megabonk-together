using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Session-wide gameplay settings the host chose in the lobby. Every peer applies the host's
    /// values, never its own config: a setting that changes how weapons fire or which stage comes
    /// next has to be identical on every machine, because the host simulates its teammates' weapons
    /// and every peer loads its own copy of each stage.
    ///
    /// <para>Sent by the host just before <c>RunStarted</c>, on the same ReliableOrdered channel, so
    /// a client always holds the settings before its run begins.</para>
    ///
    /// <para><b>Fields are positional on the wire.</b> <see cref="EndlessEnabled"/> is carried now,
    /// unused, so the Endless phase does not have to add a field to a message peers already speak.
    /// Add new settings only as a new message.</para>
    /// </summary>
    [MemoryPackable]
    public partial class LobbySettings : IGameNetworkMessage
    {
        /// <summary>Lifts the game's 80-projectile ceiling on a zero-interval volley.</summary>
        public bool NoProjectileCap { get; set; }

        /// <summary>Reserved for Endless Run (docs/netplay/18). Not acted on yet.</summary>
        public bool EndlessEnabled { get; set; }
    }
}

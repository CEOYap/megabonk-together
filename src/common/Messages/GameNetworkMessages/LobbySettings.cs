using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// The host's lobby settings; every peer applies these, never its own config.
    /// <para>Sent just before <c>RunStarted</c> on the same ReliableOrdered channel.</para>
    /// <para><b>Fields are positional on the wire.</b> Add new settings only as a new message.</para>
    /// </summary>
    [MemoryPackable]
    public partial class LobbySettings : IGameNetworkMessage
    {
        /// <summary>Lifts the game's 80-projectile ceiling on a zero-interval volley.</summary>
        public bool NoProjectileCap { get; set; }

        /// <summary>Endless Run (docs/netplay/18): every stage change follows the host's route.</summary>
        public bool EndlessEnabled { get; set; }
    }
}

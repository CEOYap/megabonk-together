using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// P1-3: protocol handshake for the matchmaker path (the Steam path uses lobby metadata).
    /// Client resends until the host answers. Silence blocks Start; nobody is disconnected.
    /// ReliableOrdered: carries a string.
    /// </summary>
    [MemoryPackable]
    public partial class ProtocolHello : IGameNetworkMessage
    {
        public uint ConnectionId { get; set; }

        /// <summary><see cref="Protocol.Version"/> of the sender. The only field that is compared.</summary>
        public int ProtocolVersion { get; set; }

        /// <summary>For display only; not compared.</summary>
        public string ModVersion { get; set; }
    }
}

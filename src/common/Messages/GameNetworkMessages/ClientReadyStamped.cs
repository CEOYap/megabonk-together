using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Client to host: ready for the named round. Replaces <see cref="ClientInGameReady"/> (tag 1),
    /// whose missing stamp caused lobby-ready defect B. Carries <see cref="ConnectionId"/> since the
    /// transport has no trusted sender identity on the relay path.
    /// <para>New type, not new fields on tag 1: MemoryPack is positional, and widening a shipped message corrupts sessions silently.</para>
    /// </summary>
    [MemoryPackable]
    public partial class ClientReadyStamped : IGameNetworkMessage
    {
        public uint ConnectionId { get; set; }

        public uint SessionId { get; set; }

        public uint RoundId { get; set; }
    }
}

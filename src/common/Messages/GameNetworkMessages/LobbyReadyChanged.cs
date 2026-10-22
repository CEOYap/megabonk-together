using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Client → host: toggled lobby-ready state.
    /// <para>Do not merge with <c>ClientReadyStamped</c> (tag 72, level loaded); conflating them caused the lobby hang.</para>
    /// <para>Not a <c>Player</c> field: MemoryPack is positional, so widening tag 0 would break the wire.</para>
    /// </summary>
    [MemoryPackable]
    public partial class LobbyReadyChanged : IGameNetworkMessage
    {
        public uint ConnectionId { get; set; }

        public bool IsReady { get; set; }
    }
}

using MemoryPack;

namespace OmniBonk.Common.Messages
{
    [MemoryPackable]
    public partial class PlayerDisconnected : IGameNetworkMessage
    {
        public uint ConnectionId { get; set; }
    }
}

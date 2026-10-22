using MemoryPack;

namespace OmniBonk.Common.Messages
{
    [MemoryPackable]
    public partial class PlayerDied : IGameNetworkMessage
    {
        public uint PlayerId { get; set; }
    }
}

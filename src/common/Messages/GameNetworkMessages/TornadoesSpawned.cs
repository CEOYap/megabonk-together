using MemoryPack;

namespace OmniBonk.Common.Messages
{
    [MemoryPackable]
    public partial class TornadoesSpawned : IGameNetworkMessage
    {
        public int Amount { get; set; }
    }
}

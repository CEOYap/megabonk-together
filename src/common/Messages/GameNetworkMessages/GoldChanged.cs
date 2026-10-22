using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    [MemoryPackable]
    public partial class GoldChanged : IGameNetworkMessage
    {
        public int Amount { get; set; }
        public uint OwnerId { get; set; }
    }
}

using MemoryPack;

namespace OmniBonk.Common.Messages
{
    [MemoryPackable]
    public partial class StartingChargingShrine : IGameNetworkMessage
    {
        public uint ShrineNetplayId;
        public uint PlayerChargingId;
    }
}

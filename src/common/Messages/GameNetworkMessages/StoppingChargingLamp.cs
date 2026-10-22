using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    [MemoryPackable]
    public partial class StoppingChargingLamp : IGameNetworkMessage
    {
        public uint LampNetplayId { get; set; }
        public uint PlayerChargingId { get; set; }
    }
}

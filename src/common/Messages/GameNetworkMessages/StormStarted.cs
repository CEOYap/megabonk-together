using MemoryPack;

namespace OmniBonk.Common.Messages
{
    [MemoryPackable]
    public partial class StormStarted : IGameNetworkMessage
    {
        public float StormOverAtTime { get; set; }
    }
}

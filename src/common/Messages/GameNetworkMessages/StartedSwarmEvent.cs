using MemoryPack;

namespace OmniBonk.Common.Messages
{
    [MemoryPackable]
    public partial class StartedSwarmEvent : IGameNetworkMessage
    {
        public float Duration;
    }
}

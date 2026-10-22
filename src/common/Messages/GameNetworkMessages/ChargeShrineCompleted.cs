using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// OB-6. The host's decision that a charge shrine completed, so the reward opens together on
    /// every peer. One-shot, so reliable.
    /// </summary>
    [MemoryPackable]
    public partial class ChargeShrineCompleted : IGameNetworkMessage
    {
        public uint ShrineNetplayId { get; set; }
    }
}

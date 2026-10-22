using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// OB-4: round-stamped barrier release, so a stale repeat cannot close the next window.
    /// New type rather than new fields on <see cref="CloseEncounter"/> (tag 66): adding fields breaks the wire.
    /// </summary>
    [MemoryPackable]
    public partial class CloseEncounterStamped : IGameNetworkMessage
    {
        /// <summary>The host's per-run barrier session. Always non-zero on the wire.</summary>
        public uint SessionId { get; set; }

        /// <summary>The barrier round being released.</summary>
        public uint RoundId { get; set; }
    }
}

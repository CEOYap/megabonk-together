using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// A peer finished its reward window, stamped with the barrier round (SE-5).
    /// <para>A new type, not new fields on <see cref="EncounterClosed"/> (tag 65): MemoryPack is
    /// positional, so widening a shipped message silently corrupts mixed-build sessions. Tag 65 is still handled.</para>
    /// </summary>
    [MemoryPackable]
    public partial class EncounterClosedStamped : IGameNetworkMessage
    {
        public uint OwnerId { get; set; }

        /// <summary>The host's per-run barrier session; 0 until learned from a release.</summary>
        public uint SessionId { get; set; }

        /// <summary>The barrier round this peer believes it is reporting for.</summary>
        public uint RoundId { get; set; }
    }
}

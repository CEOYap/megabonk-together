using System.Collections.Generic;
using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Desync detector: the host's periodic view of the run; clients diff and log, never correct.
    /// <para>Off by default (<c>Diagnostics.LogDesync</c>); enable only when all peers run the same build.
    /// Reliable only: carries a dictionary, and LiteNetLib fragments reliable channels only.</para>
    /// </summary>
    [MemoryPackable]
    public partial class StateDigest : IGameNetworkMessage
    {
        /// <summary>Monotonic per host session, so a log line can name the digest it came from.</summary>
        public uint Sequence { get; set; }

        /// <summary>Entries in the host's enemy registry, keyed by the same ids the client uses.</summary>
        public int TrackedEnemies { get; set; }

        /// <summary>Entries in the host's pickup registry. See OB-5: merges alias two ids to one object.</summary>
        public int TrackedPickups { get; set; }

        /// <summary>Shared-experience barrier identity. A mismatch here is OB-21's phase slip.</summary>
        public uint BarrierSessionId { get; set; }

        public uint BarrierRoundId { get; set; }

        /// <summary>Shared XP keeps every peer on the same level; a difference means it did not.</summary>
        public int PlayerLevel { get; set; }

        /// <summary>The run's interactable counters by debug name: numUsed per row. OB-12.</summary>
        public Dictionary<string, int> InteractablesUsed { get; set; } = new Dictionary<string, int>();
    }
}

using System.Collections.Generic;
using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Desync detector. A periodic summary of the host's view of the run, which each client diffs
    /// against its own and logs where they disagree.
    ///
    /// <para><b>Why it exists.</b> Most divergence in this project has been found by comparing
    /// screenshots from two machines — OB-12's counters, OB-21's barrier rounds, OB-9's enemy health.
    /// Every field here is something that has drifted before, chosen so the next drift becomes a log
    /// line instead of an observation someone has to notice.</para>
    ///
    /// <para><b>Reports, never corrects.</b> Nothing on the receiving side writes any of these values
    /// back into the game. Papering over a mismatch would remove the evidence it exists to produce.</para>
    ///
    /// <para><b>Off by default</b>, behind <c>Diagnostics.LogDesync</c>, and sent only by a host that
    /// has it on. A peer on a build without this type cannot deserialize it, so it must only be
    /// enabled when every peer runs the same build — which is the only way it is used.</para>
    ///
    /// <para>Carries a dictionary, so it must travel on a reliable channel: LiteNetLib fragments
    /// reliable channels only.</para>
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

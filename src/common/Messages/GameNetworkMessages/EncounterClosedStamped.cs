using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// A peer reporting that it has finished its reward window, stamped with the barrier round the
    /// report belongs to.
    ///
    /// <para>Replaces <see cref="EncounterClosed"/> (union tag 65), which carries no round identity
    /// — the defect recorded as SE-5 in <c>docs/netplay/07-shared-experience-audit.md</c>. A report
    /// for a round that has already been released is indistinguishable from a report for the
    /// current one, so it counts toward the wrong round and can release it early.</para>
    ///
    /// <para><b>A new type rather than two new fields on <see cref="EncounterClosed"/>.</b>
    /// MemoryPack serializes positionally, so adding a field to a shipped message changes the
    /// layout of an existing union tag and corrupts sessions between peers on different builds
    /// silently rather than failing loudly. Appending a tag is the only safe wire change here.
    /// Tag 65 is left in place, unused by the sender and still handled by the receiver.</para>
    /// </summary>
    [MemoryPackable]
    public partial class EncounterClosedStamped : IGameNetworkMessage
    {
        public uint OwnerId { get; set; }

        /// <summary>
        /// The host's per-run barrier session. Zero means "this peer has not yet learned one",
        /// which is the normal state for a client's very first report — it only learns the value
        /// from a release it has seen.
        /// </summary>
        public uint SessionId { get; set; }

        /// <summary>The barrier round this peer believes it is reporting for.</summary>
        public uint RoundId { get; set; }
    }
}

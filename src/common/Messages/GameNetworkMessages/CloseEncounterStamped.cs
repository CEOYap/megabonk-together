using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// The host releasing the shared-experience barrier, stamped with the round being released.
    ///
    /// <para>Replaces <see cref="CloseEncounter"/> (union tag 66), which carries nothing at all. A
    /// release therefore cannot be addressed to its round, which is the mechanism behind OB-4: a
    /// second release generated for an already-finished round arrives at a peer that has since
    /// opened its <i>next</i> encounter window and closes it instantly, losing that pick.</para>
    ///
    /// <para>The stamp makes the release idempotent per round: a peer that has already applied
    /// <c>(SessionId, RoundId)</c> ignores a repeat instead of closing whatever window happens to
    /// be open. It is also how a client learns <see cref="SessionId"/> in the first place.</para>
    ///
    /// <para>New type rather than new fields on tag 66, for the reason given on
    /// <see cref="EncounterClosedStamped"/>.</para>
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

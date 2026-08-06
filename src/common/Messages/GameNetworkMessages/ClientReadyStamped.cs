using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Client → host. "I have reached the Ready state for the round you named."
    ///
    /// <para>Replaces <see cref="ClientInGameReady"/> (union tag 1), which carried only a connection
    /// id. Without a round stamp the host cannot tell a report for the level it is waiting on from
    /// one that raced ahead of its own transition — which is lobby-ready defect B: the early report
    /// is recorded, <c>ResetForNextLevel</c> then clears it, and a client that sends exactly once
    /// never reports again.</para>
    ///
    /// <para>Carries <see cref="ConnectionId"/> because our transport does not supply a sender
    /// identity the host can trust on both the direct and the relay path; the host still validates
    /// it against the participants it captured when it opened the round.</para>
    ///
    /// <para>New type rather than fields on tag 1: MemoryPack is positional, so widening a shipped
    /// message changes an existing tag's layout and corrupts sessions between builds silently.</para>
    /// </summary>
    [MemoryPackable]
    public partial class ClientReadyStamped : IGameNetworkMessage
    {
        public uint ConnectionId { get; set; }

        public uint SessionId { get; set; }

        public uint RoundId { get; set; }
    }
}

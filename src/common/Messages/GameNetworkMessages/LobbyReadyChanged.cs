using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Client → host. "I have toggled my lobby-ready state."
    ///
    /// <para><b>This is not <c>ClientReadyStamped</c> (tag 72) and must not be merged with it.</b>
    /// That message answers "I have finished loading this level" and belongs to the level-transition
    /// barrier owned by <c>ReadinessService</c>. This one answers "I am ready for the host to start
    /// the run", is toggleable, and only exists in the lobby before a run begins. Overloading one
    /// signal for both would recreate exactly the ambiguity that caused the lobby hang — a peer
    /// unable to tell which question a flag was answering.</para>
    ///
    /// <para><b>Why a message and not a field on <c>Player</c>.</b> <c>Player</c> is serialized
    /// inside <c>LobbyUpdates</c> (union tag 0). MemoryPack is positional, so widening it changes a
    /// shipped tag's layout and silently corrupts sessions between peers on different builds.
    /// Appending a tag is the only safe way to add state to the wire.</para>
    /// </summary>
    [MemoryPackable]
    public partial class LobbyReadyChanged : IGameNetworkMessage
    {
        public uint ConnectionId { get; set; }

        public bool IsReady { get; set; }
    }
}

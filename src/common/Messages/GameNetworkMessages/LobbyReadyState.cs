using System.Collections.Generic;
using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Host to clients: the authoritative lobby-ready set, sent whole (host included) so every
    /// message is a full correction and a lost one costs nothing.
    /// </summary>
    [MemoryPackable]
    public partial class LobbyReadyState : IGameNetworkMessage
    {
        public List<LobbyReadyEntry> Entries { get; set; } = new();
    }

    /// <summary>One member's lobby-ready state. Not a <c>Player</c> — see <see cref="LobbyReadyChanged"/>.</summary>
    [MemoryPackable]
    public partial class LobbyReadyEntry
    {
        public uint ConnectionId { get; set; }

        public bool IsReady { get; set; }
    }
}

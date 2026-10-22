using MemoryPack;
using System.Collections.Generic;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// The host's lobby roster by name; otherwise clients learn other clients' names only once a run starts.
    /// <para>ReliableOrdered (carries strings). Resent periodically for late joiners.</para>
    /// </summary>
    [MemoryPackable]
    public partial class LobbyRoster : IGameNetworkMessage
    {
        public List<LobbyRosterEntry> Entries { get; set; } = new();
    }

    [MemoryPackable]
    public partial class LobbyRosterEntry
    {
        public uint ConnectionId { get; set; }
        public string Name { get; set; }
    }
}

using MemoryPack;
using System.Collections.Generic;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// The host's list of who is in the lobby, by name. Names otherwise reach a client only through
    /// the pairwise <c>Introduced</c> handshake, which gives it the host's name and nobody else's,
    /// and the player records that do carry every name (<c>LobbyUpdates</c>) are only sent once a
    /// run is under way. So in the lobby each client showed the other clients as "Player".
    ///
    /// <para>Host to clients, ReliableOrdered: it carries strings. Sent when the roster changes and
    /// every few seconds while the lobby is open, so a client that connects after a change still
    /// gets it.</para>
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

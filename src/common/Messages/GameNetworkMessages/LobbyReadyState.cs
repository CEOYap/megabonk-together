using System.Collections.Generic;
using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Host → clients. The authoritative lobby-ready set, sent whole.
    ///
    /// <para><b>Whole set, not a delta.</b> The set is at most six entries and only changes when
    /// somebody presses a button, so a delta would save nothing measurable while adding the one
    /// failure mode this project keeps paying for: a peer that misses one update and stays wrong
    /// with nothing to correct it. Re-sending everything makes every message a full correction, and
    /// makes a lost one cost nothing.</para>
    ///
    /// <para>The host is included in its own broadcast. It is a lobby member like any other, and a
    /// client that had to special-case "everyone except the host, plus the host from somewhere
    /// else" would be reassembling a set the host already has.</para>
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

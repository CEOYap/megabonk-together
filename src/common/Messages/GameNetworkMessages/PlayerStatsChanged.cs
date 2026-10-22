using System.Collections.Generic;
using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// OB-1. A player's global stats, so peers draw that player's attacks with their numbers;
    /// <c>PlayerStats.GetStat</c> is static and reads the observer's.
    /// <para>A dictionary, not fields: MemoryPack is positional, so new fields break the wire; new
    /// keys do not. Keyed by game <c>EStat</c> as int (common cannot see game types). Sent rarely.</para>
    /// </summary>
    [MemoryPackable]
    public partial class PlayerStatsChanged : IGameNetworkMessage
    {
        public uint OwnerId { get; set; }

        /// <summary>Keyed by <c>EStat</c> as int. An absent key means unknown, not zero.</summary>
        public Dictionary<int, float> Stats { get; set; } = new();
    }
}

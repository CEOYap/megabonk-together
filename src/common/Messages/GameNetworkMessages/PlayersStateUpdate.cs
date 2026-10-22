using OmniBonk.Common.Models;
using MemoryPack;

namespace OmniBonk.Common.Messages
{
    /// <summary>
    /// The per-frame half of the player stream; the full <see cref="Player"/> record goes out far
    /// less often on <c>LobbyUpdates</c>.
    /// <para>A new message, not a trimmed <see cref="Player"/>: MemoryPack is positional, so
    /// changing an existing message's fields corrupts mixed-version sessions silently.</para>
    /// <para>Applying it must not touch identity or readiness (lobby-ready defect C).
    /// <c>Hp</c>/<c>Shield</c> stay <c>uint</c>: late game can exceed 65535.</para>
    /// </summary>
    [MemoryPackable]
    public partial class PlayersStateUpdate : IGameNetworkMessage
    {
        public ICollection<PlayerState> States { get; set; } = new List<PlayerState>();
    }

    /// <summary><c>MaxHp</c>/<c>MaxShield</c> ride the full record instead.</summary>
    [MemoryPackable]
    public partial class PlayerState
    {
        public uint ConnectionId { get; set; }
        public QuantizedVector3 Position { get; set; } = new();
        public AnimatorState AnimatorState { get; set; } = new();
        public MovementState MovementState { get; set; } = new();
        public uint Hp { get; set; }
        public uint Shield { get; set; }
    }
}

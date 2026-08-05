using MegabonkTogether.Common.Models;
using MemoryPack;

namespace MegabonkTogether.Common.Messages
{
    /// <summary>
    /// The continuous half of the player stream: what changes every frame and nothing else.
    ///
    /// <para><b>Why this exists.</b> <c>SendLobbyUpdate</c> broadcasts the whole <see cref="Player"/>
    /// record at 60 Hz. A level-159 capture and a Graveyard capture both measured that stream at a
    /// flat <b>~20 KB/s at two players</b> (min 19.98, max 20.05, 341 B/send) — 65-90% of all host
    /// egress, and almost none of it changing. <c>Name</c>, <c>Skin</c>, <c>Character</c>,
    /// <c>ConnectionId</c> and <c>IsHost</c> are fixed for a run; <c>Inventory</c>, <c>MaxHp</c> and
    /// <c>MaxShield</c> change a few times a minute. All of it was being retransmitted sixty times a
    /// second.</para>
    ///
    /// <para><b>Why a new message rather than trimming <see cref="Player"/>.</b> MemoryPack
    /// serializes positionally and peers on different mod versions still handshake, so removing or
    /// reordering a field on an existing message corrupts sessions silently instead of failing
    /// loudly — the standing rule in <c>CLAUDE.md</c>. Adding a union tag is the sanctioned path, so
    /// <see cref="Player"/> and <c>LobbyUpdates</c> are untouched: the full record keeps going out on
    /// the same message as before, just far less often.</para>
    ///
    /// <para><b>The correctness bonus.</b> Applying this message must not touch identity or
    /// readiness, which means the 60 Hz stream can no longer clobber <c>IsReady</c> — defect C of
    /// the four lobby-ready barrier defects, where <c>OnLobbyUpdate</c> overwrote the whole
    /// <c>Player</c> record sixty times a second. That defect is a consequence of the two concerns
    /// sharing a message, so splitting them removes it rather than guarding it.</para>
    ///
    /// <para><b>Deliberately still <c>uint</c>:</b> <c>Hp</c> and <c>Shield</c> would fit a
    /// <c>ushort</c> for most of a run and save four bytes per player per tick, but late-game
    /// stacking can carry them past 65535 and a truncation here would be a silent wrong health bar.
    /// Not worth four bytes.</para>
    /// </summary>
    [MemoryPackable]
    public partial class PlayersStateUpdate : IGameNetworkMessage
    {
        public ICollection<PlayerState> States { get; set; } = new List<PlayerState>();
    }

    /// <summary>
    /// Per-player continuous state. <c>MaxHp</c>/<c>MaxShield</c> are deliberately absent: they
    /// change on level-up, so they ride the full record instead. The cost is that a remote health
    /// bar can show a stale maximum until the next full record — bounded by the full-record
    /// interval, and invisible next to the bandwidth it saves.
    /// </summary>
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

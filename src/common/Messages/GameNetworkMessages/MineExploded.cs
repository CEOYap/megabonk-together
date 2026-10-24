using OmniBonk.Common.Models;
using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// A weapon mine exploded on the host (<c>ProjectileMines.Explode</c>), so clients draw the same
    /// explosion. A client cannot tell from <c>ProjectileDone</c> alone: most mines time out without
    /// exploding, and its own enemy check fired for only 77-88% of the host's (OB-58, 2026-10-24).
    /// <para>Host to all clients. Self-contained (position and scale), so it does not matter whether the
    /// client's copy of the mine still exists. ReliableUnordered: a one-shot that needs no ordering.
    /// Protocol 6.</para>
    /// </summary>
    [MemoryPackable]
    public partial class MineExploded : IGameNetworkMessage
    {
        public QuantizedVector3 Position { get; set; } = new();

        /// <summary>The mine's <c>projectileRadius</c>, which <c>Explode</c> applies as the explosion's uniform scale.</summary>
        public float Scale { get; set; }
    }
}

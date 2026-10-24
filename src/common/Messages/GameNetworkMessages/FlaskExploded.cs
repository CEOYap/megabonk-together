using OmniBonk.Common.Models;
using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// A weapon Poison Flask exploded on the host (<c>ProjectilePoisonFlask.ExplodeFlask</c>), so clients
    /// draw the same explosion. A client's own copy used to explode on its own collisions, and again on
    /// each later one, since the <c>ProjectileDone</c> that ends it is the host's.
    /// <para>Host to all clients. ReliableUnordered: a one-shot. <see cref="ProjectileId"/> names the
    /// client's copy, whose <c>effect</c> child is played; a copy already removed draws nothing.
    /// Protocol 7.</para>
    /// </summary>
    [MemoryPackable]
    public partial class FlaskExploded : IGameNetworkMessage
    {
        /// <summary>The host's projectile id, as in <c>SpawnedProjectile.Id</c> and <c>ProjectileDone</c>.</summary>
        public uint ProjectileId { get; set; }

        public QuantizedVector3 Position { get; set; } = new();

        /// <summary>The <c>effect</c>'s local scale <c>ExplodeFlask</c> set: its default size times attack size.</summary>
        public float ScaleX { get; set; }

        public float ScaleY { get; set; }

        public float ScaleZ { get; set; }
    }
}

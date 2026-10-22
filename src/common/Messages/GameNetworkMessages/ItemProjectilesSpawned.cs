using OmniBonk.Common.Models;
using MemoryPack;
using System.Collections.Generic;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Item projectiles (Soul Harvester ghosts, Energy Core orbs, Ghost and Bob ghosts) spawned on the
    /// machine that ran the item, so other peers can draw damage-free copies. Without it only that
    /// machine ever saw them (2026-10-23).
    /// <para>Sent by whoever ran the item: the host to all clients, a client to the host, which draws
    /// them and relays to the other clients. One batch per frame.</para>
    /// <para>ReliableUnordered: it carries a list (fragmentation needs a reliable channel) and each
    /// entry is an independent one-shot. Protocol 5.</para>
    /// </summary>
    [MemoryPackable]
    public partial class ItemProjectilesSpawned : IGameNetworkMessage
    {
        public List<ItemProjectileSpawn> Spawns { get; set; } = new();
    }

    /// <summary>The values <c>ItemProjectile.Set</c> computed on the sender, so the copy flies the same.</summary>
    [MemoryPackable]
    public partial class ItemProjectileSpawn
    {
        /// <summary><see cref="ItemProjectileKind"/>.</summary>
        public byte Kind { get; set; }

        public uint OwnerId { get; set; }

        public QuantizedVector3 Position { get; set; } = new();

        /// <summary>Unit vector, so plain floats: position quantization is scaled to the world.</summary>
        public float DirectionX { get; set; }

        public float DirectionY { get; set; }

        public float DirectionZ { get; set; }

        public float Speed { get; set; }

        /// <summary>Seconds left to live when sent.</summary>
        public float Lifetime { get; set; }

        public float UpTime { get; set; }

        public float Range { get; set; }

        public int ProjectileIndex { get; set; }

        public int ProjectileCount { get; set; }

        /// <summary>The item's damage source, which also keys its hit effect.</summary>
        public string DamageSource { get; set; } = "";
    }

    /// <summary>Which <c>PoolManager</c> pool the projectile came from. Append-only.</summary>
    public enum ItemProjectileKind : byte
    {
        AngrySoul = 0,
        Orb = 1,
        Ghost = 2,
    }
}

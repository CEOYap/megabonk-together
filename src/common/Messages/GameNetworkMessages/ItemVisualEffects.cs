using MemoryPack;
using System.Collections.Generic;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// OB-62. Item visuals drawn on the machine that ran the item, for everyone else to replay: pooled
    /// one-shot effects, item hit sparks, <c>EffectManager</c> item effects, player-attached item
    /// effects (gloves, Electric Plug, Toxic Barrel) and Slutty Cannon rockets. Visual only on the
    /// receiver.
    /// <para>The host sends what it ran to all clients. A client sends what it ran (owner-run items) to
    /// the host, which draws it and relays it to the other clients. One batch per frame.
    /// ReliableUnordered: carries a list, and each entry is a one-shot. Protocol 9.</para>
    /// </summary>
    [MemoryPackable]
    public partial class ItemVisualEffects : IGameNetworkMessage
    {
        /// <summary>The machine that ran these, for the host's relay.</summary>
        public uint SenderId { get; set; }

        /// <summary>Hit spark sources, indexed by <see cref="ItemVisualEntry.Source"/>.</summary>
        public List<string> Sources { get; set; } = new();

        public List<ItemVisualEntry> Entries { get; set; } = new();
    }

    /// <summary>One item visual. Which fields mean what depends on <see cref="Kind"/>.</summary>
    [MemoryPackable]
    public partial class ItemVisualEntry
    {
        /// <summary>The <c>ItemVisualKind</c> in the plugin.</summary>
        public byte Kind { get; set; }

        /// <summary>A pool, a player effect, or hit spark flags, by kind.</summary>
        public byte Sub { get; set; }

        /// <summary>Index into <see cref="ItemVisualEffects.Sources"/> for a hit spark.</summary>
        public ushort Source { get; set; }

        /// <summary>The player whose item this is.</summary>
        public uint Owner { get; set; }

        /// <summary>A rocket's id on the sender.</summary>
        public uint Id { get; set; }

        /// <summary>A rocket's target enemy id, 0 for none.</summary>
        public uint Target { get; set; }

        /// <summary>World-quantized position.</summary>
        public short Px { get; set; }

        public short Py { get; set; }

        public short Pz { get; set; }

        /// <summary>Rotation as a quaternion, each component x127; a direction in X..Z for sparks and mirrors.</summary>
        public sbyte Qx { get; set; }

        public sbyte Qy { get; set; }

        public sbyte Qz { get; set; }

        public sbyte Qw { get; set; }

        /// <summary>Local scale, or a single size in <see cref="Sx"/>.</summary>
        public float Sx { get; set; }

        public float Sy { get; set; }

        public float Sz { get; set; }
    }
}

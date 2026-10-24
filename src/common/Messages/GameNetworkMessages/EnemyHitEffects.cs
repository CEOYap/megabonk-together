using MemoryPack;
using System.Collections.Generic;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Host to clients: the weapon hit sparks the host drew since the last flush, one entry per
    /// <c>EffectManager.EnemyHitEffect(..., EWeapon, ...)</c> call with <c>hitEnemy</c> set (OB-60).
    /// A client draws exactly these, since it skips the hit paths of what it receives.
    /// <para>Parallel lists: entry i is <see cref="Weapons"/>[i], <see cref="Flags"/>[i], positions
    /// 3i..3i+2 (world-quantized shorts) and directions 3i..3i+2 (x127). ReliableUnordered: it carries a
    /// list and nothing supersedes it. Protocol 8.</para>
    /// </summary>
    [MemoryPackable]
    public partial class EnemyHitEffects : IGameNetworkMessage
    {
        /// <summary>Flag: the call passed the weapon attack's <c>prefabHit</c>, not null.</summary>
        public const byte WEAPON_PREFAB = 1;

        /// <summary>Flag: the call's <c>useSfx</c>.</summary>
        public const byte USE_SFX = 2;

        /// <summary><c>EWeapon</c>.</summary>
        public List<short> Weapons { get; set; } = new();

        public List<byte> Flags { get; set; } = new();

        public List<short> Positions { get; set; } = new();

        /// <summary>The call's moveDir, each component x127; may be zero.</summary>
        public List<sbyte> Directions { get; set; } = new();

        public void Clear()
        {
            Weapons.Clear();
            Flags.Clear();
            Positions.Clear();
            Directions.Clear();
        }

        public bool IsWellFormed()
        {
            var count = Weapons?.Count ?? -1;
            return count >= 0
                && Flags?.Count == count
                && Positions?.Count == count * 3
                && Directions?.Count == count * 3;
        }
    }
}

using MemoryPack;
using System.Collections.Generic;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Host to clients: one enemy tick's summed hits across many enemies, replacing one
    /// <see cref="EnemyDamaged"/> per enemy (1.0 hits per message at a 479-enemy swarm).
    /// <para>Parallel lists, one entry per index, so primitives serialize without per-entry headers;
    /// sources and attackers are per-batch tables indexed by byte. Every list has the count of
    /// <see cref="EnemyIds"/>; a batch where they disagree is dropped whole.</para>
    /// <para>Unreliable like the <see cref="EnemyDamaged"/> it replaces (HP is superseded by the enemy delta
    /// stream). The sender keeps each batch under <c>MAX_PACKET_SIZE_BYTES</c> and promotes any that is not.</para>
    /// </summary>
    [MemoryPackable]
    public partial class EnemyDamagedBatch : IGameNetworkMessage
    {
        /// <summary>Distinct damage sources in this batch, at most 256.</summary>
        public List<string> DamageSources { get; set; } = new();

        /// <summary>Distinct attacker connection ids in this batch, at most 256.</summary>
        public List<uint> AttackerIds { get; set; } = new();

        public List<uint> EnemyIds { get; set; } = new();

        /// <summary>Index into <see cref="AttackerIds"/>.</summary>
        public List<byte> AttackerIndices { get; set; } = new();

        /// <summary>Index into <see cref="DamageSources"/>.</summary>
        public List<byte> SourceIndices { get; set; } = new();

        public List<float> Damages { get; set; } = new();

        public List<int> DamagesBlockedByArmor { get; set; } = new();

        public List<float> ProcCoefficients { get; set; } = new();

        public List<float> Knockbacks { get; set; } = new();

        /// <summary><c>EDamageEffect</c>; 0 to 9 on build 21750826.</summary>
        public List<byte> DamageEffects { get; set; } = new();

        /// <summary><c>EElement</c>; 0 to 4 on build 21750826.</summary>
        public List<byte> DamageElements { get; set; } = new();

        /// <summary><c>DcFlags</c>, a bitmask; int so a new game flag cannot truncate.</summary>
        public List<int> DamageFlags { get; set; } = new();

        public List<bool> DamageIsCrits { get; set; } = new();

        public void Clear()
        {
            DamageSources.Clear();
            AttackerIds.Clear();
            EnemyIds.Clear();
            AttackerIndices.Clear();
            SourceIndices.Clear();
            Damages.Clear();
            DamagesBlockedByArmor.Clear();
            ProcCoefficients.Clear();
            Knockbacks.Clear();
            DamageEffects.Clear();
            DamageElements.Clear();
            DamageFlags.Clear();
            DamageIsCrits.Clear();
        }

        /// <summary>Receiver-side shape check: every list matches and every index is in range.</summary>
        public bool IsWellFormed()
        {
            var count = EnemyIds?.Count ?? -1;
            if (count < 0
                || DamageSources == null || AttackerIds == null
                || AttackerIndices?.Count != count || SourceIndices?.Count != count
                || Damages?.Count != count || DamagesBlockedByArmor?.Count != count
                || ProcCoefficients?.Count != count || Knockbacks?.Count != count
                || DamageEffects?.Count != count || DamageElements?.Count != count
                || DamageFlags?.Count != count || DamageIsCrits?.Count != count)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                if (AttackerIndices[i] >= AttackerIds.Count || SourceIndices[i] >= DamageSources.Count)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

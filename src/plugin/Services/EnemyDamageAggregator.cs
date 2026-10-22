using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Configuration;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OmniBonk.Services
{
    /// <summary>
    /// Host side, OB-34: sums applied hits per (enemy, attacker, source, flags) per enemy tick, instead
    /// of ~33,000 messages/s at high damage, then packs the sums for every enemy into a few
    /// <see cref="EnemyDamagedBatch"/> messages. Summing alone saved nothing at a 479-enemy swarm
    /// (1.0 hits per message, 1,128 msgs/s): with many enemies, each is hit about once per tick.
    /// <para>Decompiled (build 21750826): <c>Enemy.Damage</c> and <c>RunStats.OnEnemyDamaged</c> are
    /// linear in damage, so a sum replays the same. Flags are keyed because <c>(flags &amp; 5) == 5</c>
    /// skips armor. Clients see one summed damage number per tick; UNVERIFIED in play.</para>
    /// <para>Main thread only.</para>
    /// </summary>
    internal sealed class EnemyDamageAggregator
    {
        /// <summary>Same 100 B slack under the unreliable cap as <c>StateBroadcastService</c>'s chunker.</summary>
        private const int BATCH_BUDGET_BYTES = Tunables.MAX_PACKET_SIZE_BYTES - 100;

        /// <summary>Union tag, member count, and a 4 B count header for each of the 13 lists.</summary>
        private const int BATCH_ENVELOPE_BYTES = 2 + 13 * 4;

        /// <summary>One index across the parallel lists: 4+1+1+4+4+4+4+1+1+4+1.</summary>
        private const int BATCH_ENTRY_BYTES = 29;

        private const int BATCH_ATTACKER_BYTES = 4;

        private const int BATCH_STRING_HEADER_BYTES = 8;

        /// <summary>Indices are bytes.</summary>
        private const int BATCH_TABLE_LIMIT = byte.MaxValue + 1;

        private readonly struct Key : IEquatable<Key>
        {
            public readonly uint EnemyId;
            public readonly uint AttackerId;
            public readonly string DamageSource;
            public readonly int DamageFlags;

            public Key(uint enemyId, uint attackerId, string damageSource, int damageFlags)
            {
                EnemyId = enemyId;
                AttackerId = attackerId;
                DamageSource = damageSource;
                DamageFlags = damageFlags;
            }

            public bool Equals(Key other) =>
                EnemyId == other.EnemyId &&
                AttackerId == other.AttackerId &&
                DamageFlags == other.DamageFlags &&
                string.Equals(DamageSource, other.DamageSource, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() =>
                HashCode.Combine(EnemyId, AttackerId, DamageFlags, StringComparer.Ordinal.GetHashCode(DamageSource ?? string.Empty));
        }

        /// <summary>A struct, so a hit allocates nothing.</summary>
        private struct Bucket
        {
            public float Damage;
            public int DamageEffect;
            public int DamageBlockedByArmor;
            public float DamageProcCoefficient;
            public int DamageElement;
            public float DamageKnockback;
            public bool DamageIsCrit;
            public int Hits;
        }

        private readonly Dictionary<Key, Bucket> pending = new();
        private readonly HashSet<uint> pendingEnemies = new();

        // Reused across batches: send serializes before it returns.
        private readonly EnemyDamagedBatch batch = new();
        private readonly Dictionary<string, byte> sourceIndices = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, byte> attackerIndices = new();
        private int batchBytes = BATCH_ENVELOPE_BYTES;
        private int batchHits;

        private bool warnedNarrowing;

        /// <summary>
        /// Sums damage and armor-blocked, ORs crit, keeps max knockback; other fields are the first hit's.
        /// </summary>
        public void Add(
            uint enemyId,
            uint attackerId,
            string damageSource,
            float damage,
            int damageEffect,
            int damageBlockedByArmor,
            float damageProcCoefficient,
            int damageElement,
            int damageFlags,
            float damageKnockback,
            bool damageIsCrit)
        {
            ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(
                pending, new Key(enemyId, attackerId, damageSource, damageFlags), out var exists);

            if (!exists)
            {
                bucket.Damage = damage;
                bucket.DamageEffect = damageEffect;
                bucket.DamageBlockedByArmor = damageBlockedByArmor;
                bucket.DamageProcCoefficient = damageProcCoefficient;
                bucket.DamageElement = damageElement;
                bucket.DamageKnockback = damageKnockback;
                bucket.DamageIsCrit = damageIsCrit;
                bucket.Hits = 1;
                pendingEnemies.Add(enemyId);
                return;
            }

            bucket.Damage += damage;
            bucket.DamageBlockedByArmor += damageBlockedByArmor;
            bucket.DamageIsCrit |= damageIsCrit;
            if (damageKnockback > bucket.DamageKnockback)
            {
                bucket.DamageKnockback = damageKnockback;
            }
            bucket.Hits++;
        }

        /// <summary><paramref name="send"/> must serialize the batch before returning; it is reused.</summary>
        public void Flush(Action<EnemyDamagedBatch> send)
        {
            if (pending.Count == 0)
            {
                return;
            }

            // Cleared in finally so a throwing send cannot resend buckets next tick.
            try
            {
                foreach (var (key, bucket) in pending)
                {
                    Append(key, bucket, send);
                }

                SendBatch(send);
            }
            finally
            {
                pending.Clear();
                pendingEnemies.Clear();
                ResetBatch();
            }
        }

        /// <summary>
        /// Call before sending <c>EnemyDied</c>: clients drop hits for enemies they no longer have. Flushes
        /// everything, not just this enemy, so a burst of deaths in one frame shares one batch.
        /// </summary>
        public void FlushIfPending(uint enemyId, Action<EnemyDamagedBatch> send)
        {
            if (pendingEnemies.Contains(enemyId))
            {
                Flush(send);
            }
        }

        public void Clear()
        {
            pending.Clear();
            pendingEnemies.Clear();
            ResetBatch();
        }

        private void Append(in Key key, in Bucket bucket, Action<EnemyDamagedBatch> send)
        {
            var source = key.DamageSource ?? string.Empty;

            if (batch.EnemyIds.Count > 0 && !Fits(source, key.AttackerId))
            {
                SendBatch(send);
                ResetBatch();
            }

            batchBytes += EntryCost(source, key.AttackerId);
            batchHits += bucket.Hits;

            if (!sourceIndices.TryGetValue(source, out var sourceIndex))
            {
                sourceIndex = (byte)batch.DamageSources.Count;
                sourceIndices[source] = sourceIndex;
                batch.DamageSources.Add(source);
            }

            if (!attackerIndices.TryGetValue(key.AttackerId, out var attackerIndex))
            {
                attackerIndex = (byte)batch.AttackerIds.Count;
                attackerIndices[key.AttackerId] = attackerIndex;
                batch.AttackerIds.Add(key.AttackerId);
            }

            batch.EnemyIds.Add(key.EnemyId);
            batch.AttackerIndices.Add(attackerIndex);
            batch.SourceIndices.Add(sourceIndex);
            batch.Damages.Add(bucket.Damage);
            batch.DamagesBlockedByArmor.Add(bucket.DamageBlockedByArmor);
            batch.ProcCoefficients.Add(bucket.DamageProcCoefficient);
            batch.Knockbacks.Add(bucket.DamageKnockback);
            batch.DamageEffects.Add(ToByte(bucket.DamageEffect, "EDamageEffect"));
            batch.DamageElements.Add(ToByte(bucket.DamageElement, "EElement"));
            batch.DamageFlags.Add(key.DamageFlags);
            batch.DamageIsCrits.Add(bucket.DamageIsCrit);
        }

        private bool Fits(string source, uint attackerId)
        {
            if (!sourceIndices.ContainsKey(source) && sourceIndices.Count >= BATCH_TABLE_LIMIT)
            {
                return false;
            }

            if (!attackerIndices.ContainsKey(attackerId) && attackerIndices.Count >= BATCH_TABLE_LIMIT)
            {
                return false;
            }

            return batchBytes + EntryCost(source, attackerId) <= BATCH_BUDGET_BYTES;
        }

        /// <summary>
        /// Envelope, entry and string costs measured against MemoryPack 1.21.4: a string is its UTF-8
        /// bytes plus an 8 B header. A miss is caught by the sender's size check, not lost.
        /// </summary>
        private int EntryCost(string source, uint attackerId) =>
            BATCH_ENTRY_BYTES
            + (sourceIndices.ContainsKey(source) ? 0 : BATCH_STRING_HEADER_BYTES + Encoding.UTF8.GetByteCount(source))
            + (attackerIndices.ContainsKey(attackerId) ? 0 : BATCH_ATTACKER_BYTES);

        private void SendBatch(Action<EnemyDamagedBatch> send)
        {
            if (batch.EnemyIds.Count == 0)
            {
                return;
            }

            send(batch);
            BandwidthDiagnostics.RecordEnemyDamageBatch(batchHits, batch.EnemyIds.Count);
        }

        private void ResetBatch()
        {
            batch.Clear();
            sourceIndices.Clear();
            attackerIndices.Clear();
            batchBytes = BATCH_ENVELOPE_BYTES;
            batchHits = 0;
        }

        /// <summary>
        /// Both enums top out under 10 on build 21750826. Out of range sends 0 (None/Neutral), which
        /// only changes how a client draws the number; HP still comes from the enemy stream.
        /// </summary>
        private byte ToByte(int value, string enumName)
        {
            if ((uint)value <= byte.MaxValue)
            {
                return (byte)value;
            }

            if (!warnedNarrowing)
            {
                warnedNarrowing = true;
                Plugin.Log.LogWarning(
                    $"[enemy-damage] {enumName} value {value} does not fit EnemyDamagedBatch's byte; "
                    + "clients see it as 0. A game update probably added values.");
            }

            return 0;
        }
    }
}

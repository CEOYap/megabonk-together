using OmniBonk.Common.Messages;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OmniBonk.Services
{
    /// <summary>
    /// Host side, OB-34: sums applied hits into one <see cref="EnemyDamaged"/> per (enemy, attacker,
    /// source, flags) per enemy tick, instead of ~33,000 messages/s at high damage.
    /// <para>Decompiled (build 21750826): <c>Enemy.Damage</c> and <c>RunStats.OnEnemyDamaged</c> are
    /// linear in damage, so a sum replays the same. Flags are keyed because <c>(flags &amp; 5) == 5</c>
    /// skips armor. Clients see one summed damage number per tick; UNVERIFIED in play.</para>
    /// <para>No new message or field (wire format is append-only). Main thread only.</para>
    /// </summary>
    internal sealed class EnemyDamageAggregator
    {
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

        private struct Bucket
        {
            public EnemyDamaged Message;
            public int Hits;
        }

        private readonly Dictionary<Key, Bucket> pending = new();
        private readonly List<Key> keysToFlush = new();

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
                bucket.Message = new EnemyDamaged
                {
                    EnemyId = enemyId,
                    AttackerId = attackerId,
                    DamageSource = damageSource,
                    DamageFlags = damageFlags,
                    Damage = damage,
                    DamageEffect = damageEffect,
                    DamageBlockedByArmor = damageBlockedByArmor,
                    DamageProcCoefficient = damageProcCoefficient,
                    DamageElement = damageElement,
                    DamageKnockback = damageKnockback,
                    DamageIsCrit = damageIsCrit,
                };
                bucket.Hits = 1;
                return;
            }

            var message = bucket.Message;
            message.Damage += damage;
            message.DamageBlockedByArmor += damageBlockedByArmor;
            message.DamageIsCrit |= damageIsCrit;
            if (damageKnockback > message.DamageKnockback)
            {
                message.DamageKnockback = damageKnockback;
            }
            bucket.Hits++;
        }

        public void Flush(Action<EnemyDamaged> send)
        {
            if (pending.Count == 0)
            {
                return;
            }

            // Cleared in finally so a throwing send cannot resend buckets next tick.
            try
            {
                foreach (var bucket in pending.Values)
                {
                    send(bucket.Message);
                    BandwidthDiagnostics.RecordEnemyDamageAggregated(bucket.Hits);
                }
            }
            finally
            {
                pending.Clear();
            }
        }

        /// <summary>Call before sending <c>EnemyDied</c>: clients drop hits for enemies they no longer have.</summary>
        public void FlushEnemy(uint enemyId, Action<EnemyDamaged> send)
        {
            if (pending.Count == 0)
            {
                return;
            }

            keysToFlush.Clear();
            foreach (var key in pending.Keys)
            {
                if (key.EnemyId == enemyId)
                {
                    keysToFlush.Add(key);
                }
            }

            foreach (var key in keysToFlush)
            {
                if (pending.Remove(key, out var bucket))
                {
                    send(bucket.Message);
                    BandwidthDiagnostics.RecordEnemyDamageAggregated(bucket.Hits);
                }
            }

            keysToFlush.Clear();
        }

        public void Clear()
        {
            pending.Clear();
            keysToFlush.Clear();
        }
    }
}

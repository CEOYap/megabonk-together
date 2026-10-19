using MegabonkTogether.Common.Messages;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Host side: sums the hits it applies into one <see cref="EnemyDamaged"/> per enemy, attacker,
    /// source and flags, and sends each sum once per enemy tick.
    ///
    /// <para><b>Why.</b> The host forwarded every hit it applied as its own message to every client.
    /// At god-mode damage on 2026-10-19 (OB-34) that was ~33,000 messages/s, 2.6 MB/s. A bucket
    /// bounds it at one message per (enemy, attacker, source) per tick, whatever the hit rate.</para>
    ///
    /// <para><b>Why a sum replays the same as the hits did.</b> Both things a replay feeds are
    /// linear in <c>damage</c>, by decompilation on build 21750826. <c>Enemy.Damage</c>
    /// (VA 0x1804AC7B0) multiplies the damage by a player stat and by an armor factor, subtracts it
    /// from HP and clamps; <c>RunStats.OnEnemyDamaged</c> (VA 0x180411930) adds it to the source's
    /// total. The armor factor is skipped when <c>(flags &amp; 5) == 5</c>, which is why the flags
    /// are part of the key and not merged: two hits that disagree on that would otherwise replay
    /// with one hit's armor treatment.</para>
    ///
    /// <para><b>What does change on a client.</b> One damage number per bucket per tick instead of
    /// one per hit, showing the sum; the hit flash already runs on a timer, so it looks the same.
    /// HP is unaffected either way, because <c>EnemyModel.Hp</c> on the enemy delta supersedes it.
    /// UNVERIFIED in play.</para>
    ///
    /// <para><b>Merge rules.</b> Damage and armor-blocked damage are summed, crit is kept if any hit
    /// critted, knockback is the largest, effect, element and proc coefficient are the first hit's.
    /// No new message and no new field: the wire format is append-only.</para>
    ///
    /// <para>Main thread only: hits arrive from <c>Enemy.Damage_Postfix</c> and the flush runs in
    /// <c>NetworkHandler.Update</c>.</para>
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
        /// Adds one applied hit. Allocates a message only for the first hit in a bucket; later hits
        /// merge into it.
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

        /// <summary>Sends every pending bucket and empties the buffer. Called once per enemy tick.</summary>
        public void Flush(Action<EnemyDamaged> send)
        {
            if (pending.Count == 0)
            {
                return;
            }

            // Cleared in a finally so a send that throws cannot leave buckets behind to go out a
            // second time next tick. The rest of that tick is lost instead, which an unreliable
            // message whose HP effect the enemy delta supersedes can afford.
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

        /// <summary>
        /// Sends one enemy's pending buckets now. The host calls this just before it sends that
        /// enemy's <c>EnemyDied</c>: a client drops a hit for an enemy it no longer has, so without
        /// this an enemy killed within a tick of its first hits would reach the clients' damage
        /// panels with none of them — most of a swarm.
        /// </summary>
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

        /// <summary>Drops everything pending, for a stage change or a session teardown.</summary>
        public void Clear()
        {
            pending.Clear();
            keysToFlush.Clear();
        }
    }
}

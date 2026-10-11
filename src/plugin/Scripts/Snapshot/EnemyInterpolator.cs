using Assets.Scripts.Actors.Enemies;
using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Scripts.Snapshot
{
    //TODO: Find a way to make abstract class work with IL2CPP because its not working for some reason ¯\_(ツ)_/¯
    public class EnemyInterpolator : MonoBehaviour
    {
        private Enemy enemy;

        private readonly List<EnemySnapshot> snapshotsBuffer = new List<EnemySnapshot>();

        protected float interpolationDelayMs = 0.1f;
        protected int maxBufferSize = 200;

        /// <summary>
        /// Position in <see cref="EnemyInterpolatorManager"/>'s registry, or -1 when unregistered.
        /// Stored here so removal is O(1) rather than a linear search.
        /// </summary>
        internal int RegistryIndex { get; set; } = -1;

        /// <summary>
        /// PERF: was <c>Update()</c>. Driven by <see cref="EnemyInterpolatorManager"/> instead, so
        /// the managed↔native crossing happens once per frame for all enemies rather than once per
        /// enemy — the same change PERF 1A made for target switchers, on the client this time.
        /// The body is unchanged apart from taking the render time as a parameter.
        /// </summary>
        internal void Tick(double now)
        {
            if (!HasEnoughSnapshots())
                return;

            double renderTime = now - interpolationDelayMs;
            PerformInterpolation(renderTime);
            CleanupOldSnapshots(renderTime);
        }

        private void OnEnable()
        {
            EnemyInterpolatorManager.Register(this);
        }

        private void OnDisable()
        {
            EnemyInterpolatorManager.Unregister(this);
        }

        private void OnDestroy()
        {
            EnemyInterpolatorManager.Unregister(this);
        }

        /// <summary>
        /// Whether this enemy can be shown in the boss health bar. Cached once: an enemy's flags
        /// do not change after spawn, and this is read on every interpolation step.
        /// </summary>
        private bool raisesHealthChange;

        public void Initialize(Enemy enemy)
        {
            this.enemy = enemy;
            raisesHealthChange = enemy != null
                && (enemy.enemyFlag & Assets.Scripts.Actors.Enemies.EEnemyFlag.AnyBoss) != 0;

            // Belt-and-braces, exactly as TargetSwitcher.StartSwitching does: OnEnable is the
            // intended hook, but if Il2CppInterop does not wire it, nothing would ever register
            // and every remote enemy would freeze in place — a silent, total failure. Register is
            // idempotent, so the overlap costs nothing.
            EnemyInterpolatorManager.Register(this);
        }

        public void AddSnapshot(EnemySnapshot snapshot)
        {
            // The enemy stream is `Unreliable, chunked` at 40 Hz, so it has no ordering guarantee
            // and a late chunk can arrive after a newer one. Everything below this line assumes the
            // buffer is sorted by timestamp: FindSnapshotPair scans linearly for a bracketing pair,
            // and the prune drops from the FRONT. An out-of-order insert breaks both.
            //
            // It also un-kills enemies. PerformInterpolation writes `enemy.hp = newer.Hp`
            // unconditionally, so a stale pair restores health the enemy has already lost -- the
            // client sees a health bar jump back up, and with HP_TRESHOLD = 1 driving the delta
            // stream that stale value can persist until the next change.
            //
            // Dropping a stale snapshot is what `Sequenced` would do at the transport level, and it
            // is the correct answer for state that is superseded every tick: the next snapshot is
            // 25 ms away.
            if (snapshotsBuffer.Count > 0
                && snapshot.Timestamp <= snapshotsBuffer[snapshotsBuffer.Count - 1].Timestamp)
            {
                return;
            }

            snapshotsBuffer.Add(snapshot);

            if (snapshotsBuffer.Count > maxBufferSize)
            {
                snapshotsBuffer.RemoveAt(0);
            }
        }

        protected bool HasEnoughSnapshots()
        {
            return snapshotsBuffer.Count >= 2;
        }

        protected void PerformInterpolation(double renderTime)
        {
            if (!FindSnapshotPair(renderTime, out EnemySnapshot older, out EnemySnapshot newer))
                return;

            var previousHp = enemy.hp;
            enemy.hp = newer.Hp;

            // The boss health bar never polls HP. TargetOfInterestPrefab.Update only fades and
            // redraws debuffs; UpdateHp (the text and the bar) runs only from its OnDamage and
            // OnHealthChange handlers, subscribed in Awake to the static Enemy.A_Damage and
            // Enemy.A_HealthChange (all decompiled). A client applies a boss's HP here, by direct
            // write, so neither event fired and the bar showed the spawn value for the whole fight
            // -- reported 2026-10-11 as "6.2m / 6.2m" on the client against 7.9m / 8.4m on the
            // host, and read as the client being unable to damage the boss.
            //
            // A_HealthChange is the event the game itself raises when health moves outside a hit,
            // which is what this is. Bosses only, and only on a change: it is a global event and
            // this runs for every remote enemy at the interpolation rate.
            if (raisesHealthChange && previousHp != newer.Hp)
            {
                Enemy.A_HealthChange?.Invoke(enemy);
            }

            float dist = Vector3.Distance(older.Position, newer.Position);
            if (dist > 2.0f)
            {
                enemy.transform.position = newer.Position;
                enemy.transform.rotation = newer.Rotation;
                return;
            }

            float t = CalculateInterpolationFactor(renderTime, older.Timestamp, newer.Timestamp);
            t = Mathf.Clamp01(t);

            if (enemy.transform == null)
            {
                return;
            }

            enemy.transform.position = Vector3.Lerp(older.Position, newer.Position, t);
            enemy.transform.rotation = Quaternion.Slerp(older.Rotation, newer.Rotation, t);
        }

        private bool FindSnapshotPair(double renderTime, out EnemySnapshot older, out EnemySnapshot newer)
        {
            older = null;
            newer = null;

            for (int i = 0; i < snapshotsBuffer.Count - 1; i++)
            {
                if (snapshotsBuffer[i].Timestamp <= renderTime &&
                    snapshotsBuffer[i + 1].Timestamp >= renderTime)
                {
                    older = snapshotsBuffer[i];
                    newer = snapshotsBuffer[i + 1];
                    return true;
                }
            }

            return false;
        }

        private float CalculateInterpolationFactor(double renderTime, double olderTime, double newerTime)
        {
            return (float)((renderTime - olderTime) / (newerTime - olderTime));
        }

        protected void CleanupOldSnapshots(double renderTime)
        {
            int removeCount = 0;
            while (removeCount < snapshotsBuffer.Count - 2 &&
                   snapshotsBuffer[removeCount].Timestamp < renderTime - interpolationDelayMs)
            {
                removeCount++;
            }

            if (removeCount > 0)
            {
                snapshotsBuffer.RemoveRange(0, removeCount);
            }
        }
    }
}

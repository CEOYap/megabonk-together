using Assets.Scripts.Actors.Enemies;
using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Scripts.Snapshot
{
    //TODO: Find a way to make abstract class work with IL2CPP because its not working for some reason ¯\_(ツ)_/¯
    public class EnemyInterpolator : MonoBehaviour
    {
        private Enemy enemy;

        private readonly List<EnemySnapshot> snapshotsBuffer = new List<EnemySnapshot>();

        protected float interpolationDelayMs = 0.1f;
        protected int maxBufferSize = 200;

        /// <summary>Index in the manager's registry for O(1) removal; -1 when unregistered.</summary>
        internal int RegistryIndex { get; set; } = -1;

        /// <summary>PERF: driven by <see cref="EnemyInterpolatorManager"/> instead of a per-enemy Update.</summary>
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

        /// <summary>Boss flag, cached at init.</summary>
        private bool raisesHealthChange;

        public void Initialize(Enemy enemy)
        {
            this.enemy = enemy;
            raisesHealthChange = enemy != null
                && (enemy.enemyFlag & Assets.Scripts.Actors.Enemies.EEnemyFlag.AnyBoss) != 0;

            // Idempotent backup in case Il2CppInterop does not wire OnEnable.
            EnemyInterpolatorManager.Register(this);
        }

        /// <summary>OB-51 diagnostic. A long silence is a hint, not proof, that the host lost the enemy.</summary>
        public float LastSnapshotAt { get; private set; } = -1f;

        public void AddSnapshot(EnemySnapshot snapshot)
        {
            LastSnapshotAt = Time.unscaledTime;

            // Unreliable stream: drop out-of-order snapshots. The buffer must stay sorted, and a stale
            // one would restore lost HP.
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

            // The boss bar only updates on A_Damage/A_HealthChange (decompiled), never by polling.
            // Bosses only, on change: it is a global event.
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

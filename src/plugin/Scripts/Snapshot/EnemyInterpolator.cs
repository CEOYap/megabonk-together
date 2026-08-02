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

        public void Initialize(Enemy enemy)
        {
            this.enemy = enemy;

            // Belt-and-braces, exactly as TargetSwitcher.StartSwitching does: OnEnable is the
            // intended hook, but if Il2CppInterop does not wire it, nothing would ever register
            // and every remote enemy would freeze in place — a silent, total failure. Register is
            // idempotent, so the overlap costs nothing.
            EnemyInterpolatorManager.Register(this);
        }

        public void AddSnapshot(EnemySnapshot snapshot)
        {
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

            enemy.hp = newer.Hp;

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

using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Scripts.Snapshot
{
    public class ProjectileInterpolator : MonoBehaviour
    {
        private readonly Dictionary<uint, GameObject> activeProjectiles = new Dictionary<uint, GameObject>();
        private readonly Dictionary<uint, List<ProjectileSnapshot>> snapshotsBuffers = new Dictionary<uint, List<ProjectileSnapshot>>();
        // FIX P2-5: id -> owner, so a departed peer's projectiles can be dropped. Sender id space,
        // separate from ProjectileManagerService's.
        private readonly Dictionary<uint, uint> projectileOwners = new Dictionary<uint, uint>();

        /// <summary>id -> earliest destroy time. See <see cref="UnregisterProjectile"/>.</summary>
        private readonly Dictionary<uint, double> pendingRemoval = new Dictionary<uint, double>();

        private readonly List<uint> expiredScratch = new List<uint>();

        private static bool loggedFirstExplosion;

        // Seconds, despite the name.
        protected float interpolationDelayMs = 0.1f;
        protected int maxBufferSize = 30;

        protected void Update()
        {
            double renderTime = Time.timeAsDouble - interpolationDelayMs;

            foreach (var projectileId in activeProjectiles.Keys)
            {
                if (!snapshotsBuffers.TryGetValue(projectileId, out var buffer))
                    continue;

                if (!HasEnoughSnapshots(buffer))
                    continue;

                PerformInterpolation(projectileId, buffer, renderTime);
                CleanupOldSnapshots(buffer, renderTime);
            }

            if (pendingRemoval.Count > 0)
            {
                SweepPendingRemovals();
            }
        }

        private void SweepPendingRemovals()
        {
            var now = Time.timeAsDouble;

            expiredScratch.Clear();

            foreach (var kv in pendingRemoval)
            {
                if (kv.Value <= now)
                {
                    expiredScratch.Add(kv.Key);
                }
            }

            foreach (var id in expiredScratch)
            {
                PlayRocketExplosion(id);
                RemoveProjectileNow(id);
            }
        }

        /// <summary>
        /// The client never runs <c>Rocket.ProjectileDone</c> for a received rocket (RocketPatches), and
        /// that is where the explosion is drawn. Decompiled (0x1804F3FA0): one object from
        /// <c>PoolManager.explosionPool</c>, placed at the rocket's position plus its forward. Damage
        /// cannot leak: client <c>Enemy.Damage</c> is blocked and a weapon source is never forwarded.
        /// <b>UNVERIFIED</b> that the explosion prefab carries nothing beyond its visual.
        /// </summary>
        private void PlayRocketExplosion(uint id)
        {
            if (!activeProjectiles.TryGetValue(id, out var projectile) || projectile == null)
            {
                return;
            }

            var rocket = projectile.GetComponent<ProjectileRocket>()?.rocket;
            var pool = Assets.Scripts.Objects.Pooling.PoolManager.Instance?.explosionPool;
            if (rocket == null || pool == null || !rocket.gameObject.activeSelf)
            {
                return;
            }

            var explosion = pool.Get();
            if (explosion == null)
            {
                return;
            }

            var rocketTransform = rocket.transform;
            explosion.transform.position = rocketTransform.position + rocketTransform.forward;
            explosion.SetActive(true);

            if (!loggedFirstExplosion)
            {
                loggedFirstExplosion = true;
                Plugin.Log.LogInfo("[rocket] Drew the first explosion for a received rocket.");
            }
        }

        public void UpdateProjectiles(List<ProjectileSnapshot> projectileSnapshots)
        {
            if (projectileSnapshots == null || projectileSnapshots.Count == 0)
                return;

            foreach (var snapshot in projectileSnapshots)
            {
                AddSnapshot(snapshot);
            }
        }

        private void AddSnapshot(ProjectileSnapshot snapshot)
        {
            if (!snapshotsBuffers.TryGetValue(snapshot.Id, out var buffer))
            {
                buffer = new List<ProjectileSnapshot>();
                snapshotsBuffers[snapshot.Id] = buffer;
            }

            // Unreliable stream: drop out-of-order snapshots; the buffer must stay sorted.
            if (buffer.Count > 0 && snapshot.Timestamp <= buffer[buffer.Count - 1].Timestamp)
            {
                return;
            }

            buffer.Add(snapshot);

            if (buffer.Count > maxBufferSize)
            {
                buffer.RemoveAt(0);
            }
        }

        private bool HasEnoughSnapshots(List<ProjectileSnapshot> buffer)
        {
            return buffer.Count >= 2;
        }

        private void PerformInterpolation(uint projectileId, List<ProjectileSnapshot> buffer, double renderTime)
        {
            if (!activeProjectiles.TryGetValue(projectileId, out var projectile)) return;

            if (!FindSnapshotPair(buffer, renderTime, out ProjectileSnapshot older, out ProjectileSnapshot newer)) return;

            float t = CalculateInterpolationFactor(renderTime, older.Timestamp, newer.Timestamp);
            t = Mathf.Clamp01(t);

            InterpolateSnapshot(projectile, older, newer, t);
        }

        private bool FindSnapshotPair(List<ProjectileSnapshot> buffer, double renderTime, out ProjectileSnapshot older, out ProjectileSnapshot newer)
        {
            older = null;
            newer = null;

            for (int i = 0; i < buffer.Count - 1; i++)
            {
                if (buffer[i].Timestamp <= renderTime &&
                    buffer[i + 1].Timestamp >= renderTime)
                {
                    older = buffer[i];
                    newer = buffer[i + 1];
                    return true;
                }
            }

            return false;
        }

        private float CalculateInterpolationFactor(double renderTime, double olderTime, double newerTime)
        {
            return (float)((renderTime - olderTime) / (newerTime - olderTime));
        }

        private void InterpolateSnapshot(GameObject projectile, ProjectileSnapshot older, ProjectileSnapshot newer, float t)
        {
            var transform = GetProjectileTransform(projectile);
            if (transform == null)
                return;

            transform.position = Vector3.Lerp(older.Position, newer.Position, t);

            // LookRotation(zero) logs a warning per call and snaps to identity; keep the rotation.
            if (older.Rotation == Vector3.zero || newer.Rotation == Vector3.zero)
            {
                return;
            }

            var olderRot = Quaternion.LookRotation(older.Rotation, Vector3.up);
            var newerRot = Quaternion.LookRotation(newer.Rotation, Vector3.up);
            transform.rotation = Quaternion.Slerp(olderRot, newerRot, t);
        }

        private void CleanupOldSnapshots(List<ProjectileSnapshot> buffer, double renderTime)
        {
            int removeCount = 0;
            while (removeCount < buffer.Count - 2 &&
                   buffer[removeCount].Timestamp < renderTime - interpolationDelayMs)
            {
                removeCount++;
            }

            if (removeCount > 0)
            {
                buffer.RemoveRange(0, removeCount);
            }
        }

        public void RegisterProjectile(uint id, GameObject projectile, uint ownerId)
        {
            activeProjectiles[id] = projectile;
            projectileOwners[id] = ownerId;

            // Ids are recycled; cancel a pending removal of the old one.
            pendingRemoval.Remove(id);

            if (!snapshotsBuffers.ContainsKey(id))
            {
                snapshotsBuffers[id] = new List<ProjectileSnapshot>();
            }
        }

        /// <summary>On disconnect: no further snapshots will arrive for that peer.</summary>
        public void UnregisterProjectilesByOwner(uint ownerId)
        {
            List<uint> toRemove = null;

            foreach (var kv in projectileOwners)
            {
                if (kv.Value != ownerId)
                {
                    continue;
                }

                toRemove ??= new List<uint>();
                toRemove.Add(kv.Key);
            }

            if (toRemove == null)
            {
                return;
            }

            foreach (var id in toRemove)
            {
                RemoveProjectileNow(id);
            }
        }

        /// <summary>
        /// Destroys without calling <c>Rocket.ProjectileDone</c>: decompiled, it releases the child
        /// rocket to the pool, so destroying the parent after would pool a destroyed object.
        /// </summary>
        public void UnregisterProjectile(uint id)
        {
            if (!activeProjectiles.ContainsKey(id))
            {
                snapshotsBuffers.Remove(id);
                projectileOwners.Remove(id);
                pendingRemoval.Remove(id);
                return;
            }

            // Deferred by one interpolation delay, or the rendered copy dies short of its target.
            pendingRemoval[id] = Time.timeAsDouble + interpolationDelayMs;
        }

        /// <summary>No deferral: for a departed peer or teardown.</summary>
        public void RemoveProjectileNow(uint id)
        {
            if (activeProjectiles.TryGetValue(id, out var toDel))
            {
                DestroyImmediate(toDel);
                activeProjectiles.Remove(id);
            }

            snapshotsBuffers.Remove(id);
            projectileOwners.Remove(id);
            pendingRemoval.Remove(id);
        }

        private Transform GetProjectileTransform(GameObject projectile)
        {
            var projectileCringeSword = projectile.GetComponent<ProjectileCringeSword>();
            if (projectileCringeSword != null)
            {
                return projectileCringeSword.movingProjectile.transform;
            }

            var projectileHeroSword = projectile.GetComponent<ProjectileHeroSword>();
            if (projectileHeroSword != null)
            {
                return projectileHeroSword.movingProjectile.transform;
            }

            var projectileRocket = projectile.GetComponent<ProjectileRocket>();
            if (projectileRocket != null)
            {
                return projectileRocket.rocket.transform;
            }

            return projectile.transform;
        }
    }
}

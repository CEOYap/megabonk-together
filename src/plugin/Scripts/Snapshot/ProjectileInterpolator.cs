using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Scripts.Snapshot
{
    public class ProjectileInterpolator : MonoBehaviour
    {
        private readonly Dictionary<uint, GameObject> activeProjectiles = new Dictionary<uint, GameObject>();
        private readonly Dictionary<uint, List<ProjectileSnapshot>> snapshotsBuffers = new Dictionary<uint, List<ProjectileSnapshot>>();
        // FIX P2-5 (remainder): id -> owning connection id. These ids come from the SENDER's
        // counter, so they are a different id space from ProjectileManagerService.spawnedProjectile
        // and cannot share its map. Without an owner here, a departed peer's received projectiles
        // stayed registered and kept being interpolated against snapshots that would never arrive.
        private readonly Dictionary<uint, uint> projectileOwners = new Dictionary<uint, uint>();

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

            if (!snapshotsBuffers.ContainsKey(id))
            {
                snapshotsBuffers[id] = new List<ProjectileSnapshot>();
            }
        }

        /// <summary>
        /// Drops every projectile received from <paramref name="ownerId"/>. Called when that peer
        /// disconnects: no further snapshots will arrive for them, so they would otherwise sit in
        /// <see cref="activeProjectiles"/> for the rest of the run, walked by every Update.
        /// </summary>
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
                UnregisterProjectile(id);
            }
        }

        /// <summary>
        /// <para>The <c>rocket.rocket.ProjectileDone()</c> call that used to stand here was a
        /// double free. Decompiled <c>Rocket$$ProjectileDone</c> ends with
        /// <c>gameObject.SetActive(false)</c> followed by <c>ObjectPool&lt;GameObject&gt;.Release</c>
        /// on the <b>Rocket's own</b> GameObject — which is a child of <paramref name="id"/>'s
        /// object. The very next line then destroyed the parent, taking the just-pooled child with
        /// it, so <c>PoolManager</c> kept a destroyed GameObject and handed it out again later.
        /// That is the pool-starvation shape <c>Helpers/PoolHelper.cs</c> exists to paper over.</para>
        ///
        /// <para>Destroying without releasing is what every other projectile type already does on a
        /// client: <see cref="Patches.Projectiles.ProjectileBasePatches.ProjectileDone_Postfix"/>
        /// suppresses the pooled despawn for anything carrying a netplayId, and the object is
        /// destroyed outright.</para>
        ///
        /// <para><b>Deliberately not changed:</b> <c>DestroyImmediate</c> stays. <c>Destroy</c> is
        /// the safer call and <c>ProjectileManagerService.RemoveProjectile</c> uses it, but
        /// swapping it here is a separate change with its own ordering risk.</para>
        /// </summary>
        public void UnregisterProjectile(uint id)
        {
            if (activeProjectiles.TryGetValue(id, out var toDel))
            {
                DestroyImmediate(toDel);
                activeProjectiles.Remove(id);
            }

            snapshotsBuffers.Remove(id);
            projectileOwners.Remove(id);
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

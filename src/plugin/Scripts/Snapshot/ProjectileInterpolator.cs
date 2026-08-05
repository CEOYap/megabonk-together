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

        /// <summary>
        /// id -> the time at which it may actually be destroyed. See <see cref="UnregisterProjectile"/>.
        /// </summary>
        private readonly Dictionary<uint, double> pendingRemoval = new Dictionary<uint, double>();

        /// <summary>Reused across frames so the removal sweep does not allocate per Update.</summary>
        private readonly List<uint> expiredScratch = new List<uint>();

        // Seconds, despite the name — it is subtracted from Time.timeAsDouble directly. Remote
        // projectiles are therefore drawn 100 ms behind the host, which is what UnregisterProjectile
        // has to wait out.
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

        /// <summary>
        /// Destroys projectiles whose deferred removal has come due. Collected first and destroyed
        /// after, because destroying inside the enumeration would invalidate it.
        /// </summary>
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
                RemoveProjectileNow(id);
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

            // Quaternion.LookRotation on a zero vector logs "Look rotation viewing vector is zero"
            // and returns identity. Both snapshots are read every frame for every remote
            // projectile, so a stationary or quantization-collapsed direction snapped the
            // projectile to identity twice per frame and flooded the log: 208,936 of those
            // warnings on the client in one Run C, against zero on the host. Keeping the previous
            // rotation is strictly better than snapping to identity, and Unity's own logging is
            // expensive enough at that rate to matter on its own.
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

            // Ids are recycled, so a new projectile can land on one still waiting out its deferred
            // removal. Without this it would be destroyed mid-flight a fraction of a second later.
            pendingRemoval.Remove(id);

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
                // Immediate: that peer is gone, so no further snapshot will arrive and there is
                // nothing for the deferred path to wait for.
                RemoveProjectileNow(id);
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
            if (!activeProjectiles.ContainsKey(id))
            {
                // Never registered, or already swept. Drop any residue and stop.
                snapshotsBuffers.Remove(id);
                projectileOwners.Remove(id);
                pendingRemoval.Remove(id);
                return;
            }

            // Deferred, not immediate. Update draws every remote projectile interpolationDelayMs
            // behind the host, but ProjectileDone is a discrete event applied the moment it
            // arrives — so destroying here killed the projectile while its rendered copy was still
            // 100 ms of travel short of the target. Reported as rockets vanishing and ending their
            // animation mid-flight on the way to an enemy, after the mid-flight *explosion* had
            // already been fixed separately.
            //
            // Waiting one interpolation delay lets the buffered snapshots play out to the position
            // the host ended at. It keeps interpolating in the meantime; only the destroy waits.
            pendingRemoval[id] = Time.timeAsDouble + interpolationDelayMs;
        }

        /// <summary>
        /// Destroys immediately, skipping the interpolation-delay wait in
        /// <see cref="UnregisterProjectile"/>. For cases where no further snapshot is coming and
        /// waiting would just hold a dead object: a departed peer, or teardown.
        /// </summary>
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

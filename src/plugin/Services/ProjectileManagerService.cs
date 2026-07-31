using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Extensions;
using MegabonkTogether.Helpers;
using MegabonkTogether.Scripts.Snapshot;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MegabonkTogether.Services
{
    public interface IProjectileManagerService
    {
        public IEnumerable<Projectile> GetAllProjectiles();
        public IEnumerable<Projectile> GetAllProjectilesDeltaAndUpdate();
        public uint AddSpawnedProjectile(ProjectileBase projectile, uint ownerId);
        public ProjectileBase GetProjectileById(uint id);
        public KeyValuePair<uint, ProjectileBase> GetProjectileByReference(ProjectileBase projectile);
        public ProjectileBase RemoveProjectileById(uint id);
        public void ResetForNextLevel();
        public void RemoveProjectile(Projectile projectileId);
        public void RegisterProjectileForInterpolation(uint id, GameObject projectile);
        public void UnregisterProjectileFromInterpolation(uint id);
        public void UpdateProjectileSnapshots(List<ProjectileSnapshot> projectileSnapshots);
        public void RemoveProjectilesByOwnerId(uint connectionId);
    }

    internal class ProjectileManagerService : IProjectileManagerService
    {
        private readonly ConcurrentDictionary<uint, ProjectileBase> spawnedProjectile = [];
        // FIX P2-5: id -> owning connection id, kept in step with spawnedProjectile. Same key
        // space, so it is only ever the ids this peer allocated; remote projectiles carry their
        // owner on the message and are tracked by the interpolator instead.
        private readonly ConcurrentDictionary<uint, uint> projectileOwners = [];
        private Dictionary<uint, Projectile> previousSpawnedProjectilesDelta = [];
        private uint currentProjectileId = 0;
        private ProjectileInterpolator projectileInterpolator;

        private const float POSITION_THRESHOLD = 0.05f;

        public IEnumerable<Projectile> GetAllProjectiles()
        {
            RemoveAllDeadProjectiles();
            return spawnedProjectile.Select(kv => kv.Value.ToModel(kv.Key)).ToList();
        }

        public IEnumerable<Projectile> GetAllProjectilesDeltaAndUpdate()
        {
            var currentProjectiles = spawnedProjectile.Select(kv => kv.Value.ToModel(kv.Key)).ToList();

            if (previousSpawnedProjectilesDelta.Count == 0)
            {
                previousSpawnedProjectilesDelta = currentProjectiles.ToDictionary(p => p.Id);
                return currentProjectiles;
            }

            var deltas = new List<Projectile>();

            foreach (var current in currentProjectiles)
            {
                if (!previousSpawnedProjectilesDelta.TryGetValue(current.Id, out var previous) || HasDelta(previous, current))
                {
                    deltas.Add(current);
                }
            }

            previousSpawnedProjectilesDelta = currentProjectiles.ToDictionary(p => p.Id);

            return deltas;
        }

        private bool HasDelta(Projectile previous, Projectile current)
        {
            float positionDelta = Vector3.Distance(
                Quantizer.Dequantize(previous.Position),
                Quantizer.Dequantize(current.Position)
            );

            return positionDelta > POSITION_THRESHOLD;
        }

        public ProjectileBase GetProjectileById(uint id)
        {
            if (spawnedProjectile.TryGetValue(id, out var projo))
            {
                return projo;
            }
            return null;
        }

        private void RemoveAllDeadProjectiles()
        {
            var toRemove = spawnedProjectile.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList();
            foreach (var id in toRemove)
            {
                spawnedProjectile.TryRemove(id, out var _);
                projectileOwners.TryRemove(id, out var _);
            }
        }

        public uint AddSpawnedProjectile(ProjectileBase projectile, uint ownerId)
        {
            currentProjectileId++;
            if (!spawnedProjectile.TryAdd(currentProjectileId, projectile))
            {
                Plugin.Log.LogWarning($"Attempted to add a projectile that already exists. Id: {currentProjectileId}");
                return 0;
            }

            // FIX P2-5: record who this projectile belongs to. Nothing did, which is why
            // RemoveProjectilesByOwnerId could only ever guess — see the comment there.
            projectileOwners[currentProjectileId] = ownerId;

            return currentProjectileId;
        }

        public KeyValuePair<uint, ProjectileBase> GetProjectileByReference(ProjectileBase projectile)
        {
            return spawnedProjectile.FirstOrDefault(kv => kv.Value == projectile);
        }

        public ProjectileBase RemoveProjectileById(uint id)
        {
            projectileOwners.TryRemove(id, out var _);

            if (!spawnedProjectile.TryRemove(id, out var projectile))
            {
                Plugin.Log.LogWarning($"Attempted to remove an projectile that does not exist {id}");
                return null;
            }

            return projectile;
        }

        public void ResetForNextLevel()
        {
            currentProjectileId = 0;
            spawnedProjectile.Clear();
            projectileOwners.Clear();
            previousSpawnedProjectilesDelta = [];

            if (projectileInterpolator != null)
            {
                Object.Destroy(projectileInterpolator.gameObject);
                projectileInterpolator = null;
            }
        }

        public void RemoveProjectile(Projectile projectileId)
        {
            var removed = RemoveProjectileById(projectileId.Id);

            if (removed == null)
            {
                Plugin.Log.LogWarning($"Tried to remove projectile with id {projectileId.Id} but it was not found.");
                return;
            }

            GameObject.Destroy(removed.gameObject);
        }

        public void RegisterProjectileForInterpolation(uint id, GameObject projectile)
        {
            EnsureInterpolatorExists();
            projectileInterpolator.RegisterProjectile(id, projectile);
        }

        public void UnregisterProjectileFromInterpolation(uint id)
        {
            if (projectileInterpolator != null)
            {
                projectileInterpolator.UnregisterProjectile(id);
            }
        }

        public void UpdateProjectileSnapshots(List<ProjectileSnapshot> projectileSnapshots)
        {
            EnsureInterpolatorExists();

            if (projectileSnapshots != null && projectileSnapshots.Count > 0)
            {
                projectileInterpolator.UpdateProjectiles(projectileSnapshots);
            }
        }

        /// <summary>
        /// Destroys every projectile this peer is simulating on behalf of <paramref name="connectionId"/>.
        /// Called when that peer disconnects; their projectiles otherwise keep flying, holding the
        /// weapon and attack of a NetPlayer that has just been destroyed.
        ///
        /// <para>FIX P2-5: the filter used to be <c>kv.Key == connectionId</c> — comparing the
        /// <b>projectile id</b> to a connection id, because no owner was recorded anywhere. It
        /// removed, at most, the single projectile whose id happened to equal that connection id,
        /// and normally nothing at all.</para>
        /// </summary>
        public void RemoveProjectilesByOwnerId(uint connectionId)
        {
            var projectilesToRemove = projectileOwners
                .Where(kv => kv.Value == connectionId)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var projectileId in projectilesToRemove)
            {
                var removedProjectile = RemoveProjectileById(projectileId);
                if (removedProjectile == null)
                {
                    continue;
                }

                // Guarded per projectile: one already-destroyed GameObject must not abandon the
                // rest of the sweep (the P1-7 lesson).
                try
                {
                    GameObject.DestroyImmediate(removedProjectile.gameObject);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"Could not destroy projectile {projectileId} of departed player {connectionId}: {ex.Message}");
                }
            }
        }

        private void EnsureInterpolatorExists()
        {
            if (projectileInterpolator == null)
            {
                var interpolatorGameObject = new GameObject("ProjectileInterpolator");
                projectileInterpolator = interpolatorGameObject.AddComponent<ProjectileInterpolator>();
                Object.DontDestroyOnLoad(interpolatorGameObject);
            }
        }
    }
}

using Assets.Scripts.Inventory__Items__Pickups.Items.ItemProjectiles;
using Assets.Scripts.Objects.Pooling;
using Assets.Scripts.Utility;
using BepInEx.Logging;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Extensions;
using OmniBonk.Helpers;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Services
{
    /// <summary>
    /// Draws other machines' item projectiles (Soul Harvester ghosts, Energy Core orbs, Ghost and Bob
    /// ghosts). Each item runs on one machine only (host for kill listeners, owner for timed items),
    /// so before this only that machine saw them. Copies are damage-free: see
    /// <c>ItemProjectileVisualPatches</c>. Main thread only.
    /// </summary>
    public interface IItemProjectileVisualService
    {
        /// <summary>True while this service is building a copy, so the <c>Set</c> postfix does not send it.</summary>
        bool BuildingCopy { get; }

        bool IsCopy(ItemProjectile projectile);

        void ForgetCopy(ItemProjectile projectile);

        /// <summary>Diagnostics: a copy ended on an enemy without dealing damage.</summary>
        void NoteCopyEndedOnEnemy();

        /// <summary>A real projectile was just set up on this machine: queue it for the other peers.</summary>
        void OnLocalProjectileSet(ItemProjectile projectile);

        void Reset();
    }

    internal class ItemProjectileVisualService : IItemProjectileVisualService
    {
        private readonly INetTransport transport;
        private readonly ISynchronizationService synchronizationService;
        private readonly IPlayerManagerService playerManagerService;
        private readonly ITrackerService trackerService;
        private readonly ManualLogSource logger;

        // Pooled objects are reused, so a copy is forgotten when it finishes or is set up for real.
        // Keyed by Unity instance id: these stock-compiled MonoBehaviours expose no IL2CPP Pointer.
        private readonly HashSet<int> copies = [];
        private ItemProjectilesSpawned pending = new();
        private bool flushScheduled;
        private bool loggedUnknownPool;

        // Diagnostics, logged at most every 10 s while copies arrive: drawn per kind, sent, ended on an enemy.
        private readonly int[] drawnByKind = new int[3];
        private int sentSpawns;
        private int copiesEndedOnEnemy;
        private float nextSummaryAt;

        public bool BuildingCopy { get; private set; }

        public ItemProjectileVisualService(
            INetTransport transport,
            ISynchronizationService synchronizationService,
            IPlayerManagerService playerManagerService,
            ITrackerService trackerService,
            ManualLogSource logger)
        {
            this.transport = transport;
            this.synchronizationService = synchronizationService;
            this.playerManagerService = playerManagerService;
            this.trackerService = trackerService;
            this.logger = logger;
            EventManager.SubscribeItemProjectilesSpawnedEvents(OnReceived);
        }

        public bool IsCopy(ItemProjectile projectile) => projectile != null && copies.Contains(projectile.GetInstanceID());

        public void ForgetCopy(ItemProjectile projectile)
        {
            if (projectile != null)
            {
                copies.Remove(projectile.GetInstanceID());
            }
        }

        public void NoteCopyEndedOnEnemy() => copiesEndedOnEnemy++;

        public void Reset()
        {
            System.Array.Clear(drawnByKind);
            sentSpawns = 0;
            copiesEndedOnEnemy = 0;
            copies.Clear();
            pending = new ItemProjectilesSpawned();
            flushScheduled = false;
        }

        public void OnLocalProjectileSet(ItemProjectile projectile)
        {
            var kind = KindOf(projectile.damageSource);
            if (!kind.HasValue)
            {
                if (!loggedUnknownPool)
                {
                    loggedUnknownPool = true;
                    logger.LogWarning($"[item-fx] An item projectile from an unknown pool was not shared ({projectile.damageSource}).");
                }

                return;
            }

            var owner = trackerService.GetCurrentPlayerId() ?? playerManagerService.GetLocalPlayer()?.ConnectionId;
            if (!owner.HasValue)
            {
                return;
            }

            var direction = projectile.startDirection;
            pending.Spawns.Add(new ItemProjectileSpawn
            {
                Kind = (byte)kind.Value,
                OwnerId = owner.Value,
                Position = Quantizer.Quantize(projectile.transform.position),
                DirectionX = direction.x,
                DirectionY = direction.y,
                DirectionZ = direction.z,
                Speed = projectile.finalProjectileSpeed,
                Lifetime = Math.Max(0f, projectile.expirationTime - MyTime.time),
                UpTime = projectile.upTime,
                Range = projectile.range,
                ProjectileIndex = projectile.projectileIndex,
                ProjectileCount = projectile.projectilesCount,
                DamageSource = projectile.damageSource ?? "",
            });

            if (!flushScheduled)
            {
                flushScheduled = true;
                CoroutineRunner.Instance.Run(FlushNextFrame());
            }
        }

        private IEnumerator FlushNextFrame()
        {
            yield return null;

            flushScheduled = false;
            if (pending.Spawns.Count == 0 || !synchronizationService.HasNetplaySessionStarted())
            {
                pending.Spawns.Clear();
                yield break;
            }

            var batch = pending;
            pending = new ItemProjectilesSpawned();
            sentSpawns += batch.Spawns.Count;
            LogSummaryIfDue();

            if (synchronizationService.IsServerMode() == true)
            {
                transport.SendToAllClients(batch, NetDelivery.ReliableUnordered);
            }
            else
            {
                transport.SendToHost(batch, NetDelivery.ReliableUnordered);
            }
        }

        private void OnReceived(ItemProjectilesSpawned received)
        {
            if (received?.Spawns == null || received.Spawns.Count == 0 || !synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var isHost = synchronizationService.IsServerMode() == true;
            var localId = playerManagerService.GetLocalPlayer()?.ConnectionId;

            foreach (var spawn in received.Spawns)
            {
                try
                {
                    SpawnCopy(spawn);
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[item-fx] Could not draw a {(ItemProjectileKind)spawn.Kind} copy: {ex.Message}");
                }
            }

            LogSummaryIfDue();

            // A client's batch: every entry has the sender as owner. Relay to the other clients.
            if (isHost && received.Spawns[0].OwnerId != localId)
            {
                transport.SendToAllClientsExcept(received.Spawns[0].OwnerId, received);
            }
        }

        private void SpawnCopy(ItemProjectileSpawn spawn)
        {
            var pool = PoolOf((ItemProjectileKind)spawn.Kind);
            if (pool == null)
            {
                return;
            }

            var go = pool.Get();
            if (go == null)
            {
                return;
            }

            go.SetActive(true);
            var projectile = go.GetComponent<ItemProjectile>();
            if (projectile == null)
            {
                return;
            }

            if (spawn.Kind < drawnByKind.Length)
            {
                drawnByKind[spawn.Kind]++;
            }

            // Marked before Set: Set runs CheckSpawnCollision, which must already treat it as a copy.
            copies.Add(projectile.GetInstanceID());
            BuildingCopy = true;
            try
            {
                projectile.Set(
                    Quantizer.Dequantize(spawn.Position), 0f, 0f, spawn.DamageSource, pool,
                    spawn.ProjectileIndex, spawn.ProjectileCount, spawn.Lifetime, spawn.Range);
            }
            finally
            {
                BuildingCopy = false;
            }

            // Set derived these from this machine's stats; use the sender's so the copy flies the same.
            var direction = new Vector3(spawn.DirectionX, spawn.DirectionY, spawn.DirectionZ);
            projectile.startDirection = direction;
            projectile.lastDirection = direction;
            projectile.finalProjectileSpeed = spawn.Speed;
            projectile.upTime = spawn.UpTime;
            projectile.expirationTime = MyTime.time + spawn.Lifetime;

            // Init pointed the orb at this machine's player; it belongs to the owner.
            var orb = go.GetComponent<ItemProjectileOrb>();
            if (orb != null)
            {
                var owner = playerManagerService.GetNetPlayerByNetplayId(spawn.OwnerId);
                if (owner?.Model != null)
                {
                    orb.orbitTarget = owner.Model.transform;
                }
            }
        }

        private void LogSummaryIfDue()
        {
            var now = Time.realtimeSinceStartup;
            if (now < nextSummaryAt)
            {
                return;
            }

            nextSummaryAt = now + 10f;
            logger.LogInfo(
                $"[item-fx] session so far: sent {sentSpawns}; drew souls {drawnByKind[(int)ItemProjectileKind.AngrySoul]}, "
                + $"orbs {drawnByKind[(int)ItemProjectileKind.Orb]}, ghosts {drawnByKind[(int)ItemProjectileKind.Ghost]}; "
                + $"{copiesEndedOnEnemy} copies ended on an enemy.");
        }

        /// <summary>
        /// From the item's damage source (its <c>EItem</c> name, decompiled in the ctors): the pools
        /// cannot be compared by pointer here. Ghost and BobDead share <c>ghostPool</c>; their source
        /// strings are <b>UNVERIFIED</b> (inferred from the same ctor pattern).
        /// </summary>
        private static ItemProjectileKind? KindOf(string damageSource) => damageSource switch
        {
            "SoulHarvester" => ItemProjectileKind.AngrySoul,
            "EnergyCore" => ItemProjectileKind.Orb,
            "Ghost" or "BobDead" => ItemProjectileKind.Ghost,
            _ => null,
        };

        private static UnityEngine.Pool.ObjectPool<GameObject> PoolOf(ItemProjectileKind kind)
        {
            var manager = PoolManager.Instance;
            if (manager == null)
            {
                return null;
            }

            return kind switch
            {
                ItemProjectileKind.AngrySoul => manager.angrySoulPool,
                ItemProjectileKind.Orb => manager.orbPool,
                ItemProjectileKind.Ghost => manager.ghostPool,
                _ => null,
            };
        }
    }
}

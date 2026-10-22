using Actors.Enemies;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Game.Spawning;
using Assets.Scripts.Managers;
using BepInEx.Logging;
using OmniBonk.Common.Models;
using OmniBonk.Configuration;
using OmniBonk.Extensions;
using OmniBonk.Helpers;
using OmniBonk.Scripts.Enemies;
using MonoMod.Utils;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace OmniBonk.Services
{
    public interface IEnemyManagerService
    {
        public IEnumerable<(uint, uint)> ReTargetEnemies(uint oldTargetId, IEnumerable<uint> currentPlayersExcludingOldOneId);
        public IEnumerable<EnemyModel> GetAllEnemiesDeltaAndUpdate();
        public uint AddSpawnedEnemy(Enemy enemy);

        /// <summary>Desync detector. Entries in the registry, keyed by host id on every peer.</summary>
        int TrackedEnemyCount { get; }
        public void SetSpawnedEnemy(uint enemyId, Enemy enemy);
        public Enemy GetEnemyById(uint id);

        /// <summary>Every registered enemy, for diagnostics. A live view: do not hold it across frames.</summary>
        public IEnumerable<KeyValuePair<uint, Enemy>> GetAllRegisteredEnemies();
        public KeyValuePair<uint, Enemy> GetEnemyByReference(Enemy enemy);
        public void RemoveEnemyById(uint id);

        /// <summary>OB-51. Unregisters whatever id this pooled object held; returns it, or null.</summary>
        public uint? RemoveEnemyByInstance(Enemy enemy);
        public void ResetForNextLevel();
        public void ApplyRetargetedEnemies(IEnumerable<(uint, uint)> enemy_NewTargetids, IEnumerable<(uint, Rigidbody)> playerId_rigidbody);
        public void InitializeSwitcher(TargetSwitcher switcher, EEnemyFlag enemyFlag, EEnemy enemyName);

        public void AddReviverEnemy_Name(Enemy enemy, string netplayName);
        public string GetReviverEnemy_Name(Enemy enemy);
        public void RemoveReviverEnemy_Name(Enemy enemy);
        public void RebalanceIfNeededReviverEnemy(Enemy enemy, uint? currentReviver, uint? currentReviverOwner);
        public void ResetReviverSpawnCounts();

        /// <summary>OB-11. Host only.</summary>
        public Vector3 RecentreBossSpawnOnRandomPlayer(Vector3 pos);
    }
    internal class EnemyManagerService : IEnemyManagerService
    {
        private readonly IPlayerManagerService playerManagerService;
        private readonly ManualLogSource logger;

        public EnemyManagerService(IPlayerManagerService playerManagerService, ManualLogSource logger)
        {
            this.playerManagerService = playerManagerService;
            this.logger = logger;
        }

        private readonly ConcurrentDictionary<uint, Enemy> spawnedEnemies = [];

        /// <summary>
        /// OB-51: id per pooled enemy object (by instance id), so a reused object cannot be
        /// registered under several ids at once.
        /// </summary>
        private readonly Dictionary<int, uint> idByInstance = [];
        private readonly Dictionary<uint, int> instanceById = [];
        private readonly object instanceLock = new();
        private int droppedAliases;

        /// <summary>Indexes a new registration, dropping any older id that still points at this object.</summary>
        private void IndexRegistration(uint id, Enemy enemy)
        {
            var instanceId = enemy.GetInstanceID();
            lock (instanceLock)
            {
                if (idByInstance.TryGetValue(instanceId, out var oldId) && oldId != id)
                {
                    instanceById.Remove(oldId);
                    if (spawnedEnemies.TryRemove(oldId, out _))
                    {
                        droppedAliases++;
                        if (droppedAliases == 1 || droppedAliases % 25 == 0)
                        {
                            logger.LogInfo($"[desync] OB-51: enemy object reused for {id} while {oldId} still pointed at it; dropped {oldId} ({droppedAliases} so far).");
                        }
                    }
                }

                idByInstance[instanceId] = id;
                instanceById[id] = instanceId;
            }
        }

        private void UnindexId(uint id)
        {
            lock (instanceLock)
            {
                if (instanceById.Remove(id, out var instanceId)
                    && idByInstance.TryGetValue(instanceId, out var current) && current == id)
                {
                    idByInstance.Remove(instanceId);
                }
            }
        }

        /// <summary>OB-51. Client: the game pooled this object, so whatever id it held is gone here.</summary>
        public uint? RemoveEnemyByInstance(Enemy enemy)
        {
            uint id;
            lock (instanceLock)
            {
                if (!idByInstance.Remove(enemy.GetInstanceID(), out id))
                {
                    return null;
                }

                instanceById.Remove(id);
            }

            return spawnedEnemies.TryRemove(id, out _) ? id : null;
        }

        public int TrackedEnemyCount => spawnedEnemies.Count;
        private readonly ConcurrentDictionary<Enemy, string> reviverEnemies_NetplayNames = [];
        private readonly ConcurrentDictionary<uint, int> reviverSpawnCountPerOwner = [];
        // FIX P0-3: int for Interlocked.Increment; first id is 1, 0 is the failure sentinel.
        private int currentEnemyId = 0;

        #region Budgeted priority enemy stream

        /// <remarks>
        /// The stream is unreliable without acks, so delivery is not guaranteed; staleness is bounded
        /// instead. Changed enemies go stalest-first up to a per-tick budget, spare budget refreshes
        /// enemies unsent for a refresh period, so a lost final state is corrected within ~1 s.
        /// </remarks>
        private const int ENEMY_MODEL_WIRE_BYTES = Tunables.ENEMY_MODEL_WIRE_BYTES;

        private const int MAX_ENEMY_TICK_BYTES = Tunables.MAX_ENEMY_TICK_BYTES;

        private const int MAX_ENEMIES_PER_TICK = Tunables.MAX_ENEMIES_PER_TICK;

        /// <summary>Minimum staleness for a refresh, and the ticks a full refresh pass spreads over.</summary>
        private const int ENEMY_REFRESH_PERIOD_TICKS = Tunables.ENEMY_REFRESH_PERIOD_TICKS;

        /// <summary>Last state put on the wire, not last sampled.</summary>
        private Dictionary<uint, EnemyModel> lastSentEnemies = [];
        private Dictionary<uint, long> lastSentTick = [];
        private long enemyStreamTick;

        // Reused per tick (40 Hz hot path). `selected` is copied by SendEnemiesUpdate before the next tick.
        private readonly Dictionary<uint, EnemyModel> sampled = [];
        private readonly List<Candidate> changed = [];
        private readonly List<Candidate> unchanged = [];
        private readonly List<EnemyModel> selected = [];
        private readonly List<uint> despawned = [];

        private readonly struct Candidate(long staleness, EnemyModel model)
        {
            public readonly long Staleness = staleness;
            public readonly EnemyModel Model = model;
        }

        private static readonly System.Comparison<Candidate> StalestFirst =
            (a, b) => b.Staleness.CompareTo(a.Staleness);

        #endregion

        /// <summary>Host side: retarget enemies when a player dies.</summary>
        public IEnumerable<(uint, uint)> ReTargetEnemies(uint oldTargetId, IEnumerable<uint> currentPlayersAliveExcludingOldOneId)
        {
            var retargetedEnemies = new List<(uint, uint)>();

            // FIX P1-6: materialise once; an empty set used to throw and skip SpawnReviver.
            var candidates = currentPlayersAliveExcludingOldOneId as IList<uint>
                             ?? currentPlayersAliveExcludingOldOneId?.ToList();

            if (candidates == null || candidates.Count == 0)
            {
                return retargetedEnemies;
            }

            var oldTargetEnemies = spawnedEnemies.Values.Where(enemy =>
            {
                var currentTargetid = DynamicData.For(enemy).Get<uint?>("targetId");
                if (currentTargetid.HasValue && currentTargetid.Value == oldTargetId)
                {
                    return true;
                }
                return false;
            });


            foreach (var oldEnemy in oldTargetEnemies)
            {
                var randomNewTargetId = candidates[Random.Range(0, candidates.Count)];

                DynamicData.For(oldEnemy).Set("targetId", randomNewTargetId);
                var enemyId = GetEnemyByReference(oldEnemy).Key;

                retargetedEnemies.Add((enemyId, randomNewTargetId));
            }

            return retargetedEnemies;
        }

        public void ApplyRetargetedEnemies(IEnumerable<(uint, uint)> enemy_NewTargetids, IEnumerable<(uint, Rigidbody)> playerId_rigidbody)
        {
            // PERF 04 item 4: index players once instead of O(enemies x players).
            var rigidbodyByPlayerId = new Dictionary<uint, Rigidbody>();
            foreach (var (playerId, rigidbody) in playerId_rigidbody)
            {
                rigidbodyByPlayerId[playerId] = rigidbody;
            }

            foreach (var (enemyId, newTargetId) in enemy_NewTargetids)
            {
                var enemy = GetEnemyById(enemyId);
                if (enemy != null)
                {
                    rigidbodyByPlayerId.TryGetValue(newTargetId, out var playerRigidbody);
                    if (playerRigidbody != null)
                    {
                        DynamicData.For(enemy).Set("targetId", newTargetId);
                        enemy.target = playerRigidbody;
                    }
                }
                else
                {
                    Plugin.Log.LogWarning($"Failed to retarget enemy {enemyId} to new target {newTargetId} - enemy not found");
                }
            }
        }

        /// <summary>Call once per host tick.</summary>
        public IEnumerable<EnemyModel> GetAllEnemiesDeltaAndUpdate()
        {
            enemyStreamTick++;

            sampled.Clear();
            foreach (var (id, enemy) in spawnedEnemies)
            {
                sampled[id] = enemy.ToModel(id);
            }

            PruneDespawned();

            changed.Clear();
            unchanged.Clear();

            foreach (var current in sampled.Values)
            {
                // Never-sent enemies are maximally stale so they cannot be starved.
                var staleness = lastSentTick.TryGetValue(current.Id, out var sentAt)
                    ? enemyStreamTick - sentAt
                    : long.MaxValue;

                if (!lastSentEnemies.TryGetValue(current.Id, out var previous) || HasDelta(previous, current))
                {
                    changed.Add(new Candidate(staleness, current));
                }
                else if (staleness >= ENEMY_REFRESH_PERIOD_TICKS)
                {
                    unchanged.Add(new Candidate(staleness, current));
                }
            }

            selected.Clear();

            // Changed first; overflow defers a tick and returns first as the stalest.
            changed.Sort(StalestFirst);
            TakeUpTo(changed, MAX_ENEMIES_PER_TICK);

            // Refresh sweep, capped to one period's share per tick.
            var refreshQuota = (sampled.Count + ENEMY_REFRESH_PERIOD_TICKS - 1) / ENEMY_REFRESH_PERIOD_TICKS;
            unchanged.Sort(StalestFirst);
            TakeUpTo(unchanged, System.Math.Min(MAX_ENEMIES_PER_TICK, selected.Count + refreshQuota));

            foreach (var model in selected)
            {
                lastSentEnemies[model.Id] = model;
                lastSentTick[model.Id] = enemyStreamTick;
            }

            return selected;
        }

        private void TakeUpTo(List<Candidate> candidates, int cap)
        {
            for (var i = 0; i < candidates.Count && selected.Count < cap; i++)
            {
                selected.Add(candidates[i].Model);
            }
        }

        /// <summary>Drops stream bookkeeping for despawned enemies so it does not grow all stage.</summary>
        private void PruneDespawned()
        {
            despawned.Clear();

            foreach (var id in lastSentTick.Keys)
            {
                if (!sampled.ContainsKey(id))
                {
                    despawned.Add(id);
                }
            }

            foreach (var id in despawned)
            {
                lastSentTick.Remove(id);
                lastSentEnemies.Remove(id);
            }
        }

        private bool HasDelta(EnemyModel previous, EnemyModel current)
        {
            float positionDelta = Vector3.Distance(
                Quantizer.Dequantize(previous.Position),
                Quantizer.Dequantize(current.Position)
            );

            float yawDelta = Mathf.Abs(
                Quantizer.DequantizeYaw(previous.Yaw)
                - Quantizer.DequantizeYaw(current.Yaw)
            );

            float hpDelta = Mathf.Abs(previous.Hp - current.Hp);

            return positionDelta > Tunables.ENEMY_POSITION_THRESHOLD ||
                   yawDelta > Tunables.ENEMY_YAW_THRESHOLD ||
                   hpDelta > Tunables.ENEMY_HP_THRESHOLD;
        }

        public IEnumerable<KeyValuePair<uint, Enemy>> GetAllRegisteredEnemies() => spawnedEnemies;

        public Enemy GetEnemyById(uint id)
        {
            if (spawnedEnemies.TryGetValue(id, out var enemy))
            {
                return enemy;
            }
            return null;
        }


        /// <summary>Host side.</summary>
        public uint AddSpawnedEnemy(Enemy enemy)
        {
            // FIX P0-3: allocate atomically, then use only the local.
            var newId = (uint)Interlocked.Increment(ref currentEnemyId);

            if (!spawnedEnemies.TryAdd(newId, enemy))
            {
                Plugin.Log.LogWarning($"Attempted to add an enemy that already exists. EnemyId: {newId}");
                return 0;
            }

            DynamicData.For(enemy).Set("netplayId", newId);
            IndexRegistration(newId, enemy);

            return newId;
        }

        /// <summary>Client side.</summary>
        public void SetSpawnedEnemy(uint enemyId, Enemy enemy)
        {
            if (!spawnedEnemies.TryAdd(enemyId, enemy))
            {
                Plugin.Log.LogWarning($"Attempted to add an enemy that already exists. EnemyId: {enemyId}");
                return;
            }

            DynamicData.For(enemy).Set("netplayId", enemyId);
            IndexRegistration(enemyId, enemy);
        }

        public KeyValuePair<uint, Enemy> GetEnemyByReference(Enemy enemy)
        {
            var netplayId = DynamicData.For(enemy).Get<uint?>("netplayId");
            if (netplayId.HasValue && spawnedEnemies.TryGetValue(netplayId.Value, out var stored) && stored == enemy)
            {
                return new KeyValuePair<uint, Enemy>(netplayId.Value, stored);
            }

            return spawnedEnemies.FirstOrDefault(kv => kv.Value == enemy);
        }

        public void RemoveEnemyById(uint id)
        {
            UnindexId(id);
            spawnedEnemies.TryRemove(id, out _);
        }

        public void ResetForNextLevel()
        {
            //spawnedEnemies.Select(Enemy => Enemy.Value).ToList().ForEach(enemy => GameObject.Destroy(enemy.gameObject));
            spawnedEnemies.Clear();
            lock (instanceLock)
            {
                idByInstance.Clear();
                instanceById.Clear();
            }
            lastSentEnemies = [];
            lastSentTick = [];
            enemyStreamTick = 0;
        }

        //TODO: the applied values should be stored in GameBalanceService
        public void InitializeSwitcher(TargetSwitcher switcher, EEnemyFlag enemyFlag, EEnemy enemyName)
        {
            switch (enemyName)
            {
                case EEnemy.GhostGrave1:
                case EEnemy.GhostGrave2:
                case EEnemy.GhostGrave3:
                case EEnemy.GhostGrave4:
                    switcher.UpdateSwitchIntervalRange(7f, 12f);
                    switcher.UpdateSwitchMaxDistance(30f);
                    return;
                default:
                    break;
            }

            switch (enemyFlag)
            {
                case EEnemyFlag.FinalBoss:
                    switcher.UpdateSwitchIntervalRange(30f, 50f);
                    switcher.UpdateSwitchMaxDistance(300f);
                    break;
                case EEnemyFlag.StageBoss:
                    switcher.UpdateSwitchIntervalRange(20f, 40f);
                    switcher.UpdateSwitchMaxDistance(300f);
                    break;
                default:
                    switcher.UpdateSwitchIntervalRange(40f, 60f);
                    switcher.UpdateSwitchMaxDistance(50f);
                    break;
            }
        }

        public void AddReviverEnemy_Name(Enemy enemy, string netplayName)
        {
            reviverEnemies_NetplayNames.TryAdd(enemy, netplayName);
        }

        public string GetReviverEnemy_Name(Enemy enemy)
        {
            if (reviverEnemies_NetplayNames.TryGetValue(enemy, out var name))
            {
                return name;
            }
            return null;
        }

        public void RemoveReviverEnemy_Name(Enemy enemy)
        {
            reviverEnemies_NetplayNames.TryRemove(enemy, out _);
        }

        public void RebalanceIfNeededReviverEnemy(Enemy enemy, uint? currentReviver, uint? currentReviverOwner)
        {
            if (!currentReviver.HasValue || !currentReviverOwner.HasValue)
            {
                return;
            }

            var ownerId = currentReviverOwner.Value;
            var count = reviverSpawnCountPerOwner.AddOrUpdate(ownerId, 1, (_, prev) => prev + 1);

            if (count >= 6)
            {
                return;
            }

            var multiplier = (count * 2) / 12f;
            var newHp = enemy.hp * multiplier;

            enemy.hp = newHp;
            enemy.controlHp = newHp;
            enemy.maxHp = newHp;
            enemy._hp_k__BackingField = newHp;
        }

        public void ResetReviverSpawnCounts()
        {
            reviverSpawnCountPerOwner.Clear();
        }

        #region OB-11 — miniboss spawns are host-centred

        private readonly List<(uint ConnectionId, Vector3 Anchor)> bossSpawnCandidates = [];

        /// <summary>
        /// OB-11: round-robin slot for the next boss (random runs made the fix untestable by eye).
        /// Never reset, so the cycle carries across levels.
        /// </summary>
        private int bossSpawnRotation = 0;

        /// <summary>
        /// OB-11: re-centre a miniboss spawn on another player. Decompiled:
        /// <c>SpawnPositions.GetEnemySpawnPositionBiased</c> (VA <c>0x180482390</c>) only uses the local player.
        /// <para>Runs in the prefix so the enemy is created at the corrected point. Host only, or each
        /// peer would spawn its own boss. Every failure returns <paramref name="pos"/> unchanged.</para>
        /// <para><b>UNVERIFIED.</b> Not run in-game: safety of calling <c>GetEnemySpawnPositionAroundPoint</c>
        /// from this prefix, and the ring radius tunables.</para>
        /// </summary>
        public Vector3 RecentreBossSpawnOnRandomPlayer(Vector3 pos)
        {
            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.player == null)
            {
                return pos;
            }

            var localPosition = gameManager.player.transform.position;

            bossSpawnCandidates.Clear();

            foreach (var netPlayer in playerManagerService.GetAllSpawnedNetPlayers())
            {
                if (netPlayer == null)
                {
                    continue;
                }

                var model = playerManagerService.GetPlayer(netPlayer.ConnectionId);
                if (model == null || model.Hp == 0)
                {
                    continue;
                }

                // Replicated position, not the avatar transform (it can free-fall before the first snapshot).
                var anchor = model.Position != null
                    ? Quantizer.Dequantize(model.Position)
                    : netPlayer.transform.position;

                if (!IsPlausibleSpawnAnchor(anchor))
                {
                    logger.LogInfo(
                        $"OB-11: skipping peer {netPlayer.ConnectionId} as a spawn anchor; " +
                        $"position {anchor} is not on the map.");
                    continue;
                }

                bossSpawnCandidates.Add((netPlayer.ConnectionId, anchor));
            }

            if (bossSpawnCandidates.Count == 0)
            {
                logger.LogInfo(
                    "OB-11: no eligible peer to re-centre on (none spawned, all dead, or all " +
                    "anchors implausible); keeping the host-centred position.");
                return pos;
            }

            // One slot per peer plus the host's own (index Count = keep the vanilla position).
            var slots = bossSpawnCandidates.Count + 1;
            var pick = (int)((uint)bossSpawnRotation % (uint)slots);
            unchecked
            {
                bossSpawnRotation++;
            }

            if (pick == bossSpawnCandidates.Count)
            {
                logger.LogInfo(
                    $"OB-11: rotation reached the host's own slot ({pick} of {slots}); " +
                    "keeping the host-centred position.");
                return pos;
            }

            var (targetId, target) = bossSpawnCandidates[pick];

            // Reuse the game's chosen XZ distance (it scales with run time).
            var dx = pos.x - localPosition.x;
            var dz = pos.z - localPosition.z;
            var radius = Mathf.Sqrt((dx * dx) + (dz * dz));
            // Clamped both ends: pos can equal the host position (fallback), or a coffin's far transform.
            if (radius < Tunables.MIN_BOSS_RING_RADIUS)
            {
                radius = Tunables.MIN_BOSS_RING_RADIUS;
            }
            else if (radius > Tunables.MAX_BOSS_RING_RADIUS)
            {
                radius = Tunables.MAX_BOSS_RING_RADIUS;
            }

            // onlyGround stays false (inside TryPlaceAround): the game's spawner uses whatIsGroundAndObjects (0x34); true failed every placement.
            var recentred = TryPlaceAround(target, radius, Tunables.BOSS_RING_BAND);

            // Retry with a wide band before falling back to the host.
            if (IsInvalid(recentred))
            {
                recentred = TryPlaceAround(target, radius, WIDE_BOSS_RING_BAND);
            }

            if (IsInvalid(recentred))
            {
                logger.LogInfo(
                    $"OB-11: no valid miniboss position within {radius:F1}u of peer {targetId} " +
                    $"at {target} (tried a narrow then a wide ring); keeping the host-centred one.");
                return pos;
            }

            logger.LogInfo(
                $"OB-11: re-centred a boss spawn on peer {targetId} - {pos} -> {recentred} " +
                $"({radius:F1}u ring, slot {pick} of {slots}).");

            return recentred;
        }

        private const float WIDE_BOSS_RING_BAND = 0.7f;

        private static Vector3 TryPlaceAround(Vector3 target, float radius, float band)
        {
            return SpawnPositions.GetEnemySpawnPositionAroundPoint(
                target,
                radius * (1f - band),
                radius * (1f + band),
                Tunables.BOSS_RING_ATTEMPTS,
                false,
                999f);
        }

        /// <summary>OB-11: rejects anchors that cannot be on the map (e.g. a falling, unspawned avatar).</summary>
        private static bool IsPlausibleSpawnAnchor(Vector3 candidate)
        {
            if (float.IsNaN(candidate.x) || float.IsNaN(candidate.y) || float.IsNaN(candidate.z)
                || float.IsInfinity(candidate.x) || float.IsInfinity(candidate.y) || float.IsInfinity(candidate.z))
            {
                return false;
            }

            return Mathf.Abs(candidate.x) < Tunables.MAX_PLAUSIBLE_WORLD_COORDINATE
                && Mathf.Abs(candidate.y) < Tunables.MAX_PLAUSIBLE_WORLD_COORDINATE
                && Mathf.Abs(candidate.z) < Tunables.MAX_PLAUSIBLE_WORLD_COORDINATE;
        }

        /// <summary>The game returns the <c>INVALID_POS</c> sentinel (69.6978 on each axis) on failure.</summary>
        private static bool IsInvalid(Vector3 candidate)
        {
            return (candidate - SpawnPositions.INVALID_POS).sqrMagnitude < 0.01f;
        }

        #endregion
    }
}

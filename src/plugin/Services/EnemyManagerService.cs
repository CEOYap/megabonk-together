using Actors.Enemies;
using Assets.Scripts.Actors.Enemies;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Extensions;
using MegabonkTogether.Helpers;
using MegabonkTogether.Scripts.Enemies;
using MonoMod.Utils;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace MegabonkTogether.Services
{
    public interface IEnemyManagerService
    {
        public IEnumerable<(uint, uint)> ReTargetEnemies(uint oldTargetId, IEnumerable<uint> currentPlayersExcludingOldOneId);
        public IEnumerable<EnemyModel> GetAllEnemiesDeltaAndUpdate();
        public uint AddSpawnedEnemy(Enemy enemy);
        public void SetSpawnedEnemy(uint enemyId, Enemy enemy);
        public Enemy GetEnemyById(uint id);
        public KeyValuePair<uint, Enemy> GetEnemyByReference(Enemy enemy);
        public void RemoveEnemyById(uint id);
        public void ResetForNextLevel();
        public void ApplyRetargetedEnemies(IEnumerable<(uint, uint)> enemy_NewTargetids, IEnumerable<(uint, Rigidbody)> playerId_rigidbody);
        public void InitializeSwitcher(TargetSwitcher switcher, EEnemyFlag enemyFlag, EEnemy enemyName);

        public void AddReviverEnemy_Name(Enemy enemy, string netplayName);
        public string GetReviverEnemy_Name(Enemy enemy);
        public void RemoveReviverEnemy_Name(Enemy enemy);
        public void RebalanceIfNeededReviverEnemy(Enemy enemy, uint? currentReviver, uint? currentReviverOwner);
        public void ResetReviverSpawnCounts();
    }
    internal class EnemyManagerService : IEnemyManagerService
    {
        private readonly ConcurrentDictionary<uint, Enemy> spawnedEnemies = [];
        private Dictionary<uint, EnemyModel> previousSpawnedEnemiesDelta = [];
        private readonly ConcurrentDictionary<Enemy, string> reviverEnemies_NetplayNames = [];
        private readonly ConcurrentDictionary<uint, int> reviverSpawnCountPerOwner = [];
        // FIX P0-3: int rather than uint so Interlocked.Increment can allocate ids atomically.
        // Increment returns the new value, so the first id handed out is 1 and 0 stays free as
        // the "allocation failed" sentinel AddSpawnedEnemy returns.
        private int currentEnemyId = 0;

        private const float POSITION_TRESHOLD = 0.1f;
        private const float YAW_TRESHOLD = 5.0f;
        private const ushort HP_TRESHOLD = 1;

        /// <summary>
        /// Server side, retarget enemies when a player dies (or other use case ? )
        /// </summary>
        public IEnumerable<(uint, uint)> ReTargetEnemies(uint oldTargetId, IEnumerable<uint> currentPlayersAliveExcludingOldOneId)
        {
            var retargetedEnemies = new List<(uint, uint)>();

            // FIX P1-6: materialise once, and bail out when there is nobody left to retarget onto.
            //
            // The loop below used to call .Count() then .ElementAt() on the sequence per enemy.
            // With an empty set that threw ArgumentOutOfRangeException — Random.Range(0, 0)
            // returns 0, and ElementAt(0) on an empty sequence throws — which aborted the caller
            // partway and skipped SpawnReviver. All three call sites (OnPlayerDied,
            // OnReceivedPlayerDied, OnReceivedPlayerDisconnected) were affected, so the guard
            // lives here rather than being repeated at each of them.
            //
            // Materialising also removes the per-enemy double walk of the sequence, which is the
            // O(enemies x players) cost noted in docs/netplay/04-performance-and-gc.md.
            var candidates = currentPlayersAliveExcludingOldOneId as IList<uint>
                             ?? currentPlayersAliveExcludingOldOneId?.ToList();

            if (candidates == null || candidates.Count == 0)
            {
                // Everyone is dead, or the last remaining player disconnected. There is no valid
                // target, so the caller's RetargetedEnemies message carries an empty list -
                // which receivers already handle.
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
            // PERF 04 item 4: this was a FirstOrDefault over the player list *per enemy* — with a
            // closure allocated each time — making the whole apply O(enemies x players). It runs on
            // the death and disconnect paths, at up to 600 enemies, which is a hitch at exactly the
            // worst moment. Index the players once instead.
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

        /// <summary>
        /// This should be called once per server tick
        /// </summary>
        /// <returns></returns>
        public IEnumerable<EnemyModel> GetAllEnemiesDeltaAndUpdate()
        {
            var currentEnemies = new Dictionary<uint, EnemyModel>(spawnedEnemies.Count);
            foreach (var (id, enemy) in spawnedEnemies)
            {
                currentEnemies[id] = enemy.ToModel(id);
            }

            if (previousSpawnedEnemiesDelta.Count == 0)
            {
                previousSpawnedEnemiesDelta = currentEnemies;
                return currentEnemies.Values;
            }

            var deltas = new List<EnemyModel>();

            foreach (var current in currentEnemies.Values)
            {
                if (!previousSpawnedEnemiesDelta.TryGetValue(current.Id, out var previous) || HasDelta(previous, current))
                {
                    deltas.Add(current);
                }
            }

            previousSpawnedEnemiesDelta = currentEnemies;

            return deltas;
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

            return positionDelta > POSITION_TRESHOLD ||
                   yawDelta > YAW_TRESHOLD ||
                   hpDelta > HP_TRESHOLD;
        }

        public Enemy GetEnemyById(uint id)
        {
            if (spawnedEnemies.TryGetValue(id, out var enemy))
            {
                return enemy;
            }
            return null;
        }


        /// <summary>
        /// Server side
        /// </summary>
        public uint AddSpawnedEnemy(Enemy enemy)
        {
            // FIX P0-3: allocate once, atomically, then use the local. The previous code did a
            // non-atomic read-modify-write and then re-read the shared field three more times,
            // so two concurrent spawns could collide on an id or skip one.
            var newId = (uint)Interlocked.Increment(ref currentEnemyId);

            if (!spawnedEnemies.TryAdd(newId, enemy))
            {
                Plugin.Log.LogWarning($"Attempted to add an enemy that already exists. EnemyId: {newId}");
                return 0;
            }

            DynamicData.For(enemy).Set("netplayId", newId);

            return newId;
        }

        /// <summary>
        /// Client side
        /// </summary>
        public void SetSpawnedEnemy(uint enemyId, Enemy enemy)
        {
            if (!spawnedEnemies.TryAdd(enemyId, enemy))
            {
                Plugin.Log.LogWarning($"Attempted to add an enemy that already exists. EnemyId: {enemyId}");
                return;
            }

            DynamicData.For(enemy).Set("netplayId", enemyId);
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
            if (!spawnedEnemies.TryRemove(id, out var enemy))
            {
                return;
            }
        }

        public void ResetForNextLevel()
        {
            //spawnedEnemies.Select(Enemy => Enemy.Value).ToList().ForEach(enemy => GameObject.Destroy(enemy.gameObject));
            spawnedEnemies.Clear();
            previousSpawnedEnemiesDelta = [];
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
    }
}

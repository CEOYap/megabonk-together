using Actors.Enemies;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Game.Spawning;
using Assets.Scripts.Managers;
using BepInEx.Logging;
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

        /// <summary>OB-11. Host only — see the implementation for why.</summary>
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
        private readonly ConcurrentDictionary<Enemy, string> reviverEnemies_NetplayNames = [];
        private readonly ConcurrentDictionary<uint, int> reviverSpawnCountPerOwner = [];
        // FIX P0-3: int rather than uint so Interlocked.Increment can allocate ids atomically.
        // Increment returns the new value, so the first id handed out is 1 and 0 stays free as
        // the "allocation failed" sentinel AddSpawnedEnemy returns.
        private int currentEnemyId = 0;

        private const float POSITION_TRESHOLD = 0.1f;
        private const float YAW_TRESHOLD = 5.0f;
        private const ushort HP_TRESHOLD = 1;

        #region Budgeted priority enemy stream

        /// <remarks>
        /// <para><b>The defect this replaces.</b> The delta was taken against the last <i>sampled</i>
        /// state and the baseline was overwritten unconditionally, so once an enemy stopped changing
        /// it was never sent again. If the packet carrying its final state was lost, the client
        /// stayed wrong about that enemy indefinitely — a permanent divergence from a single dropped
        /// datagram.</para>
        ///
        /// <para><b>Why the obvious fix does not work.</b> Tracking the last <i>sent</i> state
        /// instead is still not the last <i>acknowledged</i> state: these sends are fire-and-forget
        /// on an unreliable channel. No delta scheme is correct without acks, and acks would mean a
        /// per-client baseline and a per-client serialize every tick instead of one broadcast.
        /// Marking an enemy's final packet reliable does not work either — you only learn a packet
        /// was an enemy's last on the <i>following</i> tick, when it produces no delta.</para>
        ///
        /// <para><b>What this does instead: bound staleness rather than guarantee delivery.</b>
        /// Changed enemies are sent stalest-first up to a per-tick budget, and any spare budget
        /// refreshes enemies that have not been sent for a while. Nothing can then stay wrong
        /// indefinitely — worst case it is wrong for one refresh period and is then corrected. The
        /// hole closes by construction, with no acks, no reliable channel on the hot path, and no
        /// receive-side change: <c>OnReceivedEnemiesUpdate</c> feeds an interpolator and skips
        /// unknown ids, so a repeated unchanged state is a no-op.</para>
        ///
        /// <para><b>It also caps the big ticks.</b> A 600-enemy swarm previously produced one ~8.7 KB
        /// tick at 40 Hz. Excess changed enemies now defer one tick and come back first, being the
        /// stalest — so the per-enemy rate degrades gracefully instead of egress spiking (a
        /// level-159 capture peaked at 321.1 KB/s at two players).</para>
        ///
        /// <para><b>The refresh cost is counter-cyclical, which is what makes it affordable.</b> Only
        /// enemies staler than the refresh period are eligible, and only a period's worth per tick,
        /// so a busy field spends nothing on refresh and a quiet one spends a few hundred bytes.</para>
        ///
        /// <para><b>Deliberately not done: relevance ordering.</b> Under the cap it would be better
        /// to degrade enemies far from every player rather than whichever sort ordering lands last.
        /// <c>DistanceThrottler</c> already computes that, but only on the receiving peer's
        /// <c>Enemy.MyUpdate</c> — it has never gated the host's send. Adding it here is a strict
        /// refinement of the ordering below and can land on its own; it is left out so this change
        /// stays one variable in a playtest.</para>
        /// </remarks>
        private const int ENEMY_MODEL_WIRE_BYTES = 15;

        /// <summary>Roughly four sub-MTU datagrams — see UdpClientService.SendStreamUpdate.</summary>
        private const int MAX_ENEMY_TICK_BYTES = 3600;

        private const int MAX_ENEMIES_PER_TICK = MAX_ENEMY_TICK_BYTES / ENEMY_MODEL_WIRE_BYTES;

        /// <summary>
        /// How stale an unchanged enemy must be before the refresh sweep will re-send it, and the
        /// number of ticks the sweep spreads a full pass over. 40 ticks is ~1 s at the 40 Hz enemy
        /// tick rate, so a quiet enemy costs one re-send per second and no state survives being
        /// wrong for longer than that plus contention for the budget.
        /// </summary>
        private const int ENEMY_REFRESH_PERIOD_TICKS = 40;

        /// <summary>What was last put on the wire, not what was last sampled. See the remarks above.</summary>
        private Dictionary<uint, EnemyModel> lastSentEnemies = [];
        private Dictionary<uint, long> lastSentTick = [];
        private long enemyStreamTick;

        // Reused across ticks rather than reallocated: this runs at 40 Hz over hundreds of enemies,
        // and per-tick collections in a hot path are exactly what docs/netplay/04-performance-and-gc.md
        // is about. Safe to hand `selected` back to the caller because SendEnemiesUpdate materialises
        // it into its own list before the next tick can run.
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

        /// <summary>Staleness precomputed into the struct so the comparison does no dictionary lookups.</summary>
        private static readonly System.Comparison<Candidate> StalestFirst =
            (a, b) => b.Staleness.CompareTo(a.Staleness);

        #endregion

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
                // An enemy we have never sent is maximally stale, so it sorts ahead of everything
                // and cannot be starved by a field that is busy the moment it spawns.
                var staleness = lastSentTick.TryGetValue(current.Id, out var sentAt)
                    ? enemyStreamTick - sentAt
                    : long.MaxValue;

                if (!lastSentEnemies.TryGetValue(current.Id, out var previous) || HasDelta(previous, current))
                {
                    changed.Add(new Candidate(staleness, current));
                }
                else if (staleness >= ENEMY_REFRESH_PERIOD_TICKS)
                {
                    // Only stale-enough enemies are refresh candidates. Without this a field below
                    // the budget would re-send every enemy every tick, which costs more than the
                    // delta stream it replaced rather than less.
                    unchanged.Add(new Candidate(staleness, current));
                }
            }

            selected.Clear();

            // Changed first: a real state change is always worth more than a refresh of state the
            // client most likely already has. Anything that does not fit defers one tick, and comes
            // back at the front next tick because deferring is what made it the stalest.
            changed.Sort(StalestFirst);
            TakeUpTo(changed, MAX_ENEMIES_PER_TICK);

            // Spend what is left on the refresh sweep, capped to one period's share so a full pass
            // is spread over ENEMY_REFRESH_PERIOD_TICKS rather than landing in one tick.
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

        /// <summary>
        /// Drops per-enemy stream bookkeeping for enemies that no longer exist. Without this both
        /// dictionaries grow for the lifetime of a stage — ids are never reused within one — and the
        /// refresh sweep would keep scoring entries that can never be sent again.
        /// </summary>
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

        /// <summary>
        /// The floor for the re-rolled ring radius, in world units.
        ///
        /// <para>Only reached in the degenerate case: when every one of the game's 50 placement
        /// attempts fails, <c>SummonerController.EventMiniboss</c> falls back to
        /// <c>MyPlayer.Instance.transform.position</c> and the miniboss spawns *inside* the host.
        /// The derived radius is then ~0, and without this floor the correction would faithfully
        /// reproduce the bug on somebody else. <b>The value is a guess and is UNVERIFIED</b> — the
        /// game's own distances live in unnamed float constants that were not decompiled.</para>
        /// </summary>
        private const float MIN_BOSS_RING_RADIUS = 20f;

        /// <summary>How wide a band to give the re-rolled ring, as a fraction of the radius.</summary>
        private const float BOSS_RING_BAND = 0.1f;

        /// <summary>Matches the attempt count the game itself passes on this path.</summary>
        private const int BOSS_RING_ATTEMPTS = 50;

        /// <summary>Reused across calls — boss spawns are rare, but this costs nothing to keep.</summary>
        private readonly List<Vector3> bossSpawnCandidates = [];

        /// <summary>
        /// OB-11: pick a player at random and re-centre a miniboss spawn on them.
        ///
        /// <para><b>The cause is confirmed by decompile, not inferred.</b>
        /// <c>SpawnPositions.GetEnemySpawnPositionBiased</c> (VA <c>0x180482390</c>) centres its ring
        /// on <c>MyPlayer.Instance.playerMovement.rigidbody.position + velocity * t</c> and biases the
        /// direction by <c>MyPlayer.Instance.averageMovingDirection</c>. Every term is the local
        /// player and there is no second player anywhere in the body — <c>GameManager.Instance</c>
        /// appears only to supply a layer mask for the ground raycast. The game is single-player and
        /// its spawner knows about one player; on the host that is the host. Full chain in
        /// <c>docs/netplay/08-observed-bugs.md#ob-11</c>.</para>
        ///
        /// <para><b>Why this runs before the spawn rather than after it.</b> <c>SpawnBoss</c> takes
        /// the position as a <i>parameter</i>, so correcting it in the prefix means the enemy is
        /// created at the corrected point. Repositioning afterwards would leave anything the game
        /// cached from its spawn transform in <c>Awake</c> stale — the hazard the
        /// clone-inactive-then-position note in <see cref="SynchronizationService"/> already exists
        /// because of.</para>
        ///
        /// <para><b>No wire change, and no new desync surface.</b> The host corrects the position
        /// before spawning and <c>SpawnEnemy_Postfix</c> replicates the corrected value verbatim.
        /// Clients never spawn bosses — <c>SpawnBoss_Prefix</c> suppresses that — so there is still
        /// one miniboss with one id, agreed by both ends. This must stay host-only for exactly that
        /// reason: two peers each picking their own player would produce two minibosses.</para>
        ///
        /// <para><b>Every failure path returns <paramref name="pos"/> unchanged</b>, which is
        /// vanilla behaviour. This is a fairness correction, not a mechanic — it must never be the
        /// reason a miniboss fails to spawn.</para>
        ///
        /// <para><b>UNVERIFIED.</b> Not run in-game. Specifically unproven: that
        /// <c>GetEnemySpawnPositionAroundPoint</c> is safe to call from inside a prefix on this path,
        /// that <see cref="MIN_BOSS_RING_RADIUS"/> is a sensible distance, and that a miniboss placed
        /// around a non-host player never lands somewhere the host's own camera or pathing logic
        /// dislikes.</para>
        /// </summary>
        public Vector3 RecentreBossSpawnOnRandomPlayer(Vector3 pos)
        {
            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.player == null)
            {
                return pos;
            }

            // GameManager.Instance.player is the same object the spawner reads as MyPlayer.Instance;
            // this is the idiom the rest of the plugin already uses for the local player.
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

                bossSpawnCandidates.Add(netPlayer.transform.position);
            }

            if (bossSpawnCandidates.Count == 0)
            {
                return pos;
            }

            // The local player is one candidate among the peers, so with N players each takes a 1/N
            // share of minibosses. Index Count is the local player's slot: drawing it means the
            // game's own host-centred position was the right answer this time and nothing is done.
            var pick = UnityEngine.Random.Range(0, bossSpawnCandidates.Count + 1);
            if (pick == bossSpawnCandidates.Count)
            {
                return pos;
            }

            var target = bossSpawnCandidates[pick];

            // Reuse the distance the game just chose rather than inventing one: it scales with run
            // time through constants that were not decompiled, and copying the result preserves that
            // curve for free. Measured in XZ because the ring is horizontal.
            var dx = pos.x - localPosition.x;
            var dz = pos.z - localPosition.z;
            var radius = Mathf.Sqrt((dx * dx) + (dz * dz));
            if (radius < MIN_BOSS_RING_RADIUS)
            {
                radius = MIN_BOSS_RING_RADIUS;
            }

            var recentred = SpawnPositions.GetEnemySpawnPositionAroundPoint(
                target,
                radius * (1f - BOSS_RING_BAND),
                radius * (1f + BOSS_RING_BAND),
                BOSS_RING_ATTEMPTS,
                true,
                999f);

            // The game signals "no position found" by returning INVALID_POS rather than by failing,
            // and EventMiniboss itself compares against it with an epsilon rather than for equality.
            var invalid = SpawnPositions.INVALID_POS;
            var offset = recentred - invalid;
            if (offset.sqrMagnitude < 0.01f)
            {
                logger.LogDebug($"OB-11: no valid miniboss position around peer; keeping the host-centred one.");
                return pos;
            }

            return recentred;
        }

        #endregion
    }
}

using Assets.Scripts.Actors.Enemies;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using UnityEngine;

namespace OmniBonk.Scripts.Enemies
{
    public class TargetSwitcher : MonoBehaviour
    {
        private Enemy enemy;
        private float timer = 0f;
        private float delay = 0f;
        private float switchMaxDistance = 100f;
        private (Transform transform, Rigidbody rigidBody) currentTarget = (null, null);

        private uint currentTargetNetplayId = 0;
        private (float min, float max) switchIntervalRange = (2f, 6f);
        private IPlayerManagerService playerManagerService;
        private ISynchronizationService synchronizationService;
        private DynamicData enemyData;
        // PERF 1C: cached; `enemy.transform` is an interop call. Refreshed wherever `enemy` changes.
        private Transform enemyTransform;

        private void Awake()
        {
            playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
            synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        }

        public uint StartSwitching(Enemy targetEnemy, bool pickACloseTarget = false)
        {
            enemy = targetEnemy;
            enemyData = DynamicData.For(enemy);
            enemyTransform = targetEnemy.transform;
            ResetTimer();

            // PERF 1A: also registered here in case Il2CppInterop never fires OnEnable (unverified). Idempotent.
            TargetSwitcherManager.Register(this);

            if (pickACloseTarget)
            {
                PickACloseTarget();
            }
            else
            {
                PickANewTarget();
            }

            return currentTargetNetplayId;
        }

        public void UpdateSwitchIntervalRange(float minSeconds, float maxSeconds)
        {
            switchIntervalRange = (minSeconds, maxSeconds);
        }

        public void UpdateSwitchMaxDistance(float distance)
        {
            switchMaxDistance = distance;
        }

        private void PickANewTarget()
        {
            // FIX P1-4: shared buffer, no allocation. Safe: main thread, nothing below re-enters it.
            var alives = playerManagerService.GetAllPlayersAliveNonAlloc();
            if (alives.Count == 0) return;

            var selectedPlayer = alives[Random.Range(0, alives.Count)];

            if (playerManagerService.IsRemoteConnectionId(selectedPlayer.ConnectionId))
            {
                // Alive does not imply spawned (e.g. mid-disconnect).
                var netplayer = playerManagerService.GetNetPlayerByNetplayId(selectedPlayer.ConnectionId);
                if (netplayer == null || netplayer.Model == null)
                {
                    return; // keep the existing target rather than clearing it
                }

                currentTarget = (netplayer.Model.transform, netplayer.Rigidbody);
            }
            else
            {
                currentTarget = (GameManager.Instance.player.transform, GameManager.Instance.player.playerMovement.rb);
            }

            currentTargetNetplayId = selectedPlayer.ConnectionId;
        }

        private void PickACloseTarget()
        {
            // FIX P1-4: as above.
            var alives = playerManagerService.GetAllPlayersAliveNonAlloc();
            if (alives.Count == 0) return;

            if (enemyTransform == null)
            {
                return;
            }

            // PERF 1C: one native position read; squared distances.
            var enemyPosition = enemyTransform.position;

            var closestSqrDistance = float.MaxValue;
            (Transform transform, Rigidbody rigidBody) closestTarget = (null, null);

            uint closestNetplayId = 0;
            foreach (var player in alives)
            {
                (Transform transform, Rigidbody rigidBody) target;
                if (playerManagerService.IsRemoteConnectionId(player.ConnectionId))
                {
                    // Same guard as PickANewTarget: alive does not imply spawned.
                    var netplayer = playerManagerService.GetNetPlayerByNetplayId(player.ConnectionId);
                    if (netplayer == null || netplayer.Model == null)
                    {
                        continue;
                    }

                    target = (netplayer.Model.transform, netplayer.Rigidbody);
                }
                else
                {
                    target = (GameManager.Instance.player.transform, GameManager.Instance.player.playerMovement.rb);
                }

                var sqrDistance = (enemyPosition - target.transform.position).sqrMagnitude;
                if (sqrDistance < closestSqrDistance)
                {
                    closestSqrDistance = sqrDistance;
                    closestTarget = target;
                    closestNetplayId = player.ConnectionId;
                }
            }

            if (closestTarget.transform == null)
            {
                return; // nobody resolvable; leave whatever target was already set
            }

            currentTarget = closestTarget;
            currentTargetNetplayId = closestNetplayId;
        }

        /// <summary>Index in <see cref="TargetSwitcherManager"/>'s registry (for O(1) removal), or -1.</summary>
        internal int RegistryIndex { get; set; } = -1;

        internal uint CurrentTargetNetplayId => currentTargetNetplayId;

        /// <summary>
        /// Moves this enemy off a departed player; true if the target changed. Bypasses
        /// <see cref="CanSwitch"/>: a distant target beats a destroyed Rigidbody.
        /// <para>With no replacement, <c>enemy.target</c> is nulled. <b>UNVERIFIED:</b> that the game
        /// tolerates a null <c>Enemy.target</c>.</para>
        /// </summary>
        internal bool RetargetAwayFromDepartedPlayer()
        {
            if (enemy == null)
            {
                return false;
            }

            var departedId = currentTargetNetplayId;

            PickANewTarget();

            if (currentTarget.transform == null || currentTargetNetplayId == departedId)
            {
                currentTarget = (null, null);
                enemy.target = null;
                return false;
            }

            enemyData.Set("targetId", currentTargetNetplayId);
            enemy.target = currentTarget.rigidBody;
            return true;
        }

        /// <summary>PERF 1A: a deactivated pooled enemy stops ticking, as with a real <c>Update</c>.</summary>
        private void OnEnable()
        {
            TargetSwitcherManager.Register(this);
        }

        private void OnDisable()
        {
            TargetSwitcherManager.Unregister(this);
        }

        /// <summary>OnDestroy is known to fire on injected types here; the manager also drops Unity-null entries.</summary>
        private void OnDestroy()
        {
            TargetSwitcherManager.Unregister(this);
        }

        /// <summary>Driven by <see cref="TargetSwitcherManager"/> instead of a per-enemy <c>Update</c>.</summary>
        internal void Tick(float deltaTime)
        {
            if (enemy == null) return;

            timer += deltaTime;
            if (timer >= delay)
            {
                if (!synchronizationService.HasNetplaySessionStarted()) return;

                PickANewTarget();
                if (currentTarget.transform == null)
                {
                    ResetTimer();
                    return;
                }
                if (CanSwitch())
                {
                    enemyData.Set("targetId", currentTargetNetplayId);
                    enemy.target = currentTarget.rigidBody;
                }
                ResetTimer();
            }
        }

        private bool CanSwitch()
        {
            if (enemyTransform == null) return false;

            // PERF 1C: cached transform, squared comparison.
            float sqrDistance = (enemyTransform.position - currentTarget.transform.position).sqrMagnitude;
            return sqrDistance <= switchMaxDistance * switchMaxDistance;
        }

        private void ResetTimer()
        {
            timer = 0f;
            delay = Random.Range(switchIntervalRange.min, switchIntervalRange.max);
        }
    }
}

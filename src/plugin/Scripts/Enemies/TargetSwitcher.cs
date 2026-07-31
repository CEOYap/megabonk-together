using Assets.Scripts.Actors.Enemies;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using UnityEngine;

namespace MegabonkTogether.Scripts.Enemies
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

        private void Awake()
        {
            playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
            synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        }

        public uint StartSwitching(Enemy targetEnemy, bool pickACloseTarget = false)
        {
            enemy = targetEnemy;
            enemyData = DynamicData.For(enemy);
            ResetTimer();

            // PERF 1A: belt-and-braces. OnEnable below is the intended registration hook, but no
            // other injected MonoBehaviour in this repo relies on OnEnable, so there is no local
            // evidence Il2CppInterop wires it. If it does not fire, nothing would ever register and
            // target switching would silently stop entirely — the exact silent-dead-component
            // failure the il2cpp skill warns about. StartSwitching is called unconditionally from
            // Enemy.init_PostFix, so registering here too makes that impossible. Register is
            // idempotent, so the overlap is free.
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
            // FIX P1-4: this runs per enemy on a 2-6s timer, so at a full swarm it was the mod's
            // worst allocation site — a Player[], two LINQ iterators and a List copy per call.
            // The buffer is safe here: Update is main thread, and nothing below re-enters it.
            var alives = playerManagerService.GetAllPlayersAliveNonAlloc();
            if (alives.Count == 0) return;

            var selectedPlayer = alives[Random.Range(0, alives.Count)];

            if (playerManagerService.IsRemoteConnectionId(selectedPlayer.ConnectionId))
            {
                var netplayer = playerManagerService.GetNetPlayerByNetplayId(selectedPlayer.ConnectionId);
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
            // FIX P1-4: as above. The loop below only reads the buffer and calls lookups that do
            // not re-enter it, so holding it across the iteration is safe.
            var alives = playerManagerService.GetAllPlayersAliveNonAlloc();
            if (alives.Count == 0) return;

            var closestDistance = float.MaxValue;
            (Transform transform, Rigidbody rigidBody) closestTarget = (null, null);
            
            uint closestNetplayId = 0;
            foreach (var player in alives)
            {
                (Transform transform, Rigidbody rigidBody) target;
                if (playerManagerService.IsRemoteConnectionId(player.ConnectionId))
                {
                    var netplayer = playerManagerService.GetNetPlayerByNetplayId(player.ConnectionId);
                    target = (netplayer.Model.transform, netplayer.Rigidbody);
                }
                else
                {
                    target = (GameManager.Instance.player.transform, GameManager.Instance.player.playerMovement.rb);
                }

                var distance = Vector3.Distance(enemy.transform.position, target.transform.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestTarget = target;
                    closestNetplayId = player.ConnectionId;
                }
            }
            currentTarget = closestTarget;
            currentTargetNetplayId = closestNetplayId;
        }

        /// <summary>
        /// Position in <see cref="TargetSwitcherManager"/>'s registry, or -1 when unregistered.
        /// Stored here so removal is O(1) rather than a linear search.
        /// </summary>
        internal int RegistryIndex { get; set; } = -1;

        /// <summary>
        /// 04-performance-and-gc.md item 1A: registration follows Unity's own enable/disable, so a
        /// pooled enemy being deactivated stops ticking exactly as it did when this was a real
        /// <c>Update</c>.
        /// </summary>
        private void OnEnable()
        {
            TargetSwitcherManager.Register(this);
        }

        private void OnDisable()
        {
            TargetSwitcherManager.Unregister(this);
        }

        /// <summary>
        /// Unlike OnEnable/OnDisable, private OnDestroy is already used by injected types in this
        /// repo (NetPlayer, NetPlayersDisplayer), so this path is known to work. The manager also
        /// drops Unity-null entries defensively when it ticks.
        /// </summary>
        private void OnDestroy()
        {
            TargetSwitcherManager.Unregister(this);
        }

        /// <summary>
        /// Was <c>Update()</c>. Driven by <see cref="TargetSwitcherManager"/> instead, so the
        /// managed↔native crossing happens once per frame for all enemies rather than once per
        /// enemy. Body is unchanged apart from taking the delta as a parameter.
        /// </summary>
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
            if (enemy.transform == null) return false;

            float distance = Vector3.Distance(enemy.transform.position, currentTarget.transform.position);
            return distance <= switchMaxDistance;
        }

        private void ResetTimer()
        {
            timer = 0f;
            delay = Random.Range(switchIntervalRange.min, switchIntervalRange.max);
        }
    }
}

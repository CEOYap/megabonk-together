using Assets.Scripts.Actors.Enemies;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using System.Linq;
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

        private void Update()
        {
            if (enemy == null) return;

            timer += Time.deltaTime;
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

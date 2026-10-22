using OmniBonk.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// Throttles far entities' updates. OB-30: does not hide renderers (the cutoff was visible).
    /// Client enemies still move via <c>EnemyInterpolatorManager</c>.
    /// </summary>
    public class DistanceThrottler(float mediumDistanceUpdateInterval = Tunables.MEDIUM_DISTANCE_UPDATE_INTERVAL_SECONDS)
    {
        private readonly Dictionary<int, float> lastUpdateTimes = [];
        private readonly float mediumDistanceUpdateInterval = mediumDistanceUpdateInterval;

        public bool ShouldUpdate(GameObject gameObject, int instanceId, bool isServer = false)
        {
            DistanceToPlayer distance = Plugin.GetDistanceToPlayer(gameObject.transform.position);
            return ShouldUpdate(gameObject, instanceId, distance, isServer);
        }

        private bool ShouldUpdate(GameObject gameObject, int instanceId, DistanceToPlayer distance, bool isServer = false)
        {
            switch (distance)
            {
                case DistanceToPlayer.Far:
                    return isServer;

                case DistanceToPlayer.Medium:
                    if (isServer)
                    {
                        return true;
                    }

                    float currentTime = Time.time;
                    if (!lastUpdateTimes.TryGetValue(instanceId, out float lastTime) ||
                        currentTime - lastTime >= mediumDistanceUpdateInterval)
                    {
                        lastUpdateTimes[instanceId] = currentTime;
                        return true;
                    }

                    return false;

                case DistanceToPlayer.Close:
                default:
                    return true;
            }
        }

        public void Cleanup(int instanceId)
        {
            lastUpdateTimes.Remove(instanceId);
        }

        public void ClearAll()
        {
            lastUpdateTimes.Clear();
        }
    }
}

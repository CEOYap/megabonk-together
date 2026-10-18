using MegabonkTogether.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Throttles per-frame updates of far entities. It no longer hides them.
    ///
    /// <para><b>OB-30: renderers are left alone.</b> This used to set the first child
    /// <c>Renderer</c>'s <c>enabled = false</c> beyond <see cref="Tunables.DISTANCE_MEDIUM"/>
    /// (60 units) and back on inside it, so in netplay every non-boss enemy vanished past 60 units
    /// and popped back in when it came closer, with a hard edge that flickered at the boundary. 60
    /// units is inside what the camera shows, so the cutoff was visible. The update skip is where
    /// the CPU saving is and it stays; the renderer toggle only saved draw calls, which Unity's own
    /// frustum culling already handles for anything off-screen.</para>
    ///
    /// <para>Client enemies beyond 60 still move: their position comes from
    /// <c>EnemyInterpolatorManager</c>, which ticks every enemy whatever its distance, not from the
    /// skipped <c>MyUpdate</c>.</para>
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

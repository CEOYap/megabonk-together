using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Scripts.Snapshot
{
    /// <summary>
    /// PERF 1A for <see cref="EnemyInterpolator"/>: one <c>Update</c> ticks every client interpolator,
    /// avoiding ~600 IL2CPP managed/native crossings per frame. <b>Unmeasured</b>.
    /// </summary>
    public class EnemyInterpolatorManager : MonoBehaviour
    {
        /// <summary>Sized past the 600-enemy swarm so the list does not grow mid-run.</summary>
        private static readonly List<EnemyInterpolator> interpolators = new(700);

        /// <summary>Idempotent: enemies are pooled, so the same component re-enables many times.</summary>
        internal static void Register(EnemyInterpolator interpolator)
        {
            if (interpolator == null || interpolator.RegistryIndex >= 0)
            {
                return;
            }

            interpolator.RegistryIndex = interpolators.Count;
            interpolators.Add(interpolator);
        }

        /// <summary>O(1) swap-remove; despawns are frequent enough at a swarm for O(n) to matter.</summary>
        internal static void Unregister(EnemyInterpolator interpolator)
        {
            if (interpolator == null)
            {
                return;
            }

            var index = interpolator.RegistryIndex;
            interpolator.RegistryIndex = -1;

            if (index < 0 || index >= interpolators.Count || interpolators[index] != interpolator)
            {
                return;
            }

            RemoveAt(index);
        }

        /// <summary>Separate from <see cref="Unregister"/>, whose null guard rejects destroyed (Unity-null) entries.</summary>
        private static void RemoveAt(int index)
        {
            var last = interpolators.Count - 1;
            if (index != last)
            {
                interpolators[index] = interpolators[last];
                if (interpolators[index] != null)
                {
                    interpolators[index].RegistryIndex = index;
                }
            }

            interpolators.RemoveAt(last);
        }

        internal static void Clear()
        {
            foreach (var interpolator in interpolators)
            {
                if (interpolator != null)
                {
                    interpolator.RegistryIndex = -1;
                }
            }

            interpolators.Clear();
        }

        public void Update()
        {
            if (interpolators.Count == 0)
            {
                return; // host, singleplayer, or no enemies yet — one no-op call per frame
            }

            var renderTime = Time.timeAsDouble;

            // Backwards, so swap-remove during a tick only moves already-ticked entries.
            for (var i = interpolators.Count - 1; i >= 0; i--)
            {
                if (i >= interpolators.Count)
                {
                    continue; // the list shrank underneath us
                }

                var interpolator = interpolators[i];
                if (interpolator == null)
                {
                    RemoveAt(i);
                    continue;
                }

                try
                {
                    interpolator.Tick(renderTime);
                }
                catch (System.Exception ex)
                {
                    // P1-7: one bad enemy must not stop the rest.
                    Plugin.Log.LogWarning($"EnemyInterpolator tick failed: {ex.Message}");
                }
            }
        }
    }
}

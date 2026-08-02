using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Scripts.Snapshot
{
    /// <summary>
    /// The same fix PERF 1A applied to <see cref="Enemies.TargetSwitcher"/>, for
    /// <see cref="EnemyInterpolator"/> — which has the identical shape and was missed.
    ///
    /// <para>An interpolator is added to every enemy <b>on the client</b>
    /// (`SynchronizationService.OnReceivedSpawnedEnemy`), so at a full swarm the client was making
    /// ~600 managed <c>Update()</c> calls per frame, each crossing the IL2CPP managed↔native
    /// boundary — the crossing 1A identified as the dominant cost, and this one lands on the peer
    /// that is already doing the most work per frame. Most of those calls returned immediately
    /// because the snapshot buffer held fewer than two entries.</para>
    ///
    /// <para>This ticks every registered interpolator from one <c>Update</c>. The per-interpolator
    /// work becomes an ordinary C# call.</para>
    ///
    /// <para><b>Unmeasured</b>, like 1A: no profiler capture has been taken. The reasoning is
    /// structural and the pattern is the one already shipped for target switchers.</para>
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

        /// <summary>
        /// Separate from <see cref="Unregister"/> for the same reason as the switcher registry: a
        /// destroyed interpolator compares equal to null through Unity's operator, so Unregister's
        /// null guard would reject it and leave the dead entry in place forever.
        /// </summary>
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

        /// <summary>Drops every registration so a new session starts clean.</summary>
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

            // One native Time read for the whole sweep instead of one per enemy.
            var renderTime = Time.timeAsDouble;

            // Backwards: if a tick causes an unregister, swap-remove moves a later element into
            // the freed slot, and going backwards means that slot is already done.
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
                    // One bad enemy must not stop every other enemy interpolating for the rest of
                    // the run — the P1-7 lesson, applied to a loop that runs every frame.
                    Plugin.Log.LogWarning($"EnemyInterpolator tick failed: {ex.Message}");
                }
            }
        }
    }
}

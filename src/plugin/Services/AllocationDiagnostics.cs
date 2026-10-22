using OmniBonk.Configuration;
using System;
using UnityEngine;

namespace OmniBonk.Services
{
    /// <summary>
    /// Managed (mod-only) allocation-rate sampler; the Unity Profiler cannot attach to a retail build.
    /// <para>Expected ceiling for EnemyModel churn: 189 enemies/tick x 40 Hz = ~7,560/s. Compare a full
    /// swarm against an empty field. Do not pool before measuring.</para>
    /// </summary>
    internal static class AllocationDiagnostics
    {
        private const float REPORT_INTERVAL_SECONDS = Tunables.DIAGNOSTIC_REPORT_INTERVAL_SECONDS;

        private static float lastReportTime = -999f;
        private static long lastAllocatedBytes;
        private static int lastGen0;
        private static int lastGen1;
        private static int lastGen2;
        private static bool primed;

        /// <summary>Called once per frame from the network tick. Cheap enough to sit there.</summary>
        internal static void Sample(bool enabled)
        {
            if (!enabled)
            {
                return;
            }

            var now = Time.unscaledTime;

            if (!primed)
            {
                Prime(now);
                return;
            }

            var elapsed = now - lastReportTime;
            if (elapsed < REPORT_INTERVAL_SECONDS)
            {
                return;
            }

            var allocated = GC.GetTotalAllocatedBytes(precise: false);
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);

            var bytesPerSecond = (allocated - lastAllocatedBytes) / elapsed;

            Plugin.Log.LogInfo(
                $"[alloc] {bytesPerSecond / 1024f:F0} KB/s managed over {elapsed:F1}s " +
                $"(gen0 +{gen0 - lastGen0}, gen1 +{gen1 - lastGen1}, gen2 +{gen2 - lastGen2}, " +
                $"heap {GC.GetTotalMemory(false) / (1024f * 1024f):F1} MB). " +
                "Mod allocations only - IL2CPP game allocations are not counted.");

            lastReportTime = now;
            lastAllocatedBytes = allocated;
            lastGen0 = gen0;
            lastGen1 = gen1;
            lastGen2 = gen2;
        }

        private static void Prime(float now)
        {
            primed = true;
            lastReportTime = now;
            lastAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            lastGen0 = GC.CollectionCount(0);
            lastGen1 = GC.CollectionCount(1);
            lastGen2 = GC.CollectionCount(2);
        }

        /// <summary>Re-primes so a new session's first window is not measured against the old one.</summary>
        internal static void Reset()
        {
            primed = false;
            lastReportTime = -999f;
        }
    }
}

using System;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Managed allocation-rate sampler — the measurement `09-performance-audit.md` needs, without a
    /// Unity Profiler.
    ///
    /// <para><b>Why this exists.</b> That document's first recommendation was a Unity Profiler
    /// GC-Alloc capture. The Unity Profiler can only attach to a **development** player, and
    /// Megabonk ships a retail IL2CPP build — so that capture cannot be taken, and every allocation
    /// claim in the audit was going to stay unmeasured indefinitely. BepInEx 6 runs the plugin on
    /// .NET 6, and the CoreCLR GC counts our own allocations, which is exactly the population in
    /// question: `EnemyModel` churn, the per-tick delta dictionary, LINQ closures. Game-side IL2CPP
    /// allocations are invisible here, and that is the point — this measures the mod, not the
    /// game.</para>
    ///
    /// <para>Reports once per <see cref="REPORT_INTERVAL_SECONDS"/> and only while a session is
    /// running, in the same throttled shape as the other diagnostics in this build. Reading
    /// <see cref="GC.GetTotalAllocatedBytes(bool)"/> imprecisely is a cheap counter read, so the
    /// sampler cannot meaningfully distort what it is measuring.</para>
    ///
    /// <para>What to do with the number: at 40 Hz on the host, `GetAllEnemiesDeltaAndUpdate`
    /// allocates one `EnemyModel` per enemy per tick. At 600 enemies that predicts roughly 24,000
    /// objects a second. If the reported rate at a full swarm is in that region and drops when the
    /// swarm ends, the audit's hypothesis is confirmed and pooling those models is worth doing. If
    /// it is far lower, the hypothesis is wrong and the effort belongs elsewhere.</para>
    /// </summary>
    internal static class AllocationDiagnostics
    {
        private const float REPORT_INTERVAL_SECONDS = 10f;

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
                "Mod allocations only — IL2CPP game allocations are not counted.");

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

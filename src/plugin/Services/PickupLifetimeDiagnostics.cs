using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace OmniBonk.Services
{
    /// <summary>
    /// OB-5: classifies "pickup not found" as never-registered (a replication gap) or
    /// registered-then-removed (consumed first; harmless). Throttled, silent when healthy, and
    /// deliberately not behind a config flag.
    /// </summary>
    internal static class PickupLifetimeDiagnostics
    {
        private const double REPORT_INTERVAL_SECONDS = 10d;

        /// <summary>Every id registered this session, including removed ones.</summary>
        private static readonly ConcurrentDictionary<uint, byte> everRegistered = new();

        private static readonly ConcurrentDictionary<string, int> neverSeen = new();
        private static readonly ConcurrentDictionary<string, int> seenThenRemoved = new();
        private static long lastReportTicks;

        internal static void RecordRegistered(uint pickupId)
        {
            if (pickupId != 0)
            {
                everRegistered[pickupId] = 0;
            }
        }

        /// <summary><paramref name="site"/> names the handler, so the report shows which path dominates.</summary>
        internal static void RecordMiss(string site, uint pickupId)
        {
            var bucket = everRegistered.ContainsKey(pickupId) ? seenThenRemoved : neverSeen;
            bucket.AddOrUpdate(site, 1, (_, prev) => prev + 1);

            Report();
        }

        private static void Report()
        {
            var nowTicks = System.DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref lastReportTicks);

            if (last != 0
                && new System.TimeSpan(nowTicks - last).TotalSeconds < REPORT_INTERVAL_SECONDS)
            {
                return;
            }

            // Only the thread that wins the swap reports, so concurrent misses cannot double-log.
            if (Interlocked.CompareExchange(ref lastReportTicks, nowTicks, last) != last)
            {
                return;
            }

            if (last == 0)
            {
                return; // first call only primes the throttle
            }

            var never = Drain(neverSeen);
            var removed = Drain(seenThenRemoved);

            if (never.Length == 0 && removed.Length == 0)
            {
                return;
            }

            var neverTotal = never.Sum(kv => kv.Value);
            var removedTotal = removed.Sum(kv => kv.Value);

            Plugin.Log.LogWarning(
                $"[pickup] {neverTotal + removedTotal} unresolved pickup instruction(s) in the last " +
                $"~{REPORT_INTERVAL_SECONDS:F0}s - " +
                $"NEVER-REGISTERED {neverTotal} [{Describe(never)}], " +
                $"registered-then-removed {removedTotal} [{Describe(removed)}]. " +
                "NEVER-REGISTERED means the spawn did not reach this peer and is the replication " +
                "gap worth chasing; registered-then-removed is a consumed pickup and is expected. " +
                "See OB-5.");
        }

        private static System.Collections.Generic.KeyValuePair<string, int>[] Drain(
            ConcurrentDictionary<string, int> counters)
        {
            var snapshot = counters.ToArray();
            foreach (var kv in snapshot)
            {
                counters.TryRemove(kv.Key, out _);
            }

            return snapshot;
        }

        private static string Describe(System.Collections.Generic.KeyValuePair<string, int>[] rows)
        {
            return rows.Length == 0
                ? "none"
                : string.Join(", ", rows.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"));
        }

        /// <summary>The id set must be cleared: ids restart per session.</summary>
        internal static void Reset()
        {
            everRegistered.Clear();
            neverSeen.Clear();
            seenThenRemoved.Clear();
            Interlocked.Exchange(ref lastReportTicks, 0);
        }
    }
}

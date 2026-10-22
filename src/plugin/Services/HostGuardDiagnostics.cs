using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace OmniBonk.Services
{
    /// <summary>
    /// OB-10: counts rejected host-only sends by message type (which names the patch missing its
    /// ownership check) and reports them throttled. A healthy session totals 0.
    /// </summary>
    internal static class HostGuardDiagnostics
    {
        private const double REPORT_INTERVAL_SECONDS = 10d;

        private static readonly ConcurrentDictionary<string, int> rejections = new();
        private static long lastReportTicks;
        private static long sessionTotal;

        /// <summary><paramref name="messageTypeName"/> may be null; then counted under the send method.</summary>
        internal static void Record(string transport, string sendMethod, string messageTypeName)
        {
            var key = string.IsNullOrEmpty(messageTypeName)
                ? $"{transport}/{sendMethod}"
                : $"{transport}/{sendMethod}/{messageTypeName}";

            rejections.AddOrUpdate(key, 1, (_, prev) => prev + 1);
            Interlocked.Increment(ref sessionTotal);

            Report();
        }

        private static void Report()
        {
            var nowTicks = System.DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref lastReportTicks);
            var elapsed = new System.TimeSpan(nowTicks - last).TotalSeconds;

            if (last != 0 && elapsed < REPORT_INTERVAL_SECONDS)
            {
                return;
            }

            // Only the swap winner reports, so concurrent threads cannot double-log.
            if (Interlocked.CompareExchange(ref lastReportTicks, nowTicks, last) != last)
            {
                return;
            }

            if (last == 0)
            {
                return; // first call only primes the throttle
            }

            LogPending($"in the last ~{REPORT_INTERVAL_SECONDS:F0}s");
        }

        private static void LogPending(string window)
        {
            var snapshot = rejections.ToArray();
            if (snapshot.Length == 0)
            {
                return;
            }

            foreach (var kv in snapshot)
            {
                rejections.TryRemove(kv.Key, out _);
            }

            var total = snapshot.Sum(kv => kv.Value);
            var ranked = string.Join(", ", snapshot
                .OrderByDescending(kv => kv.Value)
                .Take(6)
                .Select(kv => $"{kv.Key} x{kv.Value}"));

            Plugin.Log.LogWarning(
                $"[host-guard] {total} host-only send(s) rejected on this peer {window}: {ranked}. " +
                "Each names a path missing an ownership check - see OB-10. The send was dropped, " +
                "so nothing is broken, but the work leading up to it was done for nothing.");
        }

        /// <summary>Flushes held counts and always prints the session total (even 0), then clears.</summary>
        internal static void Reset()
        {
            LogPending("since the last report, at session end");

            var total = Interlocked.Exchange(ref sessionTotal, 0);
            Plugin.Log.LogInfo($"[host-guard] session total: {total} host-only send(s) rejected on this peer.");

            rejections.Clear();
            Interlocked.Exchange(ref lastReportTicks, 0);
        }
    }
}

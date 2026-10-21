using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// OB-10. Attributes the host-only guard rejections that both transports were reporting
    /// anonymously, and collapses them into one throttled line.
    ///
    /// <para><b>The problem this replaces.</b> A client repeatedly enters a host-only send path,
    /// the guard drops the send, and nothing breaks — but the warning carried no context at all, so
    /// 1,841 identical lines (matchmaker transport, 2026-08-07) and then <b>20,078</b> of them
    /// (Steam transport, 2026-08-17) said only that it had happened. The entry's recorded next step
    /// was <c>[CallerMemberName]</c> on the guard, which would have named <c>SendToAllClients</c> —
    /// something already known, since the guard has four callers and they are all send methods.
    /// <b>The message type is the useful identifier</b>: it names the gameplay path directly, so
    /// "SpawnedEnemy x1200" points at the patch that is missing its ownership check.</para>
    ///
    /// <para><b>Aggregating is not silencing.</b> The entry says a guard firing 1,841 times is
    /// information and the fix is upstream of it. Both still hold: nothing is suppressed, the count
    /// is reported in full, and it now says <i>what</i> was rejected. What is removed is 20,078
    /// individual log writes on a path that runs several times a second — which
    /// <c>docs/netplay/04-performance-and-gc.md</c> and the BepInEx guidance both treat as a real
    /// per-frame cost in its own right.</para>
    ///
    /// <para>Reports itself on a throttle rather than sitting behind a config flag, in the same
    /// shape as the kill-attribution diagnostic. A healthy session prints only its end-of-session
    /// total, <c>0</c>, because a healthy session never rejects a send.</para>
    /// </summary>
    internal static class HostGuardDiagnostics
    {
        private const double REPORT_INTERVAL_SECONDS = 10d;

        private static readonly ConcurrentDictionary<string, int> rejections = new();
        private static long lastReportTicks;
        private static long sessionTotal;

        /// <summary>
        /// Records one rejected host-only send. <paramref name="messageTypeName"/> may be null when
        /// the caller only had raw bytes; the entry is still counted, under the send method alone.
        /// </summary>
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

            // Only the thread that wins this swap reports, so concurrent rejections cannot
            // double-log. Same guard the kill-attribution diagnostic uses.
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

        /// <summary>
        /// Flushes what the throttle is still holding, prints the session's total, then clears
        /// counters and the throttle so each session starts from zero.
        ///
        /// <para><b>Why the flush.</b> <see cref="Report"/> prints only when a rejection arrives
        /// after the interval, and the first one only primes it. A run with a handful of rejections,
        /// or the last batch of any run, was therefore counted and never printed — so a log with no
        /// <c>[host-guard]</c> line could not prove that nothing was rejected. The total line is
        /// printed even when it is zero, for the same reason: absence of a line no longer means
        /// anything, so a clean session says so.</para>
        /// </summary>
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

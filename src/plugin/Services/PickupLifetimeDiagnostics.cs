using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// OB-5. Separates the two explanations for a pickup instruction naming an id this peer cannot
    /// resolve, which is the one thing the existing warnings cannot do.
    ///
    /// <para><b>The question.</b> A 2026-08-07 capture logged <b>184</b>
    /// <c>Pickup N not found in PickupManagerService</c> warnings in one session, and the entry's
    /// two candidates want opposite fixes:</para>
    ///
    /// <list type="bullet">
    /// <item><b>never-seen</b> — the spawn was never registered on this peer. A replication gap;
    /// the fix is upstream, in how the pickup reaches the client at all.</item>
    /// <item><b>seen-then-removed</b> — the pickup was consumed before the instruction for it
    /// arrived. A lifetime/ordering race; the correct fix is to ignore the instruction quietly,
    /// because nothing is actually wrong.</item>
    /// </list>
    ///
    /// <para><b>Why the log could not already tell them apart.</b> The success path of
    /// <c>OnReceivedSpawnedPickup</c> logs nothing, so an absent id looks identical either way.
    /// This keeps a <c>HashSet</c> of every id ever registered this session — the entry's own
    /// recommended next step — and the warning branch then asks it which case this is.</para>
    ///
    /// <para><b>Reports on a throttle, and only when something failed.</b> Same shape as the
    /// kill-attribution diagnostic: a healthy session says nothing. That matters because the
    /// player-visible symptom is now reported as fixed — the pull works — so this is expected to
    /// run mostly silent and only characterise the residual drops.</para>
    ///
    /// <para><b>Deliberately not behind a config flag.</b> The counters are two integer increments
    /// and a set lookup on a path that already builds a warning string; the reporting is one line
    /// per 10 s at most. Gating it would mean the next capture arrives without it, which is how
    /// this bug stayed open for five sessions.</para>
    /// </summary>
    internal static class PickupLifetimeDiagnostics
    {
        private const double REPORT_INTERVAL_SECONDS = 10d;

        /// <summary>
        /// Every pickup id this peer has registered this session, including ones since removed.
        /// Bounded by pickups per run, and an id is 4 bytes — a long run is tens of thousands of
        /// entries, which is far cheaper than the question it answers.
        /// </summary>
        private static readonly ConcurrentDictionary<uint, byte> everRegistered = new();

        private static readonly ConcurrentDictionary<string, int> neverSeen = new();
        private static readonly ConcurrentDictionary<string, int> seenThenRemoved = new();
        private static long lastReportTicks;

        /// <summary>Called wherever a pickup id becomes known to this peer, host or client.</summary>
        internal static void RecordRegistered(uint pickupId)
        {
            if (pickupId != 0)
            {
                everRegistered[pickupId] = 0;
            }
        }

        /// <summary>
        /// Called from a "pickup not found" branch. <paramref name="site"/> names the handler so the
        /// report can say whether one path dominates — the 2026-08-07 capture had 184 failures in
        /// <c>OnReceivedPickupFollowingPlayer</c> against 14 in <c>OnReceivedPickupApplied</c>, and
        /// that ratio is itself a clue.
        /// </summary>
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

        /// <summary>
        /// Clears everything so a session starts from zero. The id set MUST be cleared here: ids are
        /// allocated from a per-session counter, so a stale set would report a fresh session's
        /// never-registered pickups as registered-then-removed — the exact answer this exists to
        /// give correctly.
        /// </summary>
        internal static void Reset()
        {
            everRegistered.Clear();
            neverSeen.Clear();
            seenThenRemoved.Clear();
            Interlocked.Exchange(ref lastReportTicks, 0);
        }
    }
}

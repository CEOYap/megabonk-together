using OmniBonk.Configuration;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace OmniBonk.Services
{
    /// <summary>
    /// Per-message-type byte counters and a latency/loss readout. Counts payload times recipients
    /// (payload only, no transport overhead). Near-free when off: <see cref="Record"/> checks a static bool first.
    /// <para><b>UNVERIFIED:</b> that the recorder's own cost is negligible at six players with a
    /// full swarm.</para>
    /// </summary>
    internal static class BandwidthDiagnostics
    {
        private const float REPORT_INTERVAL_SECONDS = Tunables.DIAGNOSTIC_REPORT_INTERVAL_SECONDS;

        private const int TOP_N = 8;

        private sealed class Counter
        {
            public long Bytes;
            public long Sends;
        }

        // Written from the main thread and the LiteNetLib receive path.
        private static readonly ConcurrentDictionary<string, Counter> counters = new();

        private static volatile bool enabled;

        // Host EnemyDamaged aggregation: hits -> summed entries -> EnemyDamagedBatch messages.
        private static long aggregatedHits;
        private static long aggregatedEntries;
        private static long aggregatedMessages;
        private static float lastReportTime = -999f;
        private static bool primed;

        /// <summary><paramref name="recipients"/> may be 0 (still counted as a send).</summary>
        internal static void Record(string messageType, int payloadBytes, int recipients)
        {
            if (!enabled)
            {
                return;
            }

            var counter = counters.GetOrAdd(messageType, static _ => new Counter());

            Interlocked.Add(ref counter.Bytes, (long)payloadBytes * recipients);
            Interlocked.Increment(ref counter.Sends);
        }

        /// <summary>One call per <c>EnemyDamagedBatch</c> sent.</summary>
        internal static void RecordEnemyDamageBatch(int hits, int entries)
        {
            if (!enabled)
            {
                return;
            }

            Interlocked.Add(ref aggregatedHits, hits);
            Interlocked.Add(ref aggregatedEntries, entries);
            Interlocked.Increment(ref aggregatedMessages);
        }

        /// <summary>Once per frame. Takes <see cref="INetTransport"/>, never the LiteNetLib type, so Steam sessions work.</summary>
        internal static void Sample(bool configEnabled, INetTransport udpClientService, IPlayerManagerService playerManagerService)
        {
            if (!configEnabled)
            {
                if (enabled)
                {
                    // Turned off mid-session: drop the partial window.
                    enabled = false;
                    counters.Clear();
                    ClearAggregation();
                    primed = false;
                }

                return;
            }

            enabled = true;

            var now = Time.unscaledTime;

            if (!primed)
            {
                primed = true;
                lastReportTime = now;
                counters.Clear();
                ClearAggregation();
                return;
            }

            var elapsed = now - lastReportTime;
            if (elapsed < REPORT_INTERVAL_SECONDS)
            {
                return;
            }

            lastReportTime = now;
            Report(elapsed, udpClientService, playerManagerService);
        }

        private static void Report(float elapsed, INetTransport udpClientService, IPlayerManagerService playerManagerService)
        {
            // Not atomic as a whole; a mid-report send may land in either window. Acceptable.
            var snapshot = counters
                .Select(kv => (Type: kv.Key, Bytes: Interlocked.Exchange(ref kv.Value.Bytes, 0), Sends: Interlocked.Exchange(ref kv.Value.Sends, 0)))
                .Where(x => x.Sends > 0)
                .OrderByDescending(x => x.Bytes)
                .ToList();

            if (snapshot.Count == 0)
            {
                return;
            }

            var totalBytes = snapshot.Sum(x => x.Bytes);
            var players = playerManagerService.GetAllPlayers().Count();

            Plugin.Log.LogInfo(
                $"[bw] {totalBytes / 1024f / elapsed:F1} KB/s payload out over {elapsed:F1}s at {players} player(s). " +
                "Payload only - excludes LiteNetLib, relay-envelope and UDP/IP overhead.");

            // Padded by hand: alignment specifiers do not compile with BepInEx's log handler.
            foreach (var entry in snapshot.Take(TOP_N))
            {
                var name = entry.Type.PadRight(32);
                var rate = (entry.Bytes / 1024f / elapsed).ToString("F2").PadLeft(8);
                var sends = (entry.Sends / elapsed).ToString("F1").PadLeft(7);
                var perSend = ((double)entry.Bytes / entry.Sends).ToString("F0").PadLeft(6);

                Plugin.Log.LogInfo($"[bw]   {name} {rate} KB/s  {sends}/s  {perSend} B/send");
            }

            if (snapshot.Count > TOP_N)
            {
                var rest = snapshot.Skip(TOP_N).ToList();
                var name = $"({rest.Count} more types)".PadRight(32);
                var rate = (rest.Sum(x => x.Bytes) / 1024f / elapsed).ToString("F2").PadLeft(8);

                Plugin.Log.LogInfo($"[bw]   {name} {rate} KB/s");
            }

            ReportAggregation(elapsed);

            ReportLinkQuality(udpClientService, playerManagerService);
        }

        /// <summary>OB-34: hits per entry is the summing, entries per message the batching.</summary>
        private static void ReportAggregation(float elapsed)
        {
            var hits = Interlocked.Exchange(ref aggregatedHits, 0);
            var entries = Interlocked.Exchange(ref aggregatedEntries, 0);
            var messages = Interlocked.Exchange(ref aggregatedMessages, 0);
            if (messages == 0)
            {
                return;
            }

            var hitsPerSecond = (hits / elapsed).ToString("F1");
            var entriesPerSecond = (entries / elapsed).ToString("F1");
            var messagesPerSecond = (messages / elapsed).ToString("F1");
            var hitsPerEntry = entries == 0 ? "0" : ((double)hits / entries).ToString("F1");
            var entriesPerMessage = ((double)entries / messages).ToString("F1");

            Plugin.Log.LogInfo(
                $"[bw]   EnemyDamaged aggregation: {hitsPerSecond} hits/s -> {entriesPerSecond} entries/s -> "
                + $"{messagesPerSecond} batches/s ({hitsPerEntry} hits per entry, {entriesPerMessage} entries per batch)");
        }

        private static void ClearAggregation()
        {
            Interlocked.Exchange(ref aggregatedHits, 0);
            Interlocked.Exchange(ref aggregatedEntries, 0);
            Interlocked.Exchange(ref aggregatedMessages, 0);
        }

        private static void ReportLinkQuality(INetTransport udpClientService, IPlayerManagerService playerManagerService)
        {
            foreach (var player in playerManagerService.GetAllPlayers())
            {
                if (playerManagerService.IsLocalConnectionId(player.ConnectionId))
                {
                    continue;
                }

                try
                {
                    Plugin.Log.LogInfo($"[bw]   peer {player.ConnectionId} {udpClientService.DescribeLink(player.ConnectionId)}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[bw] Could not read link quality for {player.ConnectionId}: {ex.Message}");
                }
            }
        }

        internal static void Reset()
        {
            primed = false;
            lastReportTime = -999f;
            counters.Clear();
            ClearAggregation();
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Per-message-type byte counters and a latency/loss readout — the Phase 0 exit criteria in
    /// <c>docs/steamworks/00-migration-plan.md</c>, which asks for a baseline recorded at 2/4/6
    /// players "so the migration can be measured rather than asserted".
    ///
    /// <para><b>Why per message type and not just a total.</b> A total tells you the mod is
    /// expensive; it does not tell you which stream to attack, and after the migration it cannot
    /// tell you whether a regression came from the transport or from a message that changed. The
    /// per-type split is also what makes Steam's connection lanes worth configuring — lanes need to
    /// know which classes of message are actually competing.</para>
    ///
    /// <para><b>Counted at the wire, not at the call.</b> <see cref="Record"/> takes the serialized
    /// payload size and the number of recipients it was actually sent to, because a 40 Hz host
    /// broadcast costs its payload once per peer. Counting the call rather than the fan-out would
    /// under-report the host by a factor of five at six players — which is exactly the population
    /// this baseline exists to size. The figure is still payload only: it excludes LiteNetLib
    /// headers, the relay envelope's own framing, and UDP/IP overhead, so treat it as a floor and a
    /// comparison basis, not an absolute bandwidth number.</para>
    ///
    /// <para><b>Cost when off.</b> <see cref="Record"/> returns on a static bool read before it
    /// touches anything, and the flag is refreshed once per report window rather than read from
    /// config per send. Off is the default, and off should be indistinguishable from absent — this
    /// sits on the send path, which <c>bepinex</c>'s "never log in a hot path" rule governs.</para>
    ///
    /// <para><b>UNVERIFIED:</b> that the recorder's own cost is negligible at six players with a
    /// full swarm. It is a dictionary lookup plus two interlocked adds per send, which should be far
    /// below the serialization it follows, but that has not been measured in game — which is the
    /// same trap this file exists to avoid, so it is worth watching the first time it is switched
    /// on.</para>
    /// </summary>
    internal static class BandwidthDiagnostics
    {
        private const float REPORT_INTERVAL_SECONDS = 10f;

        /// <summary>How many message types to name in a report, worst-first.</summary>
        private const int TOP_N = 8;

        private sealed class Counter
        {
            public long Bytes;
            public long Sends;
        }

        // Sends happen from the Unity main thread (NetworkHandler.Update) and from the LiteNetLib
        // receive path, so this is written concurrently.
        private static readonly ConcurrentDictionary<string, Counter> counters = new();

        private static volatile bool enabled;
        private static float lastReportTime = -999f;
        private static bool primed;

        /// <summary>
        /// One send, already serialized. <paramref name="recipients"/> is the number of peers the
        /// payload actually went to — 0 is legitimate (a host with no peers yet) and is recorded as
        /// a send of zero bytes so the call still shows up.
        /// </summary>
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

        /// <summary>Called once per frame from the network tick, same shape as the allocation sampler.</summary>
        internal static void Sample(bool configEnabled, IUdpClientService udpClientService, IPlayerManagerService playerManagerService)
        {
            if (!configEnabled)
            {
                if (enabled)
                {
                    // Turned off mid-session: stop recording and drop what was collected, so a later
                    // re-enable does not report a window that spans the gap.
                    enabled = false;
                    counters.Clear();
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

        private static void Report(float elapsed, IUdpClientService udpClientService, IPlayerManagerService playerManagerService)
        {
            // Snapshot and reset per window. Reads and the zeroing are not atomic together, so a send
            // landing mid-report can be counted in either window — acceptable for a rate estimate,
            // and cheaper than locking the send path.
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
                "Payload only — excludes LiteNetLib, relay-envelope and UDP/IP overhead.");

            // Alignment specifiers ({x,8:F2}) do not compile against BepInEx's interpolated-string
            // log handler, so the columns are padded by hand.
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

            ReportLinkQuality(udpClientService, playerManagerService);
        }

        /// <summary>
        /// Latency and loss per peer. LiteNetLib already runs with <c>EnableStatistics = true</c>
        /// (UdpClientService.cs:129), so this costs nothing extra to read.
        /// </summary>
        private static void ReportLinkQuality(IUdpClientService udpClientService, IPlayerManagerService playerManagerService)
        {
            foreach (var player in playerManagerService.GetAllPlayers())
            {
                if (playerManagerService.IsLocalConnectionId(player.ConnectionId))
                {
                    continue;
                }

                try
                {
                    Plugin.Log.LogInfo($"[bw]   peer {player.ConnectionId} rtt {udpClientService.GetLatency(player.ConnectionId)} ms");
                }
                catch (Exception ex)
                {
                    // A peer that left between the snapshot and here is not worth failing a report
                    // over, and this runs inside the network tick.
                    Plugin.Log.LogWarning($"[bw] Could not read link quality for {player.ConnectionId}: {ex.Message}");
                }
            }
        }

        /// <summary>Re-primes so a new session's first window is not measured against the old one.</summary>
        internal static void Reset()
        {
            primed = false;
            lastReportTime = -999f;
            counters.Clear();
        }
    }
}

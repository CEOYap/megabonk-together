using Assets.Scripts.Utility;
using MegabonkTogether.Configuration;
using System;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Frame-time sampler for performance testing: one <c>[perf]</c> line every
    /// <see cref="REPORT_INTERVAL_SECONDS"/> while a session runs.
    ///
    /// <para><b>Why not average FPS alone.</b> The complaints this project has had (v4.1.0, v4.2.2,
    /// v5.x) are stutter, and an average hides it: 59 frames at 10 ms and one at 400 ms is "~70 FPS".
    /// So the line carries the 1% and 0.1% lows, the worst frame, and counts of frames over 33 ms
    /// (below 30 FPS) and over 100 ms (a visible hitch).</para>
    ///
    /// <para><b>Why the context.</b> A number is only useful against another one. The line carries
    /// what drives the mod's per-frame cost (tracked enemies, players, host or client) and how much
    /// of the window the game was paused, so two runs can be compared and a drop can be matched to a
    /// swarm. Read it together with <c>[alloc]</c> (GC) and <c>[bw]</c> (network), which use the
    /// same 10-second window: a hitch count that rises with gen-2 collections points at the GC.</para>
    ///
    /// <para><b>Cost.</b> Per frame: one float into a preallocated ring, two comparisons. At report
    /// time one in-place sort of a preallocated copy and one log line. No per-frame allocation.</para>
    ///
    /// <para><b>Limits.</b> Measures whole frames, game and mod together; it cannot say which side a
    /// slow frame came from. Compare a session with the same build, map and player count, and use
    /// singleplayer as the baseline by eye, since this only runs during a netplay session.</para>
    /// </summary>
    internal static class FrameTimeDiagnostics
    {
        private const float REPORT_INTERVAL_SECONDS = Tunables.DIAGNOSTIC_REPORT_INTERVAL_SECONDS;

        /// <summary>Room for a 10 s window at 400 FPS. Faster frames overwrite the oldest.</summary>
        private const int MAX_SAMPLES = 4096;

        private const float SLOW_FRAME_SECONDS = 1f / 30f;
        private const float HITCH_FRAME_SECONDS = 0.1f;

        private static readonly float[] samples = new float[MAX_SAMPLES];
        private static readonly float[] sorted = new float[MAX_SAMPLES];
        private static int count;
        private static int next;
        private static int pausedFrames;
        private static int lastGen2;
        private static float lastReportTime = -999f;
        private static bool primed;

        /// <summary>
        /// Called once per frame from the network tick. Returns true when a report is due; the
        /// caller then gathers the context and calls <see cref="Report"/>, so the context (which
        /// counts players through LINQ) is built once per window, not once per frame.
        /// </summary>
        internal static bool Sample(bool enabled)
        {
            if (!enabled)
            {
                return false;
            }

            var now = Time.unscaledTime;
            if (!primed)
            {
                Prime(now);
                return false;
            }

            samples[next] = Time.unscaledDeltaTime;
            next = (next + 1) % MAX_SAMPLES;
            if (count < MAX_SAMPLES)
            {
                count++;
            }

            if (MyTime.paused)
            {
                pausedFrames++;
            }

            return now - lastReportTime >= REPORT_INTERVAL_SECONDS && count > 0;
        }

        /// <summary>Logs the window <see cref="Sample"/> said was due, then starts the next one.</summary>
        internal static void Report(int trackedEnemies, int players, bool isHost)
        {
            var now = Time.unscaledTime;
            var elapsed = now - lastReportTime;
            if (!primed || count == 0)
            {
                return;
            }

            var total = 0f;
            var slow = 0;
            var hitches = 0;
            for (var i = 0; i < count; i++)
            {
                var dt = samples[i];
                total += dt;
                if (dt > SLOW_FRAME_SECONDS)
                {
                    slow++;
                }

                if (dt > HITCH_FRAME_SECONDS)
                {
                    hitches++;
                }
            }

            Array.Copy(samples, sorted, count);
            Array.Sort(sorted, 0, count);

            var averageFps = count / Math.Max(total, 1e-6f);
            var low1 = 1f / Math.Max(Percentile(0.99f), 1e-6f);
            var low01 = 1f / Math.Max(Percentile(0.999f), 1e-6f);
            var worstMs = sorted[count - 1] * 1000f;
            var gen2 = GC.CollectionCount(2);

            Plugin.Log.LogInfo(
                $"[perf] {averageFps:F0} FPS avg, 1% low {low1:F0}, 0.1% low {low01:F0}, worst {worstMs:F0} ms " +
                $"over {count} frames / {elapsed:F1}s; {slow} frame(s) >33 ms, {hitches} >100 ms; " +
                $"gen2 GC +{gen2 - lastGen2}; {trackedEnemies} enemies, {players} player(s), " +
                $"{(isHost ? "host" : "client")}, paused {100f * pausedFrames / count:F0}%.");

            Prime(now);
        }

        /// <summary>Frame time at a fraction of the sorted window; 0.99 is the 1% slowest edge.</summary>
        private static float Percentile(float fraction)
        {
            var index = (int)Math.Ceiling(fraction * count) - 1;
            return sorted[Math.Clamp(index, 0, count - 1)];
        }

        private static void Prime(float now)
        {
            primed = true;
            lastReportTime = now;
            count = 0;
            next = 0;
            pausedFrames = 0;
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

using Assets.Scripts.Utility;
using OmniBonk.Configuration;
using System;
using UnityEngine;

namespace OmniBonk.Services
{
    /// <summary>
    /// One <c>[perf]</c> line per window during a session: lows, worst frame and hitch counts,
    /// since averages hide stutter. No per-frame allocation. Measures game and mod together.
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
        /// Per frame. Returns true when <see cref="Report"/> is due, so its LINQ-built context is
        /// gathered once per window.
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

        internal static void Reset()
        {
            primed = false;
            lastReportTime = -999f;
        }
    }
}

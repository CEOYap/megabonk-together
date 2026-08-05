using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Captures a stack for the client-side exception storm.
    ///
    /// <para><b>The problem.</b> A two-player internet session logged <b>3,032</b>
    /// <c>NullReferenceException</c>s on the client and <b>zero</b> on the host, every one of them
    /// the bare Unity form with no stack:</para>
    ///
    /// <code>[Error  :     Unity] NullReferenceException: Object reference not set to an instance of an object.</code>
    ///
    /// <para>They have been present in every client log for several sessions, were briefly and
    /// wrongly attributed to a third-party mod, and have never been diagnosed. Without a stack
    /// there is nothing to act on — which is what this exists to fix.</para>
    ///
    /// <para><b>Why there is no stack today.</b> Unity's default
    /// <c>stackTraceLogType</c> for <c>LogType.Exception</c> can be <c>None</c> in a shipped
    /// player, and under IL2CPP a native frame is not a managed frame. Two things are done about
    /// it: <c>SetStackTraceLogType</c> asks Unity for script frames, and if Unity still hands back
    /// nothing, a managed <c>StackTrace</c> is captured inside the callback. The callback fires
    /// synchronously from the log call, so a managed thrower is usually still on the stack —
    /// whereas a purely native one gives an empty capture, and <b>that is itself the finding</b>:
    /// it would say the storm is game code, not ours.</para>
    ///
    /// <para><b>Why it samples.</b> 3,032 per session. Logging each one with a stack would be both
    /// a per-frame cost and unreadable. Distinct stacks are what matter, so it logs the first few
    /// unique signatures in full and counts the rest — the same shape as the other diagnostics
    /// here: cheap when healthy, loud once, quiet after.</para>
    ///
    /// <para><b>Hooked at plugin load, not at session start</b>, deliberately: whether these also
    /// occur in singleplayer is a discriminator worth having, and the host recording zero during a
    /// full session already suggests they are specific to being a client.</para>
    ///
    /// <para><b>Delete once attributed.</b></para>
    /// </summary>
    internal static class UnityExceptionDiagnostics
    {
        private const int MAX_DISTINCT_STACKS = 6;
        private const float REPORT_INTERVAL_SECONDS = 15f;

        /// <summary>
        /// Rooted deliberately. An Il2Cpp delegate that only the native event holds can be
        /// collected, and the callback then jumps into freed memory — a crash with no managed
        /// stack, which is exactly the class of bug this is meant to be diagnosing.
        /// </summary>
        private static Application.LogCallback callback;

        private static bool hooked;
        private static bool reentrant;
        private static int total;
        private static int sinceLastReport;
        private static float lastReportTime;
        private static readonly HashSet<string> seenSignatures = new HashSet<string>();

        internal static void Hook()
        {
            if (hooked)
            {
                return;
            }

            try
            {
                // Ask Unity for script frames on exceptions and errors. A shipped player often
                // defaults these to None, which is the likeliest reason the log carries a bare
                // message today.
                Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
                Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);

                callback = new Application.LogCallback(OnUnityLog);
                Application.logMessageReceived += callback;

                hooked = true;
                Plugin.Log.LogInfo("[unity-exc] Listening for Unity exceptions with script stack traces enabled.");
            }
            catch (System.Exception ex)
            {
                // Never fatal: this is instrumentation. If the hook is unavailable the mod runs
                // exactly as before, minus the diagnosis.
                Plugin.Log.LogWarning($"[unity-exc] Could not hook Unity log callback: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Clears counters so each session starts from zero. Keeps the hook installed.</summary>
        internal static void Reset()
        {
            total = 0;
            sinceLastReport = 0;
            lastReportTime = 0f;
            seenSignatures.Clear();
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (reentrant)
            {
                return;
            }

            if (type != LogType.Exception && type != LogType.Error)
            {
                return;
            }

            if (string.IsNullOrEmpty(condition) || !condition.Contains("Exception"))
            {
                return;
            }

            reentrant = true;
            try
            {
                total++;
                sinceLastReport++;

                var trace = string.IsNullOrWhiteSpace(stackTrace) ? CaptureManagedStack() : stackTrace.Trim();
                var signature = Signature(condition, trace);

                if (seenSignatures.Count < MAX_DISTINCT_STACKS && seenSignatures.Add(signature))
                {
                    Plugin.Log.LogWarning(
                        $"[unity-exc] distinct #{seenSignatures.Count}: {condition}\n{trace}");
                }

                MaybeReport();
            }
            catch
            {
                // A diagnostic that throws inside a log handler would recurse. Swallow silently.
            }
            finally
            {
                reentrant = false;
            }
        }

        /// <summary>
        /// Unity gave us nothing, so capture our own. If this comes back with no MegabonkTogether
        /// frames — or empty — the thrower is native game code and the storm is not ours, which
        /// narrows the search more than any single stack would.
        /// </summary>
        private static string CaptureManagedStack()
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(2, true);
                var frames = trace.GetFrames();
                if (frames == null || frames.Length == 0)
                {
                    return "<no managed frames — thrower is native game code>";
                }

                var sb = new System.Text.StringBuilder();
                var printed = 0;
                foreach (var frame in frames)
                {
                    if (printed >= 12)
                    {
                        break;
                    }

                    var method = frame.GetMethod();
                    if (method == null)
                    {
                        continue;
                    }

                    var declaring = method.DeclaringType;
                    sb.Append("    at ")
                      .Append(declaring == null ? "?" : declaring.FullName)
                      .Append('.')
                      .Append(method.Name);

                    var line = frame.GetFileLineNumber();
                    if (line > 0)
                    {
                        sb.Append(':').Append(line);
                    }

                    sb.Append('\n');
                    printed++;
                }

                return sb.Length == 0 ? "<no managed frames — thrower is native game code>" : sb.ToString().TrimEnd();
            }
            catch (System.Exception ex)
            {
                return $"<stack capture failed: {ex.GetType().Name}>";
            }
        }

        /// <summary>
        /// Collapses a stack to something stable enough to deduplicate on. The whole trace would
        /// make every occurrence look distinct once line numbers or object names vary.
        /// </summary>
        private static string Signature(string condition, string trace)
        {
            var firstLines = trace.Split('\n');
            var head = firstLines.Length >= 3
                ? string.Join("|", firstLines[0].Trim(), firstLines[1].Trim(), firstLines[2].Trim())
                : trace;

            return condition + "||" + head;
        }

        private static void MaybeReport()
        {
            var now = Time.unscaledTime;
            if (now - lastReportTime < REPORT_INTERVAL_SECONDS)
            {
                return;
            }
            lastReportTime = now;

            if (sinceLastReport == 0)
            {
                return;
            }

            Plugin.Log.LogWarning(
                $"[unity-exc] {sinceLastReport} Unity exception(s) in the last ~{REPORT_INTERVAL_SECONDS:F0}s " +
                $"({total} this session, {seenSignatures.Count} distinct stack(s) captured, " +
                $"cap {MAX_DISTINCT_STACKS}).");

            sinceLastReport = 0;
        }
    }
}

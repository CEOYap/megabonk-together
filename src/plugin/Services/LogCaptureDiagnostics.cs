using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Reports, at startup, whether this install will write Unity-sourced log lines to
    /// <c>LogOutput.log</c>, and names the key to change if it will not.
    ///
    /// <para><b>The problem this closes.</b> A host logged <b>zero</b> Unity-sourced lines across
    /// three sessions while its client logged thousands. Because
    /// <c>[Logging] UnityLogListening = true</c> on both, the difference was attributed to the two
    /// peers running different BepInEx builds (be.755 and be.785) and left unexplained. Every
    /// "the host does not have this" claim in <c>docs/netplay/</c> rests on the host log being
    /// comparable to the client's, so an unexplained hole in it makes those claims unprovable.</para>
    ///
    /// <para><b>The cause, and why the obvious key is the wrong one.</b> BepInEx 6 has
    /// <i>two independent</i> gates, and they have opposite defaults:</para>
    ///
    /// <list type="bullet">
    /// <item><c>[Logging] UnityLogListening</c> — "Enables showing unity log messages in the BepInEx
    /// logging system." <b>Default true.</b> This is the one that was checked. It admits Unity's
    /// messages into BepInEx as a log source named <c>Unity</c>.</item>
    /// <item><c>[Logging.Disk] WriteUnityLog</c> — "Include unity log messages in log file output."
    /// <b>Default false.</b> This is the one that decides whether those messages reach
    /// <c>LogOutput.log</c> at all.</item>
    /// </list>
    ///
    /// <para>So a stock install shows Unity lines in the console and none on disk. The host was at
    /// the defaults; the client had <c>WriteUnityLog</c> turned on at some earlier point — which is
    /// also why the client is the peer where the NullReferenceException storm was ever visible. The
    /// BepInEx build difference is a coincidence, not the cause: both keys exist with these
    /// defaults in be.785, and both installs checked here agree.</para>
    ///
    /// <para><b>This reads the config; it does not write it.</b> <c>BepInEx.cfg</c> belongs to the
    /// loader and to whatever else the user runs, and silently rewriting another component's
    /// settings is not something a mod should do on the user's behalf. The warning names the file,
    /// the key and the value so the change is one edit.</para>
    ///
    /// <para><b>Why parse the file rather than ask BepInEx.</b> <c>WriteUnityLog</c> is bound
    /// internally by <c>DiskLogListener</c> and is not reachable through any public API, so there
    /// is nothing to query. Parsing is version-tolerant in the way that matters: if a future build
    /// renames or drops the key, the parse simply reports "not found" instead of asserting
    /// something false.</para>
    /// </summary>
    internal static class LogCaptureDiagnostics
    {
        private const string SECTION = "Logging.Disk";
        private const string KEY = "WriteUnityLog";

        private static bool reported;

        /// <summary>
        /// Logs one line describing this install's Unity-log capture, plus a warning naming the fix
        /// when capture is off. Safe to call more than once; only the first call reports.
        /// </summary>
        internal static void Report()
        {
            if (reported)
            {
                return;
            }
            reported = true;

            var configPath = BepInEx.Paths.BepInExConfigPath;
            // Read off the assembly rather than BepInEx.Paths.DisplayBepInExVersion.
            //
            // That property does not exist on be.755, and the first version of this used it: the
            // client peer — running the older BepInEx, and the peer whose logs matter most — threw
            // MissingMethodException and reported nothing at all, in the very session this
            // diagnostic was written to make comparable. It failed safe only because the call site
            // in Plugin.Load is wrapped; the diagnostic itself was simply absent.
            //
            // Note a try/catch here would NOT have helped: MissingMethodException is raised when
            // this method is JIT-compiled, not when the missing call executes, so a catch inside
            // Report() never runs. The fix has to be to not reference the API at all. AssemblyName
            // has been on every BepInEx build this project has ever seen.
            //
            // This is also why the full BepInEx banner is not reproduced here — every log already
            // opens with it. What this line is for is pairing the build with the WriteUnityLog
            // answer on one line, so two logs can be shown comparable without cross-referencing.
            var bepinexVersion = typeof(BepInEx.Paths).Assembly.GetName().Version?.ToString() ?? "unknown";

            // The BepInEx build is logged alongside the answer deliberately: two peers' logs are
            // only comparable if both were captured under the same rules, and the build was the
            // thing wrongly blamed last time. Recording both makes the next comparison decidable
            // from the logs alone rather than from someone remembering their setup.
            var state = ReadWriteUnityLog(configPath);

            switch (state)
            {
                case true:
                    Plugin.Log.LogInfo(
                        $"[log-capture] BepInEx {bepinexVersion}; [{SECTION}] {KEY} = true. " +
                        "Unity-sourced lines WILL appear in LogOutput.log.");
                    break;

                case false:
                    Plugin.Log.LogWarning(
                        $"[log-capture] BepInEx {bepinexVersion}; [{SECTION}] {KEY} = false. " +
                        "Unity-sourced lines (NullReferenceException, Look rotation, etc.) will NOT " +
                        "reach LogOutput.log on this install, even though [Logging] UnityLogListening " +
                        $"is true — they are separate gates. To capture them, set {KEY} = true under " +
                        $"[{SECTION}] in {configPath} and relaunch. Both peers must match before any " +
                        "two logs are compared.");
                    break;

                default:
                    Plugin.Log.LogWarning(
                        $"[log-capture] BepInEx {bepinexVersion}; could not determine [{SECTION}] {KEY} " +
                        $"from {configPath}. Unity-log capture state is unknown, so treat the absence " +
                        "of a Unity-sourced line in this log as inconclusive rather than as evidence.");
                    break;
            }
        }

        /// <summary>
        /// Returns the value of <c>[Logging.Disk] WriteUnityLog</c>, or null when the file, the
        /// section or the key cannot be found. Section-aware because BepInEx.cfg has several
        /// <c>[Logging.*]</c> sections and the key name is not unique across a naive scan.
        /// </summary>
        private static bool? ReadWriteUnityLog(string configPath)
        {
            try
            {
                if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
                {
                    return null;
                }

                var inSection = false;

                foreach (var raw in File.ReadAllLines(configPath))
                {
                    var line = raw.Trim();

                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    if (line[0] == '[' && line.EndsWith("]"))
                    {
                        inSection = string.Equals(
                            line.Substring(1, line.Length - 2).Trim(),
                            SECTION,
                            StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inSection)
                    {
                        continue;
                    }

                    var match = Regex.Match(line, @"^([^=]+)=(.*)$");
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (!string.Equals(match.Groups[1].Value.Trim(), KEY, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = match.Groups[2].Value.Trim();
                    return bool.TryParse(value, out var parsed) ? parsed : (bool?)null;
                }

                return null;
            }
            catch (Exception ex)
            {
                // A diagnostic must never be able to stop the plugin loading — see the call-site
                // comment in Plugin.Load about the exception that did exactly that.
                Plugin.Log.LogWarning($"[log-capture] Could not read {configPath}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}

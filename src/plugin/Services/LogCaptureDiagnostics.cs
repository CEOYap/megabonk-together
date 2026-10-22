using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace OmniBonk.Services
{
    /// <summary>
    /// Reports at startup whether Unity log lines reach <c>LogOutput.log</c>. That is
    /// <c>[Logging.Disk] WriteUnityLog</c> (default false), not <c>[Logging] UnityLogListening</c>
    /// (default true), so peers' logs are not comparable without it.
    /// <para>Reads BepInEx.cfg, never writes it. Parsed because the key has no public API.</para>
    /// </summary>
    internal static class LogCaptureDiagnostics
    {
        private const string SECTION = "Logging.Disk";
        private const string KEY = "WriteUnityLog";

        private static bool reported;

        /// <summary>Only the first call reports.</summary>
        internal static void Report()
        {
            if (reported)
            {
                return;
            }
            reported = true;

            var configPath = BepInEx.Paths.BepInExConfigPath;
            // Not Paths.DisplayBepInExVersion: missing on be.755, and MissingMethodException fires
            // at JIT time, so no try/catch can save it. Informational version, because
            // AssemblyName.Version is "6.0.0.0" on every build.
            var bepinexAssembly = typeof(BepInEx.Paths).Assembly;
            var bepinexVersion =
                bepinexAssembly
                    .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                    .OfType<AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion
                ?? bepinexAssembly.GetName().Version?.ToString()
                ?? "unknown";

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
                        $"is true - they are separate gates. To capture them, set {KEY} = true under " +
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

        /// <summary>Null when not found. Section-aware: key names repeat across sections.</summary>
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
                // Must never stop the plugin loading.
                Plugin.Log.LogWarning($"[log-capture] Could not read {configPath}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}

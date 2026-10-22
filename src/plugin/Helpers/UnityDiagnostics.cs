using UnityEngine;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// Re-enables Unity script stack traces (shipped builds set None). Traces appear only in
    /// <c>%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log</c>, not LogOutput.log.
    /// Off by default: affects the whole game's logging.
    /// </summary>
    public static class UnityDiagnostics
    {
        private static bool applied;

        /// <summary>Call again after the first frame: the game overwrites the policy during startup.</summary>
        public static void EnableStackTraces()
        {
            if (applied || !Configuration.ModConfig.LogUnityStackTraces.Value)
            {
                return;
            }

            try
            {
                // ScriptOnly: native IL2CPP frames are unresolvable addresses.
                Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
                Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
                Application.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);

                // OB-25: the rocket warning is a plain Log message, so it never had a caller in any
                // log. Separate switch, because this stacks every message the game logs.
                if (Configuration.ModConfig.LogUnityMessageStackTraces.Value)
                {
                    Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.ScriptOnly);
                    Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.ScriptOnly);
                    Plugin.Log.LogInfo("[diag] Unity Log and Warning messages now carry script stacks in Player.log too.");
                }

                applied = true;
                Plugin.Log.LogInfo(
                    "[diag] Unity script stack traces enabled. Errors in this log now name their "
                    + "source; turn LogUnityStackTraces back off when you are done.");
            }
            catch (System.Exception ex)
            {
                applied = true;
                Plugin.Log.LogWarning(
                    $"[diag] Could not enable Unity stack traces: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void AllowReapply() => applied = false;
    }
}

using UnityEngine;

namespace OmniBonk.Services
{
    /// <summary>
    /// Enables stack traces for the client's stackless NullReferenceException storm. Empty frames
    /// would mean the throw is native game code. Called from <c>Plugin.Load</c> so singleplayer is
    /// covered too. Delete once attributed.
    /// </summary>
    internal static class UnityExceptionDiagnostics
    {
        private static bool hooked;

        /// <summary>
        /// No log callback: <c>Application.LogCallback</c> differs between stripped-libs and the
        /// runtime interop assembly (MissingMethodException). Every exception logs a stack, so keep runs short.
        /// </summary>
        internal static void Hook()
        {
            if (hooked)
            {
                return;
            }
            hooked = true;

            Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
            Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);

            Plugin.Log.LogInfo("[unity-exc] Script stack traces enabled for Unity exceptions and errors.");

            // OB-15: stacks only reach Unity's Player.log, not LogOutput.log; print the real path.
            Plugin.Log.LogWarning(
                "[unity-exc] Stack traces go to Unity's player log, NOT to LogOutput.log. " +
                $"To attribute an exception storm, copy this file: {ResolvePlayerLogPath()}");
        }

        /// <summary>OB-15.</summary>
        internal static string ResolvePlayerLogPath()
        {
            try
            {
                // Player.log sits in persistentDataPath.
                var persistent = Application.persistentDataPath;
                if (!string.IsNullOrEmpty(persistent))
                {
                    return System.IO.Path.Combine(persistent, "Player.log");
                }
            }
            catch
            {
                // Falls through to the documented default below.
            }

            return @"%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log";
        }

        /// <summary>OB-15: repeated at session end, when logs get copied.</summary>
        internal static void RemindWhereTracesAre()
        {
            Plugin.Log.LogWarning(
                "[unity-exc] Session ended. If this run had an exception storm, the stack traces " +
                $"are ONLY in: {ResolvePlayerLogPath()}. LogOutput.log does not contain them.");
        }

        /// <summary>Nothing per-session to clear; kept so the call in NetworkHandler stays valid.</summary>
        internal static void Reset()
        {
        }
    }
}

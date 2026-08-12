using UnityEngine;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Turns Unity's script stack traces back on, so an error in the log says where it came from.
    ///
    /// <para><b>Why this is needed at all.</b> A shipped IL2CPP build normally calls
    /// <c>Application.SetStackTraceLogType(..., StackTraceLogType.None)</c>, and Megabonk is no
    /// exception: every <c>NullReferenceException</c> in <c>LogOutput.log</c> arrives as a bare
    /// one-line message with nothing to attribute it to. BepInEx is already forwarding everything
    /// Unity emits — <c>UnityLogListening = true</c>, disk log level <c>All</c> — so the trace is
    /// not being filtered by us, it is never being produced.</para>
    ///
    /// <para><b>This is not where you read the trace.</b> BepInEx's <c>LogOutput.log</c> records
    /// Unity's exception message and discards the <c>stackTrace</c> argument that comes with it, so
    /// turning this on changes nothing you can see there. Unity's own player log keeps the whole
    /// thing: <c>%USERPROFILE%\AppData\LocalLow\Ved\Megabonk\Player.log</c>. That file is the
    /// first place to look for anything logged as <c>[Error : Unity]</c> — see
    /// <c>docs/ui/04-custom-button-null-background.md</c>, which was diagnosed from it after this
    /// toggle alone proved useless.</para>
    ///
    /// <para>Off by default, because this changes logging for the whole game and not just for the
    /// mod.</para>
    /// </summary>
    public static class UnityDiagnostics
    {
        private static bool applied;

        /// <summary>
        /// Idempotent, and worth calling more than once: the game sets its own stack-trace policy
        /// during startup, so a call from <c>Plugin.Load</c> alone can be overwritten before the
        /// first frame. Calling again after a frame has run catches that.
        /// </summary>
        public static void EnableStackTraces()
        {
            if (applied || !Configuration.ModConfig.LogUnityStackTraces.Value)
            {
                return;
            }

            try
            {
                // ScriptOnly rather than Full: the native half of an IL2CPP trace is addresses
                // nobody here can resolve, and it would triple the size of every error.
                Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
                Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
                Application.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);

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

        /// <summary>
        /// Lets the second call actually run, once the game has had a frame to set its own policy.
        /// </summary>
        public static void AllowReapply() => applied = false;
    }
}

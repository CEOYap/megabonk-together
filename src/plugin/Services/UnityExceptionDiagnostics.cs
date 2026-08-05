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
    /// <para><b>Why there is no stack today.</b> Unity's <c>stackTraceLogType</c> for
    /// <c>LogType.Exception</c> can be <c>None</c> in a shipped player, so the message is emitted
    /// without one. Asking for <c>ScriptOnly</c> makes Unity attach managed frames to the message
    /// it already emits, and BepInEx already captures that — so the stack simply appears in
    /// <c>LogOutput.log</c> beside the line that has been bare until now.</para>
    ///
    /// <para><b>If the frames come back empty anyway, that is the answer, not a failure.</b> Under
    /// IL2CPP a native frame is not a managed frame, so an exception raised entirely inside game
    /// code has no script stack to report — which would rule our code out and point at the
    /// world-generation gap instead.</para>
    ///
    /// <para><b>Called from <c>Plugin.Load</c>, not at session start</b>, deliberately: whether
    /// these also occur in singleplayer is a discriminator worth having, and the host recording
    /// zero across a full session already suggests they are specific to being a client.</para>
    ///
    /// <para><b>Delete once attributed.</b></para>
    /// </summary>
    internal static class UnityExceptionDiagnostics
    {
        private static bool hooked;

        /// <summary>
        /// Asks Unity to attach script stack traces to exceptions and errors. BepInEx already
        /// captures Unity's log output, so the stack then appears in LogOutput.log next to the
        /// message that has been bare until now — no callback, no delegate, nothing to subscribe.
        ///
        /// <para><b>Why no log callback.</b> The first version hooked
        /// <c>Application.logMessageReceived</c> so it could sample and deduplicate. It could not
        /// load at all: we compile against <c>stripped-libs</c>, where <c>Application.LogCallback</c>
        /// is an ordinary .NET delegate, but run against Il2CppInterop's generated assembly where it
        /// is not, so the constructor did not exist at runtime and the plugin failed with
        /// <c>MissingMethodException: 'Void LogCallback..ctor(System.Object, IntPtr)'</c>. Any Unity
        /// <i>event</i> subscription in this project has that compile-time/runtime split; prefer a
        /// plain static call.</para>
        ///
        /// <para><b>Cost of losing the sampler:</b> every exception now writes a stack rather than
        /// the first six distinct ones. At the observed rate that is large, so <b>play for under a
        /// minute</b> — that is more than enough to capture the repeating stack.</para>
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
        }

        /// <summary>Nothing per-session to clear; kept so the call in NetworkHandler stays valid.</summary>
        internal static void Reset()
        {
        }
    }
}

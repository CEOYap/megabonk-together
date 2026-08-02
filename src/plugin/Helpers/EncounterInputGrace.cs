using MegabonkTogether.Configuration;
using UnityEngine;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Swallows the first choice a reward window receives, for a short moment after it opens.
    ///
    /// <para><b>Why.</b> On a controller the same physical button is jump and confirm — "A" on
    /// Xbox, "✕" on PlayStation. Rewired binds them as two different actions (`Jump` and
    /// `UISubmit` are separate fields on <c>MyInputManager</c>), but the player only has one
    /// thumb. In shared experience the reward window opens because *somebody else* interacted
    /// with a chest or a shrine, so it can appear while you are mid-jump, and the press you made
    /// to jump lands on the window as a confirm. The choice is irreversible.</para>
    ///
    /// <para><b>Where the guard lives.</b> On the choice-commit methods, not on the input layer.
    /// Every reward window derives from <c>BaseEncounterWindow</c> and commits through
    /// <c>ChooseOffer(int)</c>, so guarding that (plus the chest's take/banish/discard buttons)
    /// covers mouse, keyboard and controller identically, and cannot interfere with navigation,
    /// camera or movement the way an input-layer filter would.</para>
    ///
    /// <para><b>It cannot strand a player.</b> The guard is purely a time window read from
    /// <c>Time.unscaledTime</c> — the game is paused while a window is up, so scaled time does not
    /// advance — and nothing has to happen for it to expire. Set the config entry to 0 to disable.</para>
    ///
    /// <para><b>Fresh press.</b> A time window alone only covers a tap that overlaps the window
    /// opening; it does nothing for a button <i>held</i> for longer than the grace — holding jump
    /// while gliding, for instance. So if the confirm action is already down when the window opens,
    /// the guard also stays up until it is released. <c>MyInputManager.UISubmit</c> is a
    /// <c>string</c> action name (confirmed from the interop metadata — its <c>get_UISubmit</c>
    /// accessor returns <c>string</c>) and <c>GetButton(string)</c> returns the held state, so no
    /// assumption is being made about either.</para>
    ///
    /// <para><b>The hold check can never strand a player.</b> It is capped by
    /// <see cref="HoldCapSeconds"/>, and if reading the action throws even once the check disables
    /// itself for the rest of the process and the guard falls back to the plain time window. The
    /// worst case is that the guard expires early, which is the behaviour we had before.</para>
    /// </summary>
    internal static class EncounterInputGrace
    {
        /// <summary>
        /// Upper bound on the hold extension. Long enough to cover a real held jump, short enough
        /// that a stuck or unreadable input cannot lock the player out of their own reward.
        /// </summary>
        private const float HoldCapSeconds = 1.5f;

        private static float armedAt = -999f;
        private static bool wasHeldOnOpen;
        private static bool holdCheckAvailable = true;
        private static bool loggedBinding;
        private static volatile bool runtimeReady;

        /// <summary>
        /// Set once <c>Harmony.PatchAll()</c> has returned. Until then every member here must stay
        /// out of the IL2CPP runtime.
        ///
        /// <para><b>Why this exists.</b> These patches can be entered <i>while Harmony is still
        /// installing them</i>: resolving a method patcher runs a game class constructor, that
        /// class init goes through <c>il2cpp_runtime_invoke</c>, and BepInEx's chainloader hook on
        /// that function re-entered the <c>ChooseOffer</c> detour it had just installed. The prefix
        /// then read <c>Time.unscaledTime</c> — itself a runtime invoke — and the same hook
        /// re-entered the detour again, which is an unbounded loop. It overflowed the stack inside
        /// <c>Plugin.Load()</c>, so the game did not reach the main menu at all.
        ///
        /// <para>Every other patch in this repo is immune by accident: they open with
        /// <c>HasNetplaySessionStarted()</c>, which is an enum comparison on a managed field and
        /// never crosses the interop boundary. This class is the only one that deliberately has no
        /// such gate, which is why it is the only one that could reach the runtime from the
        /// patching context. A managed bool read is the same shape of protection.</para>
        /// </summary>
        internal static void MarkRuntimeReady() => runtimeReady = true;

        /// <summary>
        /// Guards against this class being re-entered through its own IL2CPP calls.
        ///
        /// <para>The overflow that <see cref="MarkRuntimeReady"/> describes was unbounded because
        /// nothing stopped the second entry. That latch keeps the recursion from starting during
        /// patching, which is the only place it has been seen — but BepInEx's invoke hook is
        /// installed for the whole process, so "it cannot happen after patching" is reasoning, not
        /// evidence. This makes the difference between that reasoning being wrong and the game
        /// dying: re-entry returns "not blocking", which drops the guard for one press rather than
        /// recursing. Erring toward not blocking is the same direction the rest of this class errs
        /// in — the worst case is the misfire the guard exists to prevent, never a stranded
        /// player.</para>
        ///
        /// <para>Thread-static because a latch stuck on by a call from an unexpected thread would
        /// disable the guard for the rest of the process.</para>
        /// </summary>
        [System.ThreadStatic]
        private static bool reentered;

        /// <summary>Called when a reward window opens.</summary>
        internal static void Arm()
        {
            if (!runtimeReady || reentered)
            {
                return;
            }

            // Re-arming while a window is still live is what made the controller unusable: the
            // arming hooks fire more than once per window, every call reset armedAt, so elapsed
            // never reached the grace and the guard never expired — it swallowed every confirm for
            // as long as the window was open, not for 0.35s. Arming is now idempotent per window:
            // the first call starts the timer and later ones are ignored until the whole window,
            // including the hold cap, has run out. Anything longer than HoldCapSeconds is a new
            // window by definition, because that is the longest this guard can ever block for.
            if (Time.unscaledTime - armedAt < HoldCapSeconds)
            {
                return;
            }

            reentered = true;
            try
            {
                ArmCore();
            }
            finally
            {
                reentered = false;
            }
        }

        private static void ArmCore()
        {
            armedAt = Time.unscaledTime;
            wasHeldOnOpen = IsSubmitHeld();

            if (wasHeldOnOpen && !loggedBinding)
            {
                // Once per process. This is the one fact the metadata cannot answer: whether
                // UISubmit and Jump are bound to the same physical button is Rewired
                // configuration, not code. If this line appears, they are — the window opened
                // with confirm already down, which is the accident this guard exists for.
                loggedBinding = true;
                Plugin.Log.LogInfo(
                    "[input] A reward window opened with the confirm action already held — the " +
                    "carry-over press guard is doing its job. Holding the choice until release " +
                    $"(capped at {HoldCapSeconds:F1}s).");
            }
        }

        /// <summary>True while a choice should be ignored as an accidental carry-over press.</summary>
        internal static bool IsBlocking()
        {
            // First, and before anything that could touch IL2CPP. See MarkRuntimeReady.
            if (!runtimeReady || reentered)
            {
                return false;
            }

            reentered = true;
            try
            {
                return IsBlockingCore();
            }
            finally
            {
                reentered = false;
            }
        }

        private static bool IsBlockingCore()
        {
            var grace = ModConfig.EncounterInputGraceSeconds?.Value ?? 0f;
            if (grace <= 0f)
            {
                return false;
            }

            var elapsed = Time.unscaledTime - armedAt;

            if (elapsed < grace)
            {
                return true;
            }

            // Held when the window opened, and still held: this press started before the window
            // existed, so it cannot be a choice.
            return wasHeldOnOpen && elapsed < HoldCapSeconds && IsSubmitHeld();
        }

        private static bool IsSubmitHeld()
        {
            if (!holdCheckAvailable)
            {
                return false;
            }

            try
            {
                var submit = MyInputManager.UISubmit;
                return !string.IsNullOrEmpty(submit) && MyInputManager.GetButton(submit);
            }
            catch (System.Exception ex)
            {
                // Never retried. A guard that cannot read the input must degrade to the time
                // window, not throw on a UI path every frame.
                holdCheckAvailable = false;
                Plugin.Log.LogWarning(
                    $"[input] Cannot read the confirm action ({ex.GetType().Name}); falling back to " +
                    "the time window only for the rest of this process.");
                return false;
            }
        }

        /// <summary>Clears the window, so a session teardown cannot leave one pending.</summary>
        internal static void Reset()
        {
            armedAt = -999f;
            wasHeldOnOpen = false;
        }
    }
}

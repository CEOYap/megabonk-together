using OmniBonk.Configuration;
using UnityEngine;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// Swallows reward choices briefly after a window opens, so a controller jump press (same
    /// button as confirm) cannot pick an irreversible reward in a window another player opened.
    /// <para>Guards the choice-commit methods, not input. Unscaled time (the game is paused); a confirm
    /// held at open extends it until release, capped by <see cref="HoldCapSeconds"/>. Cannot strand a player.</para>
    /// </summary>
    internal static class EncounterInputGrace
    {
        /// <summary>Upper bound on the hold extension, so a stuck input cannot lock the player out.</summary>
        private const float HoldCapSeconds = 1.5f;

        private static float armedAt = -999f;
        private static bool wasHeldOnOpen;
        private static bool holdCheckAvailable = true;
        private static bool loggedBinding;
        private static volatile bool runtimeReady;

        /// <summary>
        /// Set once <c>Harmony.PatchAll()</c> returns; until then nothing here may touch IL2CPP.
        /// <para>These patches can be entered during patching via BepInEx's runtime-invoke hook, and an
        /// IL2CPP call from there recursed until the stack overflowed in <c>Plugin.Load()</c>. Other
        /// patches are safe because their netplay gate is a managed read; this class has no such gate.</para>
        /// </summary>
        internal static void MarkRuntimeReady() => runtimeReady = true;

        /// <summary>
        /// Re-entry latch: re-entering through our own IL2CPP calls returns "not blocking" instead of
        /// recursing (see <see cref="MarkRuntimeReady"/>). Thread-static so another thread cannot latch it on.
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

            // Idempotent per window: arming hooks fire repeatedly, and re-arming would never expire.
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
                // Once per process: confirms UISubmit and Jump share a button in Rewired config.
                loggedBinding = true;
                Plugin.Log.LogInfo(
                    "[input] A reward window opened with the confirm action already held - the " +
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

            // Still held since before the window opened: not a choice.
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
                // Never retried; degrade to the time window.
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

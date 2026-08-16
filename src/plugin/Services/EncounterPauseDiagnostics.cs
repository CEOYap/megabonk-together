using Assets.Scripts.Managers;
using Assets.Scripts.Utility;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// OB-13. Records whether the encounter-window pause actually holds for as long as the window
    /// is open.
    ///
    /// <para><b>The question it answers.</b> Enemies take projectile damage while an encounter
    /// window has the game paused, and only when shared experience is on — where <i>both</i> peers
    /// are paused, so no peer is legitimately still shooting. The game's own projectile paths were
    /// decompiled and all gate on <c>MyTime.paused</c>
    /// (<c>ProjectileBase.Update</c>/<c>FixedUpdate</c>, <c>Rocket.FixedUpdate</c>), so a genuinely
    /// paused peer should not advance a projectile at all. The leading hypothesis is therefore that
    /// the pause does not hold: <c>MyTime.Unpause()</c> clears both <c>MyTime.paused</c> and
    /// <c>Physics.simulationMode</c>, and the mod calls it from six places. Shared experience is the
    /// only mode that <i>holds</i> the pause open — <c>PopReward_Postfix</c> unpauses immediately
    /// when it is off — so it is the only mode with a window long enough for another screen to
    /// interleave. Full entry: <c>docs/netplay/08-observed-bugs.md#ob-13</c>.</para>
    ///
    /// <para><b>Reads the diagnosis directly off the log.</b> If a line shows the window still open
    /// while <c>paused</c> has gone false or <c>simulationMode</c> has left <c>Script</c>, the
    /// hypothesis is confirmed and the fix is to stop the shared-experience pause sharing one global
    /// flag with five other screens. If the pause holds for the whole window, the damage comes from
    /// something that never consulted the flag, and the search moves to the mod's own injected
    /// components — none of which check it.</para>
    ///
    /// <para><b>Edge-triggered, not polled.</b> It logs only when one of the three tracked values
    /// changes, so a window that behaves costs exactly one line on open and one on close. That
    /// matters because a stray unpause may last a single frame — a one-second heartbeat could miss
    /// it entirely, which is the failure mode that would make a clean log meaningless.</para>
    ///
    /// <para><b>Off unless asked for.</b> Everything below the flag check reads game state through
    /// interop every frame, which is not free. <c>Sample</c> returns on the flag before touching
    /// anything, in the same shape as <see cref="AllocationDiagnostics"/>.</para>
    /// </summary>
    internal static class EncounterPauseDiagnostics
    {
        private static bool primed;
        private static bool lastWindowOpen;
        private static bool lastPaused;
        private static int lastSimulationMode;

        /// <summary>Called once per frame from the network tick. Guarded on the config flag first.</summary>
        internal static void Sample(bool enabled)
        {
            if (!enabled)
            {
                return;
            }

            var windowOpen = IsEncounterWindowOpen();
            var paused = MyTime.paused;
            var simulationMode = (int)Physics.simulationMode;

            if (primed
                && windowOpen == lastWindowOpen
                && paused == lastPaused
                && simulationMode == lastSimulationMode)
            {
                return;
            }

            primed = true;
            lastWindowOpen = windowOpen;
            lastPaused = paused;
            lastSimulationMode = simulationMode;

            // The reading that matters is "window open, but not paused" — call it out rather than
            // leaving it to be spotted by eye across a long log.
            var verdict = windowOpen && (!paused || simulationMode != (int)SimulationMode.Script)
                ? "  <-- WINDOW OPEN BUT NOT PAUSED (OB-13)"
                : string.Empty;

            Plugin.Log.LogInfo(
                $"[pause] encounterWindow={windowOpen} MyTime.paused={paused} " +
                $"Physics.simulationMode={(SimulationMode)simulationMode} t={Time.unscaledTime:F2}{verdict}");
        }

        /// <summary>
        /// Unity null semantics throughout: <c>UiManager.Instance</c> and its children are game
        /// objects, so a destroyed one is non-null managed-side and <c>?.</c> would not catch it.
        /// </summary>
        private static bool IsEncounterWindowOpen()
        {
            var uiManager = UiManager.Instance;
            if (uiManager == null)
            {
                return false;
            }

            var encounterWindows = uiManager.encounterWindows;
            if (encounterWindows == null)
            {
                return false;
            }

            return encounterWindows.encounterInProgress;
        }

        /// <summary>Re-primes so a new session's first transition is logged rather than compared to the old one.</summary>
        internal static void Reset()
        {
            primed = false;
        }
    }
}

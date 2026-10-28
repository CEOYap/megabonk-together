using Assets.Scripts.Managers;
using Assets.Scripts.Utility;
using UnityEngine;

namespace OmniBonk.Services
{
    /// <summary>
    /// OB-13: logs whether the encounter pause (<c>MyTime.paused</c>, <c>simulationMode</c>) holds
    /// while a window is open. Edge-triggered so a one-frame unpause is not missed. Off unless
    /// enabled: it reads interop every frame.
    /// </summary>
    internal static class EncounterPauseDiagnostics
    {
        private static IEncounterService encounterService;
        private static bool primed;
        private static bool lastWindowOpen;
        private static bool lastPaused;
        private static int lastSimulationMode;

        /// <summary>Per frame; checks the config flag first.</summary>
        internal static void Sample(bool enabled)
        {
            if (!enabled)
            {
                return;
            }

            encounterService ??= Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetService<IEncounterService>(Plugin.Services);
            var soloShop = encounterService?.IsSoloShop ?? false;

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

            // Flag the key reading: window open but not paused. A solo shop (OB-67) runs unpaused on purpose.
            var verdict = soloShop && windowOpen
                ? "  (solo shop, unpaused on purpose: OB-67)"
                : windowOpen && (!paused || simulationMode != (int)SimulationMode.Script)
                ? "  <-- WINDOW OPEN BUT NOT PAUSED (OB-13)"
                : string.Empty;

            Plugin.Log.LogInfo(
                $"[pause] encounterWindow={windowOpen} MyTime.paused={paused} " +
                $"Physics.simulationMode={(SimulationMode)simulationMode} t={Time.unscaledTime:F2}{verdict}");
        }

        /// <summary>Unity null checks, not <c>?.</c>: destroyed objects are non-null managed-side.</summary>
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

        /// <summary>Re-primes for a new session.</summary>
        internal static void Reset()
        {
            primed = false;
        }
    }
}

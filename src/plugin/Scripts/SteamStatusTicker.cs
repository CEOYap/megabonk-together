using MegabonkTogether.Configuration;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace MegabonkTogether.Scripts
{
    /// <summary>
    /// Drives <see cref="ISteamService"/>: asks Steam for relay access as soon as the game's own
    /// Steam is up, then watches until it is usable.
    ///
    /// <para><b>Why a ticker rather than a call in <c>Plugin.Load</c>.</b> The migration plan says
    /// to request relay access "at plugin startup". It cannot be done there: the game's
    /// <c>SteamManager</c> initialises from a <c>RuntimeInitializeOnLoadMethod(AfterSceneLoad)</c>,
    /// which runs after BepInEx has loaded plugins, so at <c>Load</c> time Steam's interface
    /// pointers are still null and calling through one is a native access violation. Retrying on a
    /// timer until the gate opens is as early as it can be asked for.</para>
    ///
    /// <para>It stops on its own. Steam either comes up in the first few seconds or it never will,
    /// and a mod that polls forever for something that cannot change is a per-frame cost with no
    /// reader.</para>
    /// </summary>
    internal class SteamStatusTicker : MonoBehaviour
    {
        private const float PollIntervalSeconds = 1f;

        /// <summary>
        /// How long to keep looking for a Steam that never arrives. Launched straight from the exe
        /// there is no Steam at all, and that is a supported way to run — the two-instance test
        /// harness has no other option for its second copy.
        /// </summary>
        private const float GiveUpAfterSeconds = 30f;

        /// <summary>Matches the cadence of the other Diagnostics toggles.</summary>
        private const float DiagnosticIntervalSeconds = 10f;

        private ISteamService steamService;

        private float pollAccumulator;
        private float diagnosticAccumulator;
        private float waitedForSteam;

        private bool settled;
        private bool loggedGiveUp;

        public void Awake()
        {
            // Resolved here, never in a static initialiser: RegisterTypeInIl2Cpp runs the type's
            // static constructor during Plugin.Load, before the DI host exists.
            steamService = Plugin.Services.GetService<ISteamService>();
        }

        public void Update()
        {
            if (steamService == null)
            {
                return;
            }

            var delta = Time.unscaledDeltaTime;

            if (!settled)
            {
                pollAccumulator += delta;
                if (pollAccumulator >= PollIntervalSeconds)
                {
                    pollAccumulator = 0f;
                    PollUntilSettled(delta);
                }
            }

            if (!ModConfig.LogSteamStatus.Value)
            {
                return;
            }

            diagnosticAccumulator += delta;
            if (diagnosticAccumulator >= DiagnosticIntervalSeconds)
            {
                diagnosticAccumulator = 0f;
                Plugin.Log.LogInfo($"[steam] {steamService.DescribeStatus()}");
            }
        }

        private void PollUntilSettled(float delta)
        {
            steamService.Poll();

            switch (steamService.Readiness)
            {
                case SteamReadiness.Ready:
                    // SteamService logs the arrival line itself; nothing to add.
                    settled = true;
                    return;

                case SteamReadiness.Failed:
                    Plugin.Log.LogWarning($"[steam] {steamService.DescribeStatus()}");
                    settled = true;
                    return;

                case SteamReadiness.Unavailable:
                    waitedForSteam += PollIntervalSeconds + delta;
                    if (waitedForSteam >= GiveUpAfterSeconds && !loggedGiveUp)
                    {
                        loggedGiveUp = true;
                        settled = true;

                        // Info, not warning. This is the expected state of an instance launched
                        // from the exe, and nothing in the mod needs Steam yet.
                        Plugin.Log.LogInfo(
                            "[steam] Steam never initialised, so relay access was not requested. "
                            + "Expected when the game is launched outside Steam.");
                    }

                    return;
            }
        }
    }
}

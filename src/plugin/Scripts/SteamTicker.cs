using OmniBonk.Configuration;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Scripts
{
    /// <summary>
    /// Drives every Steam concern: readiness, lobby, invite and transport polling.
    /// <para>Not done in <c>Plugin.Load</c>: the game's SteamManager initialises later, and calling
    /// Steam before then is a native access violation.</para>
    /// </summary>
    internal class SteamTicker : MonoBehaviour
    {
        private const float PollIntervalSeconds = 1f;

        /// <summary>Idle lobby cadence; every frame while a call is in flight (see <see cref="PollLobby"/>).</summary>
        private const float LobbyPollIntervalSeconds = 0.25f;

        /// <summary>Launching from the exe without Steam is supported.</summary>
        private const float GiveUpAfterSeconds = 30f;

        private const float DiagnosticIntervalSeconds = 10f;

        private ISteamService steamService;
        private ISteamLobbyService steamLobbyService;
        private SteamLobbySelfTest steamLobbySelfTest;
        private SteamNetSelfTest steamNetSelfTest;
        private ISteamNetSessionService steamNetSessionService;
        private ISteamNetTransport steamNetTransport;
        private SteamLobbyPresenceService steamLobbyPresenceService;
        private SteamInviteService steamInviteService;
        private SteamPersonaService steamPersonaService;

        private float pollAccumulator;
        private float lobbyPollAccumulator;
        private float diagnosticAccumulator;
        private float waitedForSteam;

        private bool settled;
        private bool loggedGiveUp;
        private bool reappliedStackTraces;

        public void Awake()
        {
            // Not in a static initialiser: RegisterTypeInIl2Cpp runs it before the DI host exists.
            steamService = Plugin.Services.GetService<ISteamService>();
            steamLobbyService = Plugin.Services.GetService<ISteamLobbyService>();
            steamLobbySelfTest = Plugin.Services.GetService<SteamLobbySelfTest>();
            steamNetSelfTest = Plugin.Services.GetService<SteamNetSelfTest>();
            steamNetSessionService = Plugin.Services.GetService<ISteamNetSessionService>();
            steamNetTransport = Plugin.Services.GetService<ISteamNetTransport>();
            steamLobbyPresenceService = Plugin.Services.GetService<SteamLobbyPresenceService>();
            steamInviteService = Plugin.Services.GetService<SteamInviteService>();
            steamPersonaService = Plugin.Services.GetService<SteamPersonaService>();
        }

        public void Update()
        {
            if (!reappliedStackTraces)
            {
                // After the first frame, so the game's own stack-trace policy cannot overwrite ours.
                reappliedStackTraces = true;
                Helpers.UnityDiagnostics.AllowReapply();
                Helpers.UnityDiagnostics.EnableStackTraces();
            }

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

            PollLobby(delta);

            // Every frame: the transport Poll is the receive pump, so throttling only adds latency.
            if (ModConfig.UseSteamTransport.Value)
            {
                steamNetSessionService?.Poll();
                steamNetTransport?.Poll();
            }

            // Every frame, matching a real session's cadence.
            if (ModConfig.SteamNetSelfTest.Value && steamNetSelfTest is { IsFinished: false })
            {
                steamNetSelfTest.Advance();
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

        /// <summary>Drives the lobby service, invite flow, presence bridge and lobby self-test.</summary>
        private void PollLobby(float delta)
        {
            if (steamLobbyService == null)
            {
                return;
            }

            // Every frame while a call is in flight: the game's own Steam pump drains the shared
            // dispatch pipe and can eat our completion (InvalidHandle). Relies on our Update running
            // first, which Unity does not guarantee; this narrows the race, it does not close it.
            if (!steamLobbyService.HasPendingCall)
            {
                lobbyPollAccumulator += delta;
                if (lobbyPollAccumulator < LobbyPollIntervalSeconds)
                {
                    return;
                }

                lobbyPollAccumulator = 0f;
            }

            steamLobbyService.Poll();

            // Mutually exclusive: the presence bridge would leave the self-test's lobby.
            var selfTestRunning = ModConfig.SteamLobbySelfTest.Value
                && steamLobbySelfTest is { IsFinished: false };

            if (selfTestRunning)
            {
                steamLobbySelfTest.Advance();
                return;
            }

            steamInviteService?.Poll();
            OpenNetplayMenuForInvite();

            // The bridge would leave the invite's lobby before its code is read.
            if (steamInviteService is { IsResolving: true })
            {
                return;
            }

            steamLobbyPresenceService?.Poll();
        }

        /// <summary>
        /// Opens the lobby for each resolved invite, only on the main menu (PLAY TOGETHER exists) so a
        /// mid-run invite waits. Consuming the code makes it once per invite; a once-per-launch latch
        /// here made every later invite join the Steam lobby with no panel, until a restart.
        /// </summary>
        private void OpenNetplayMenuForInvite()
        {
            if (steamInviteService == null
                || string.IsNullOrEmpty(steamInviteService.PendingJoinCode))
            {
                return;
            }

            var playTogether = Plugin.Instance?.PlayTogetherButton;
            if (playTogether == null || Scripts.Modal.LobbyPanel.Current != null)
            {
                return;
            }

            var code = steamInviteService.ConsumeJoinCode();
            if (string.IsNullOrEmpty(code))
            {
                return;
            }

            Plugin.Log.LogInfo($"[steam-invite] Opening the lobby to join room {code} from an invite.");
            playTogether.JoinLobby(code);
        }

        private void PollUntilSettled(float delta)
        {
            steamService.Poll();

            switch (steamService.Readiness)
            {
                case SteamReadiness.Ready:
                    settled = true;

                    steamPersonaService?.ApplyLocalPersonaName();
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

                        Plugin.Log.LogInfo(
                            "[steam] Steam never initialised, so relay access was not requested. "
                            + "Expected when the game is launched outside Steam.");
                    }

                    return;
            }
        }
    }
}

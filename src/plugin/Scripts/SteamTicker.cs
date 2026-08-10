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
    /// <para>The status half stops on its own. Steam either comes up in the first few seconds or it
    /// never will, and a mod that polls forever for something that cannot change is a per-frame
    /// cost with no reader.</para>
    ///
    /// <para><b>It also pumps the lobby service</b>, which is why it is no longer called
    /// <c>SteamStatusTicker</c>. Steam's asynchronous results are polled rather than delivered by
    /// callback — see <see cref="ISteamLobbyService"/> — so something has to drive them, and one
    /// ticker for every Steam concern beats a GameObject each.</para>
    /// </summary>
    internal class SteamTicker : MonoBehaviour
    {
        private const float PollIntervalSeconds = 1f;

        /// <summary>
        /// The idle cadence for the lobby service. While a Steam call is actually in flight the
        /// service is polled every frame instead — see <see cref="PollLobby"/> for why that is a
        /// correctness matter rather than a latency one.
        /// </summary>
        private const float LobbyPollIntervalSeconds = 0.25f;

        /// <summary>
        /// How long to keep looking for a Steam that never arrives. Launched straight from the exe
        /// there is no Steam at all, and that is a supported way to run — the two-instance test
        /// harness has no other option for its second copy.
        /// </summary>
        private const float GiveUpAfterSeconds = 30f;

        /// <summary>Matches the cadence of the other Diagnostics toggles.</summary>
        private const float DiagnosticIntervalSeconds = 10f;

        private ISteamService steamService;
        private ISteamLobbyService steamLobbyService;
        private SteamLobbySelfTest steamLobbySelfTest;
        private SteamLobbyPresenceService steamLobbyPresenceService;
        private SteamInviteService steamInviteService;

        private float pollAccumulator;
        private float lobbyPollAccumulator;
        private float diagnosticAccumulator;
        private float waitedForSteam;

        private bool settled;
        private bool loggedGiveUp;
        private bool reappliedStackTraces;
        private bool openedMenuForInvite;

        public void Awake()
        {
            // Resolved here, never in a static initialiser: RegisterTypeInIl2Cpp runs the type's
            // static constructor during Plugin.Load, before the DI host exists.
            steamService = Plugin.Services.GetService<ISteamService>();
            steamLobbyService = Plugin.Services.GetService<ISteamLobbyService>();
            steamLobbySelfTest = Plugin.Services.GetService<SteamLobbySelfTest>();
            steamLobbyPresenceService = Plugin.Services.GetService<SteamLobbyPresenceService>();
            steamInviteService = Plugin.Services.GetService<SteamInviteService>();
        }

        public void Update()
        {
            if (!reappliedStackTraces)
            {
                // A frame has run, so the game has finished setting its own stack-trace policy and
                // ours will not be overwritten by it.
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

        /// <summary>
        /// Drives the lobby service's pending work, and the self-test when it is switched on.
        ///
        /// <para>Both are no-ops until there is something to do — the service returns immediately
        /// with no call in flight, and the self-test returns immediately once finished — so this
        /// costs an accumulator and two branches in the common case where nobody is using Steam
        /// lobbies at all, which today is everybody.</para>
        /// </summary>
        private void PollLobby(float delta)
        {
            if (steamLobbyService == null)
            {
                return;
            }

            // Every frame while a call is in flight, on an interval otherwise.
            //
            // This is not impatience. The game pumps Steam's callback dispatcher from its own
            // Update, and that pump drains the shared manual-dispatch pipe and frees each message
            // — including the completion for our call. Polling on a 0.25s interval leaves roughly
            // fifteen frames in which the pump can reach the result first, and a run where it did
            // came back as InvalidHandle. Polling every frame means we look in the first frame the
            // result exists, and this GameObject is created in Plugin.Load, well before the game's
            // own SteamManager, so our Update runs first.
            //
            // That last sentence is the fragile part and it is worth saying out loud: Unity does
            // not guarantee execution order between two default-priority scripts. This narrows the
            // window rather than closing it. If InvalidHandle still appears, the answer is not a
            // faster poll — it is to stop depending on the shared pipe.
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

            // The two are mutually exclusive, and they have to be. Both own the same single Steam
            // lobby: the self-test creates one while no matchmaker lobby exists, and the presence
            // bridge leaves any Steam lobby it did not ask for — so left to run together, the
            // bridge would tear down the self-test's lobby the moment it appeared.
            var selfTestRunning = ModConfig.SteamLobbySelfTest.Value
                && steamLobbySelfTest is { IsFinished: false };

            if (selfTestRunning)
            {
                steamLobbySelfTest.Advance();
                return;
            }

            steamInviteService?.Poll();
            OpenNetplayMenuForInvite();

            // The invite flow joins a Steam lobby it does not own, and the bridge leaves any Steam
            // lobby it did not create — so the bridge waits until the code has been read out.
            if (steamInviteService is { IsResolving: true })
            {
                return;
            }

            // After Poll, so a lobby that finished being created this tick is already InLobby and
            // the bridge can publish into it without waiting another quarter second.
            steamLobbyPresenceService?.Poll();
        }

        /// <summary>
        /// Opens the netplay menu when an invite has resolved to a room code and nothing is showing
        /// it yet, so accepting an invite takes the player into the lobby rather than leaving them
        /// on the main menu wondering.
        ///
        /// <para><b>This is a layering compromise and worth naming as one.</b> A Steam ticker has no
        /// business knowing about menus. It is here because it is the only always-running component
        /// that sees the invite service, and the alternatives were worse: a service creating
        /// GameObjects, or a Harmony patch growing UI logic. If a third thing ever needs to react to
        /// an invite, this belongs in its own MonoBehaviour.</para>
        ///
        /// <para>Guarded on the PLAY TOGETHER button existing, which is the cheap way of asking "is
        /// the main menu up" — during a run it is gone, and an invite arriving mid-game should
        /// leave the code waiting rather than tear the player out of their session. Latched so a
        /// player who closes the menu is not fighting it to stay closed.</para>
        /// </summary>
        private void OpenNetplayMenuForInvite()
        {
            if (openedMenuForInvite
                || steamInviteService == null
                || string.IsNullOrEmpty(steamInviteService.PendingJoinCode))
            {
                return;
            }

            var playTogether = Plugin.Instance?.PlayTogetherButton;
            if (playTogether == null || Plugin.Instance.NetworkTab != null)
            {
                return;
            }

            openedMenuForInvite = true;
            Plugin.Log.LogInfo("[steam-invite] Opening the netplay menu to act on an invite.");
            playTogether.OpenNetworkTab();
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

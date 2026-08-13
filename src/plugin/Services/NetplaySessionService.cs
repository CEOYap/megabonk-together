using MegabonkTogether.Common.Models;
using MegabonkTogether.Configuration;
using MegabonkTogether.Helpers;
using System;
using System.Collections;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// The session lifecycle, lifted out of <c>NetworkMenuTab</c>'s button handlers and coroutines.
    ///
    /// <para>The two coroutines it replaces were near-copies of each other that differed only in
    /// which screen they put back on failure. Here there is one, because the difference was never
    /// about connecting — it was about who was showing it.</para>
    /// </summary>
    internal class NetplaySessionService(
        ISteamLobbyService steamLobbyService,
        ISteamNetSessionService steamNetSessionService) : INetplaySessionService
    {
        /// <summary>
        /// How long to wait for Steam to create or join a lobby. Steam's own calls are usually
        /// sub-second; this is generous because the alternative to waiting is a lobby panel with no
        /// lobby behind it.
        /// </summary>
        private const float SteamLobbyWaitSeconds = 20f;

        /// <summary>
        /// How long to wait for the sockets after the lobby exists. Generous because Steam's relay
        /// access can still be coming up when a player presses Host seconds after launch, and that
        /// wait is ordinary rather than a fault.
        /// </summary>
        private const float SteamSessionWaitSeconds = 30f;

        /// <summary>
        /// How long to wait for the matchmaker before giving up. Carried over unchanged from the
        /// menu's coroutines, where both used 30.
        /// </summary>
        private const float ConnectTimeoutSeconds = 30f;

        /// <summary>
        /// How long a host waits for its room code after the connection reports ready. The code
        /// arrives on the websocket a moment later; a host without one can still use the panel to
        /// leave, so this is bounded rather than required.
        /// </summary>
        private const float RoomCodeWaitSeconds = 5f;

        private const float PollIntervalSeconds = 0.5f;

        private Coroutine connectionCoroutine;

        public NetplayConnectState State { get; private set; } = NetplayConnectState.Idle;

        public string StatusMessage { get; private set; } = "";

        public bool IsBusy =>
            State == NetplayConnectState.Connecting || State == NetplayConnectState.WaitingForMatch;

        public event Action StateChanged;

        public void Host()
        {
            if (!BeginAttempt("Creating room..."))
            {
                return;
            }

            Plugin.Instance.Mode.Mode = NetworkModeType.Friendlies;
            Plugin.Instance.Mode.Role = Role.Host;

            Start(ModConfig.UseSteamTransport.Value ? WatchSteamHost() : WatchFriendlies());
        }

        public void Join(string code)
        {
            var normalised = code?.Trim().ToUpperInvariant() ?? "";
            if (string.IsNullOrWhiteSpace(normalised))
            {
                Fail("Please enter a room code");
                return;
            }

            if (!BeginAttempt("Joining room..."))
            {
                return;
            }

            Plugin.Instance.Mode.Mode = NetworkModeType.Friendlies;
            Plugin.Instance.Mode.Role = Role.Client;
            Plugin.Instance.Mode.RoomCode = normalised;

            Start(ModConfig.UseSteamTransport.Value ? WatchSteamJoin(normalised) : WatchFriendlies());
        }

        public void Quickplay()
        {
            if (ModConfig.UseSteamTransport.Value)
            {
                // Quickplay needs a pool of strangers to match against, which on Steam means a
                // lobby browser filtered on the protocol version. That exists as a plan and not as
                // code, and silently falling back to the matchmaker would put this player on a
                // transport the setting says they are not using.
                Fail("Quickplay is not available on the Steam transport yet. Host or join by code.");
                return;
            }

            if (!BeginAttempt("Connecting..."))
            {
                return;
            }

            Plugin.Instance.Mode.Mode = NetworkModeType.Random;

            Start(WatchQuickplay());
        }

        public void Cancel()
        {
            if (connectionCoroutine != null)
            {
                CoroutineRunner.Instance.StopCoroutine(connectionCoroutine);
                connectionCoroutine = null;
            }

            if (State == NetplayConnectState.Idle)
            {
                return;
            }

            try
            {
                Plugin.Instance.NetworkHandler.ResetNetworking();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[session] ResetNetworking threw during cancel: {ex.GetType().Name}: {ex.Message}");
            }

            // Leaving the lobby is what ends a Steam session, not a separate teardown call: the
            // session service watches lobby membership and shuts the transport down when it goes.
            // One thing ends the session rather than two that could disagree about whether it has.
            if (ModConfig.UseSteamTransport.Value)
            {
                steamLobbyService.LeaveLobby();
            }

            SetState(NetplayConnectState.Idle, "");
        }

        /// <summary>
        /// Refuses a second attempt while one is running, rather than starting a coroutine that
        /// would race the first over <c>Plugin.Instance.Mode</c>.
        /// </summary>
        private bool BeginAttempt(string message)
        {
            if (IsBusy)
            {
                Plugin.Log.LogWarning($"[session] Ignoring a start request while {State}.");
                return false;
            }

            SetState(NetplayConnectState.Connecting, message);

            // The Steam path has no matchmaker to reach, and starting the websocket anyway would
            // hand this player a room on a server whose session they are never going to join.
            if (!ModConfig.UseSteamTransport.Value)
            {
                Plugin.Instance.NetworkHandler.HandleNetworking();
            }

            return true;
        }

        private void Start(IEnumerator routine)
        {
            connectionCoroutine = CoroutineRunner.Instance.Run(routine);
        }

        /// <summary>Waits for the matchmaker connection. Shared by every entry point.</summary>
        private IEnumerator AwaitMatchmaker()
        {
            var elapsed = 0f;
            while (elapsed < ConnectTimeoutSeconds
                && !Plugin.Instance.NetworkHandler.IsConnectedToMatchMaker.HasValue)
            {
                yield return new WaitForSeconds(PollIntervalSeconds);
                elapsed += PollIntervalSeconds;
            }
        }

        private IEnumerator WatchFriendlies()
        {
            yield return AwaitMatchmaker();

            if (Plugin.Instance.NetworkHandler.IsConnectedToMatchMaker != true)
            {
                FailAndReset($"Failed to connect to server: {Plugin.Instance.NetworkHandler.MatchMakerFailureMessage}");
                yield break;
            }

            PlaySelectSfx();

            if (Plugin.Instance.Mode.Role == Role.Host)
            {
                // The code arrives on the websocket a moment after the connection reports ready.
                var codeWait = 0f;
                while (codeWait < RoomCodeWaitSeconds && string.IsNullOrEmpty(Plugin.Instance.Mode.RoomCode))
                {
                    yield return new WaitForSeconds(0.1f);
                    codeWait += 0.1f;
                }

                if (string.IsNullOrEmpty(Plugin.Instance.Mode.RoomCode))
                {
                    Plugin.Log.LogWarning("[session] Hosting started but no room code arrived; the lobby opens without one.");
                }

                SetState(NetplayConnectState.Ready, "");
                yield break;
            }

            var elapsed = 0f;
            while (elapsed < ConnectTimeoutSeconds && !Plugin.Instance.NetworkHandler.HasFoundMatch.HasValue)
            {
                if (Plugin.Instance.NetworkHandler.IsNetworkInterruptedStatus)
                {
                    FailAndReset($"Network interrupted. Please try again: {Plugin.Instance.NetworkHandler.MatchMakerFailureMessage}");
                    yield break;
                }

                yield return new WaitForSeconds(PollIntervalSeconds);
                elapsed += PollIntervalSeconds;
            }

            if (Plugin.Instance.NetworkHandler.HasFoundMatch != true)
            {
                FailAndReset($"Failed to join: {Plugin.Instance.NetworkHandler.MatchMakerFailureMessage}");
                yield break;
            }

            SetState(NetplayConnectState.Ready, "");
        }

        /// <summary>
        /// Hosting on Steam: create the lobby, then let the lobby panel take over. The socket is not
        /// opened here — <see cref="ISteamNetSessionService"/> does that the moment it sees a lobby
        /// we own, and publishes readiness only once it has succeeded.
        /// </summary>
        private IEnumerator WatchSteamHost()
        {
            steamLobbyService.CreateLobby(maxMembers: 6);

            yield return AwaitSteamLobby();

            if (steamLobbyService.State != SteamLobbyState.InLobby)
            {
                FailAndReset($"Could not create a Steam lobby: {steamLobbyService.DescribeStatus()}");
                yield break;
            }

            PlaySelectSfx();

            // The lobby panel reads the room code off Mode, so the Steam lobby's own code goes here
            // and the panel needs no knowledge of which transport produced it.
            Plugin.Instance.Mode.RoomCode = steamLobbyService.LobbyCode;

            // The lobby existing is not the session existing. Starting the netplay loops before the
            // listen socket is open produced a host with no transport and an empty roster, which
            // the lobby panel reported as "Cannot toggle readiness: no local player" and which a
            // client could wait on forever.
            yield return AwaitSteamSession();

            if (steamNetSessionService.State != SteamSessionState.Live)
            {
                FailAndReset(SteamSessionFailure("Could not start hosting"));
                yield break;
            }

            Plugin.Instance.NetworkHandler.BeginSteamSession(isHost: true);
            SetState(NetplayConnectState.Ready, "");
        }

        /// <summary>
        /// Joining on Steam: find and enter the lobby by its code. Connecting to the host is not
        /// done here — the session service waits until the host publishes that its socket is open,
        /// because connecting before that fails in a way that reads like a NAT problem.
        /// </summary>
        private IEnumerator WatchSteamJoin(string code)
        {
            // Accepting an invite already put us in the lobby, and searching for it again would
            // leave and re-enter the session we are trying to join.
            var alreadyHere = steamLobbyService.State == SteamLobbyState.InLobby
                && string.Equals(steamLobbyService.LobbyCode, code, StringComparison.OrdinalIgnoreCase);

            if (!alreadyHere)
            {
                steamLobbyService.JoinByCode(code);
                yield return AwaitSteamLobby();
            }

            if (steamLobbyService.State != SteamLobbyState.InLobby)
            {
                FailAndReset($"Could not join room {code}: {steamLobbyService.DescribeStatus()}");
                yield break;
            }

            PlaySelectSfx();

            yield return AwaitSteamSession();

            if (steamNetSessionService.State != SteamSessionState.Live)
            {
                FailAndReset(SteamSessionFailure($"Could not join room {code}"));
                yield break;
            }

            Plugin.Instance.NetworkHandler.BeginSteamSession(isHost: false);
            SetState(NetplayConnectState.Ready, "");
        }

        /// <summary>Waits for the sockets, which the session service brings up on its own tick.</summary>
        private IEnumerator AwaitSteamSession()
        {
            var elapsed = 0f;
            while (elapsed < SteamSessionWaitSeconds
                && steamNetSessionService.State != SteamSessionState.Live
                && steamNetSessionService.State != SteamSessionState.Failed)
            {
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }
        }

        /// <summary>
        /// A failure message that says what actually went wrong rather than "timed out". The
        /// session service records the detail; a bare timeout reads as a network fault even when
        /// the cause was Steam still starting.
        /// </summary>
        private string SteamSessionFailure(string prefix) =>
            string.IsNullOrEmpty(steamNetSessionService.StatusMessage)
                ? $"{prefix}: timed out waiting for Steam."
                : $"{prefix}: {steamNetSessionService.StatusMessage}";

        /// <summary>Waits for a create or join to settle, either way.</summary>
        private IEnumerator AwaitSteamLobby()
        {
            var elapsed = 0f;
            while (elapsed < SteamLobbyWaitSeconds
                && steamLobbyService.State != SteamLobbyState.InLobby
                && steamLobbyService.State != SteamLobbyState.Failed)
            {
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }
        }

        private IEnumerator WatchQuickplay()
        {
            yield return AwaitMatchmaker();

            if (Plugin.Instance.NetworkHandler.IsConnectedToMatchMaker != true)
            {
                FailAndReset($"Failed to connect to server: {Plugin.Instance.NetworkHandler.MatchMakerFailureMessage}");
                yield break;
            }

            PlaySelectSfx();

            var sharedExp = ModConfig.EnabledSharedExperience.Value
                ? "<color=green>ON</color>"
                : "<color=red>OFF</color>";
            SetState(
                NetplayConnectState.WaitingForMatch,
                $"Waiting for a match...\n(You can only match people with shared experience {sharedExp})");

            while (!Plugin.Instance.NetworkHandler.HasFoundMatch.HasValue)
            {
                if (Plugin.Instance.NetworkHandler.IsNetworkInterruptedStatus)
                {
                    FailAndReset($"Network interrupted. Please try again: {Plugin.Instance.NetworkHandler.MatchMakerFailureMessage}");
                    yield break;
                }

                yield return new WaitForSeconds(PollIntervalSeconds);
            }

            if (Plugin.Instance.NetworkHandler.HasFoundMatch != true)
            {
                FailAndReset($"Failed to find a match: {Plugin.Instance.NetworkHandler.MatchMakerFailureMessage}");
                yield break;
            }

            SetState(NetplayConnectState.Ready, "");
        }

        private void FailAndReset(string message)
        {
            connectionCoroutine = null;

            try
            {
                Plugin.Instance.NetworkHandler.ResetNetworking();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[session] ResetNetworking threw after a failure: {ex.GetType().Name}: {ex.Message}");
            }

            Fail(message);
        }

        private void Fail(string message)
        {
            Plugin.Log.LogWarning($"[session] {message}");
            SetState(NetplayConnectState.Failed, message);
        }

        private void SetState(NetplayConnectState state, string message)
        {
            State = state;
            StatusMessage = message ?? "";

            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                // A subscriber throwing must not abandon the coroutine that raised this, or the
                // session would sit in a state nobody advances.
                Plugin.Log.LogError($"[session] A StateChanged subscriber threw: {ex}");
            }
        }

        private static void PlaySelectSfx()
        {
            var audio = AudioManager.Instance;
            if (audio != null && audio.uiSelect != null && audio.uiSelect.sounds.Count > 0)
            {
                audio.PlaySfx(audio.uiSelect.sounds[0]);
            }
        }
    }
}

using OmniBonk.Common.Models;
using OmniBonk.Configuration;
using OmniBonk.Helpers;
using System;
using System.Collections;
using UnityEngine;

namespace OmniBonk.Services
{
    /// <summary>The netplay session lifecycle: host, join, quickplay, cancel.</summary>
    internal class NetplaySessionService(
        ISteamLobbyService steamLobbyService,
        ISteamNetSessionService steamNetSessionService) : INetplaySessionService
    {
        private const float SteamLobbyWaitSeconds = 20f;

        /// <summary>Generous: relay access may still be coming up just after launch.</summary>
        private const float SteamSessionWaitSeconds = 30f;

        private const float ConnectTimeoutSeconds = 30f;

        /// <summary>Bounded, not required: the code arrives on the websocket shortly after ready.</summary>
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
                // No Steam lobby browser yet; never fall back silently to the matchmaker.
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

            // Leaving the lobby is what ends a Steam session; the session service watches membership.
            if (ModConfig.UseSteamTransport.Value)
            {
                steamLobbyService.LeaveLobby();
            }

            SetState(NetplayConnectState.Idle, "");
        }

        /// <summary>Refuses while busy, or two coroutines would race over <c>Mode</c>.</summary>
        private bool BeginAttempt(string message)
        {
            if (IsBusy)
            {
                Plugin.Log.LogWarning($"[session] Ignoring a start request while {State}.");
                return false;
            }

            SetState(NetplayConnectState.Connecting, message);

            // Steam has no matchmaker; the websocket would hand out an unused room.
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

        /// <summary>The socket is opened by <see cref="ISteamNetSessionService"/> once it sees our lobby.</summary>
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

            Plugin.Instance.Mode.RoomCode = steamLobbyService.LobbyCode;

            // Wait for the listen socket: starting loops earlier leaves a host with no transport.
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
        /// The session service connects only once the host publishes its socket is open.
        /// </summary>
        private IEnumerator WatchSteamJoin(string code)
        {
            // An accepted invite already put us in the lobby.
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

        private string SteamSessionFailure(string prefix) =>
            string.IsNullOrEmpty(steamNetSessionService.StatusMessage)
                ? $"{prefix}: timed out waiting for Steam."
                : $"{prefix}: {steamNetSessionService.StatusMessage}";

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
                // Must not abandon the coroutine that raised this.
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

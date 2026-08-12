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
    internal class NetplaySessionService : INetplaySessionService
    {
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

            Start(WatchFriendlies());
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

            Start(WatchFriendlies());
        }

        public void Quickplay()
        {
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
            Plugin.Instance.NetworkHandler.HandleNetworking();
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

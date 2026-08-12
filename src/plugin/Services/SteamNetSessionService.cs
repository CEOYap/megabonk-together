using MegabonkTogether.Common;

namespace MegabonkTogether.Services
{
    public enum SteamSessionState
    {
        /// <summary>Not in a Steam-carried session.</summary>
        Idle,

        /// <summary>Host: waiting for the lobby. Client: waiting for the host to say it is listening.</summary>
        Waiting,

        /// <summary>Sockets are up. The host is listening, or the client has connected.</summary>
        Live,

        /// <summary><see cref="ISteamNetSessionService.StatusMessage"/> says what went wrong.</summary>
        Failed,
    }

    /// <summary>
    /// Turns a Steam lobby into a live session: opens the host's socket, tells the lobby it is
    /// open, and connects each client to the owner once it is.
    ///
    /// <para><b>The handshake is one-way through the lobby, and that is the whole design.</b> The
    /// host publishes <see cref="SteamLobbyKeys.ServerReady"/> only after
    /// <c>CreateListenSocketP2P</c> has succeeded; a client connects only after reading it, and
    /// connects to <c>GetLobbyOwner</c> rather than to anyone's idea of who the host is. Steam
    /// permits only the lobby owner to write lobby data, so there is exactly one writer, one moment,
    /// and nothing for the two ends to disagree about.</para>
    ///
    /// <para><b>Why not just let the client connect when it joins.</b> Connecting before the host's
    /// socket exists fails in a way that reads like a NAT problem, so a client would have to retry
    /// on its own schedule — and two peers running their own retry timers over their own idea of
    /// whether the other is ready is exactly the shape that broke Steam-backed readiness and got it
    /// reverted. A published flag replaces both timers with one fact.</para>
    ///
    /// <para><b>The seed comes through the lobby too</b>, for the same reason: on the rendezvous
    /// path it arrives inside <c>MatchInfo</c>, and with the Steam lobby as the session there is no
    /// such message.</para>
    /// </summary>
    public interface ISteamNetSessionService
    {
        SteamSessionState State { get; }

        string StatusMessage { get; }

        /// <summary>
        /// Drives the session forward. Cheap and idempotent when there is nothing to do; safe to
        /// call every frame, and it needs to be, because the client's connect is gated on lobby data
        /// that can change at any tick.
        /// </summary>
        void Poll();

        /// <summary>Tears the session down and forgets everything. Safe when idle.</summary>
        void Reset();
    }

    internal class SteamNetSessionService(
        ISteamLobbyService steamLobbyService,
        ISteamNetTransport transport,
        IPlayerManagerService playerManagerService) : ISteamNetSessionService
    {
        private const string ServerReadyValue = "1";

        public SteamSessionState State { get; private set; } = SteamSessionState.Idle;

        public string StatusMessage { get; private set; } = string.Empty;

        /// <summary>
        /// Latched so a failed start is not retried every frame. A Steam socket that refused to open
        /// will refuse again, and the log would be the only thing that grew.
        /// </summary>
        private bool attempted;

        private bool seedApplied;

        public void Poll()
        {
            if (State == SteamSessionState.Failed)
            {
                return;
            }

            if (steamLobbyService.State != SteamLobbyState.InLobby)
            {
                // Left the lobby, or never entered one. A session cannot outlive the lobby that
                // identifies it.
                if (State != SteamSessionState.Idle)
                {
                    Reset();
                }

                return;
            }

            if (steamLobbyService.IsOwner)
            {
                PollAsHost();
                return;
            }

            PollAsClient();
        }

        private void PollAsHost()
        {
            if (!attempted)
            {
                attempted = true;
                State = SteamSessionState.Waiting;

                if (!transport.StartHost())
                {
                    Fail($"Could not open a Steam socket: {transport.DescribeStatus()}");
                    return;
                }

                // Published only now, after the socket is actually open. Announcing readiness first
                // and opening second would invite exactly the connection failures this flag exists
                // to prevent.
                PublishSeed();

                if (!steamLobbyService.SetLobbyData(SteamLobbyKeys.ServerReady, ServerReadyValue))
                {
                    // The socket is up and peers already in the lobby can still be accepted; what is
                    // lost is the signal that tells a client it is safe to connect. Worth a loud
                    // line rather than a silent half-session.
                    Plugin.Log.LogError(
                        "[steam-session] The listen socket is open but publishing ServerReady failed, "
                        + "so clients will not know to connect.");
                }

                State = SteamSessionState.Live;
                Plugin.Log.LogInfo("[steam-session] Hosting; the lobby has been told the socket is open.");
            }
        }

        private void PollAsClient()
        {
            if (attempted)
            {
                return;
            }

            State = SteamSessionState.Waiting;

            // The gate. Until the owner says its socket is open, connecting is a failure that looks
            // like a network fault, so this simply waits — there is no timer here on purpose.
            if (steamLobbyService.GetLobbyData(SteamLobbyKeys.ServerReady) != ServerReadyValue)
            {
                return;
            }

            var ownerSteamId = steamLobbyService.OwnerSteamId;
            if (ownerSteamId == 0UL)
            {
                // Steam has not told us who owns the lobby yet. Not a failure — the next tick will
                // have it — and deliberately not latched.
                return;
            }

            attempted = true;
            ApplySeed();

            if (!transport.ConnectToHost(ownerSteamId))
            {
                Fail($"Could not connect to the host: {transport.DescribeStatus()}");
                return;
            }

            State = SteamSessionState.Live;
            Plugin.Log.LogInfo($"[steam-session] Connecting to lobby owner {ownerSteamId}.");
        }

        /// <summary>
        /// Publishes the seed the host has already given the player manager, so every peer generates
        /// the same world.
        /// </summary>
        private void PublishSeed()
        {
            var seed = playerManagerService.GetSeed();

            if (!steamLobbyService.SetLobbyData(SteamLobbyKeys.Seed, seed.ToString()))
            {
                Plugin.Log.LogError(
                    $"[steam-session] Could not publish the seed ({seed}). Clients would generate a "
                    + "different world, so this is a desync waiting to happen rather than a cosmetic "
                    + "failure.");
            }
        }

        /// <summary>
        /// Reads the host's seed out of the lobby before connecting. Deliberately before rather than
        /// after: world generation is downstream of this, and a client that connected first could
        /// start building a world from a seed it has not read yet.
        /// </summary>
        private void ApplySeed()
        {
            if (seedApplied)
            {
                return;
            }

            var published = steamLobbyService.GetLobbyData(SteamLobbyKeys.Seed);
            if (!int.TryParse(published, out var seed))
            {
                Plugin.Log.LogError(
                    $"[steam-session] The lobby published no usable seed ('{published}'), so this "
                    + "client would generate a different world from its host.");
                return;
            }

            seedApplied = true;
            playerManagerService.SetSeed(seed);
            Plugin.Log.LogInfo($"[steam-session] Seed {seed} taken from the lobby.");
        }

        public void Reset()
        {
            if (State == SteamSessionState.Idle && !attempted)
            {
                return;
            }

            transport.Shutdown();

            attempted = false;
            seedApplied = false;
            State = SteamSessionState.Idle;
            StatusMessage = string.Empty;
        }

        private void Fail(string message)
        {
            State = SteamSessionState.Failed;
            StatusMessage = message;
            Plugin.Log.LogError($"[steam-session] {message}");
        }
    }
}

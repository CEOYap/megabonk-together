using OmniBonk.Common;

namespace OmniBonk.Services
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
    /// Turns a Steam lobby into a live session. The host publishes <see cref="SteamLobbyKeys.ServerReady"/>
    /// (and the seed) only after its socket opens; clients wait for it and connect to the lobby owner.
    /// One writer, no retry timers.
    /// </summary>
    public interface ISteamNetSessionService
    {
        SteamSessionState State { get; }

        string StatusMessage { get; }

        /// <summary>Idempotent; call every frame.</summary>
        void Poll();

        void Reset();
    }

    internal class SteamNetSessionService(
        ISteamLobbyService steamLobbyService,
        ISteamNetTransport transport,
        ISteamService steamService,
        IPlayerManagerService playerManagerService) : ISteamNetSessionService
    {
        private const string ServerReadyValue = "1";

        public SteamSessionState State { get; private set; } = SteamSessionState.Idle;

        public string StatusMessage { get; private set; } = string.Empty;

        /// <summary>Latched so a failed start is not retried every frame.</summary>
        private bool attempted;

        private bool seedApplied;

        private const int WaitLogIntervalMs = 1000;
        private long nextWaitLogTick;

        public void Poll()
        {
            if (State == SteamSessionState.Failed)
            {
                return;
            }

            if (steamLobbyService.State != SteamLobbyState.InLobby)
            {
                if (State != SteamSessionState.Idle)
                {
                    Reset();
                }

                return;
            }

            PublishLocalConnectionId();

            if (steamLobbyService.IsOwner)
            {
                PollAsHost();
                return;
            }

            PollAsClient();
        }

        /// <summary>See <see cref="SteamLobbyKeys.MemberConnectionId"/>.</summary>
        private uint? publishedConnectionId;

        /// <summary>Member data is per lobby: a new lobby needs the id again even if it is unchanged.</summary>
        private ulong publishedLobbyId;

        private void PublishLocalConnectionId()
        {
            var local = playerManagerService.GetLocalPlayer();
            if (local == null)
            {
                return;
            }

            // Once per value: SetLobbyMemberData is a network round trip.
            if (publishedConnectionId == local.ConnectionId && publishedLobbyId == steamLobbyService.LobbyId)
            {
                return;
            }

            if (steamLobbyService.SetLocalMemberData(SteamLobbyKeys.MemberConnectionId, local.ConnectionId.ToString()))
            {
                publishedConnectionId = local.ConnectionId;
                publishedLobbyId = steamLobbyService.LobbyId;
                Plugin.Log.LogInfo($"[steam-lobby] Published connection id {local.ConnectionId} for this member.");
            }
        }

        private void PollAsHost()
        {
            if (!attempted)
            {
                State = SteamSessionState.Waiting;

                // "Not ready yet" (SDR warming up) must be waited out before latching the attempt.
                if (!IsSteamReady())
                {
                    return;
                }

                attempted = true;

                if (!transport.StartHost())
                {
                    Fail($"Could not open a Steam socket: {transport.DescribeStatus()}");
                    return;
                }

                // Only after the socket is open.
                PublishSessionSettings();

                if (!steamLobbyService.SetLobbyData(SteamLobbyKeys.ServerReady, ServerReadyValue))
                {
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

            if (!IsSteamReady())
            {
                return;
            }

            // Wait (no timer) until the owner's socket is open.
            if (steamLobbyService.GetLobbyData(SteamLobbyKeys.ServerReady) != ServerReadyValue)
            {
                LogWaitingThrottled("the host has not opened its socket yet");
                return;
            }

            var ownerSteamId = steamLobbyService.OwnerSteamId;
            if (ownerSteamId == 0UL)
            {
                // Owner not known yet; not latched.
                return;
            }

            // Gated on the seed, or this client would build a different world.
            if (!ApplySessionSettings())
            {
                return;
            }

            attempted = true;

            if (!transport.ConnectToHost(ownerSteamId))
            {
                Fail($"Could not connect to the host: {transport.DescribeStatus()}");
                return;
            }

            State = SteamSessionState.Live;
            Plugin.Log.LogInfo($"[steam-session] Connecting to lobby owner {ownerSteamId}.");
        }

        /// <summary>False for the first seconds of every launch; callers wait rather than fail.</summary>
        private bool IsSteamReady()
        {
            var readiness = steamService.Readiness;

            if (readiness == SteamReadiness.Ready)
            {
                return true;
            }

            if (readiness == SteamReadiness.Failed || readiness == SteamReadiness.Unavailable)
            {
                Fail($"Steam is not usable: {steamService.DescribeStatus()}");
                return false;
            }

            LogWaitingThrottled($"Steam is still coming up ({readiness})");
            return false;
        }

        private void LogWaitingThrottled(string reason)
        {
            var now = System.Environment.TickCount64;
            if (now < nextWaitLogTick)
            {
                return;
            }

            nextWaitLogTick = now + WaitLogIntervalMs;
            Plugin.Log.LogInfo($"[steam-session] Waiting: {reason}.");
        }

        private void PublishSessionSettings()
        {
            var seed = playerManagerService.GetSeed();

            // No MatchInfo on the Steam path, so the host generates the seed (clock xor lobby id).
            if (seed == 0)
            {
                seed = (int)((((ulong)System.DateTime.UtcNow.Ticks) ^ steamLobbyService.LobbyId) & 0x7FFFFFFFUL);
                playerManagerService.SetSeed(seed);
                Plugin.Log.LogInfo($"[steam-session] Generated seed {seed} for this session.");
            }

            if (!steamLobbyService.SetLobbyData(SteamLobbyKeys.Seed, seed.ToString()))
            {
                Plugin.Log.LogError(
                    $"[steam-session] Could not publish the seed ({seed}). Clients would generate a "
                    + "different world, so this is a desync waiting to happen rather than a cosmetic "
                    + "failure.");
            }

            // Published before readiness; the host's settings decide the run and are also set on Mode.
            if (!steamLobbyService.SetLobbyData(SteamLobbyKeys.Transport, SteamLobbyKeys.TransportSteam))
            {
                Plugin.Log.LogWarning(
                    "[steam-session] Could not publish the transport marker; a friend whose "
                    + "UseSteamTransport is off will get a confusing failure rather than a clear one.");
            }

            var sharedExperience = Configuration.ModConfig.EnabledSharedExperience.Value;
            Plugin.Instance.Mode.EnabledSharedExperience = sharedExperience;

            if (!steamLobbyService.SetLobbyData(SteamLobbyKeys.SharedExperience, sharedExperience ? "1" : "0"))
            {
                Plugin.Log.LogError(
                    "[steam-session] Could not publish the Shared Experience setting, so clients "
                    + "would play under different XP rules from the host.");
            }

            Plugin.Log.LogInfo(
                $"[steam-session] Session settings published: seed {seed}, shared experience "
                + $"{(sharedExperience ? "on" : "off")}.");
        }

        /// <summary>Reads the host's settings before connecting, since world generation depends on the seed.</summary>
        private bool ApplySessionSettings()
        {
            if (seedApplied)
            {
                return true;
            }

            // Zero means unpublished.
            var published = steamLobbyService.GetLobbyData(SteamLobbyKeys.Seed);
            if (!int.TryParse(published, out var seed) || seed == 0)
            {
                Plugin.Log.LogWarning(
                    $"[steam-session] Waiting for the host's seed (lobby says '{published}').");
                return false;
            }

            seedApplied = true;
            playerManagerService.SetSeed(seed);

            // From the host, never local config.
            var sharedExperience = steamLobbyService.GetLobbyData(SteamLobbyKeys.SharedExperience) == "1";
            Plugin.Instance.Mode.EnabledSharedExperience = sharedExperience;

            Plugin.Log.LogInfo(
                $"[steam-session] Session settings taken from the lobby: seed {seed}, shared "
                + $"experience {(sharedExperience ? "on" : "off")}.");
            return true;
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

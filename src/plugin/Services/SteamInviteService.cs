using Il2CppInterop.Runtime;
using Steamworks;
using System;
using System.Runtime.InteropServices;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Turns "a friend invited me" into a matchmaker room code, however the invite arrives.
    ///
    /// <para>Two paths, because Steam has two. If the game was <b>closed</b>, Steam launches it with
    /// <c>+connect_lobby &lt;id&gt;</c> and the id is read off the command line. If the game was
    /// <b>already running</b>, nothing touches the command line and a
    /// <c>GameLobbyJoinRequested_t</c> callback fires instead. Both end at the same place: a Steam
    /// lobby id to resolve.</para>
    ///
    /// <para>Resolving means joining that Steam lobby, reading the room code the host published
    /// into it, and leaving again. The Steam lobby is a courier — the host stays in it, we do not,
    /// and staying would leave this service and the presence bridge arguing over who owns it.</para>
    /// </summary>
    internal class SteamInviteService(
        ISteamService steamService,
        ISteamLobbyService steamLobbyService)
    {
        /// <summary><c>GameLobbyJoinRequested_t.m_steamIDLobby</c>, a CSteamID at offset 0.</summary>
        private const int JoinRequestedLobbyIdOffset = 0;

        private bool seededFromLaunch;
        private bool registeredCallback;
        private bool joining;

        /// <summary>
        /// Held in a field for as long as it is registered. The dispatcher keeps a reference on the
        /// IL2CPP side, and letting the managed wrapper go while that is true is how a callback
        /// becomes a crash later.
        /// </summary>
        private SteamCallback joinRequestedCallback;

        /// <summary>The lobby waiting to be resolved, or 0.</summary>
        private ulong pendingLobbyId;

        /// <summary>
        /// The room code an invite resolved to, or empty. Read by the netplay menu so a player does
        /// not retype something they were handed.
        /// </summary>
        public string PendingJoinCode { get; private set; } = "";

        /// <summary>
        /// True while a Steam lobby is being joined and read. The ticker uses this to hold the
        /// presence bridge off: the bridge leaves any Steam lobby it did not create, and would drop
        /// us out of the one being read.
        /// </summary>
        public bool IsResolving => joining;

        /// <summary>Takes the pending code and forgets it, so an invite is offered once.</summary>
        public string ConsumeJoinCode()
        {
            var code = PendingJoinCode;
            PendingJoinCode = "";
            return code;
        }

        public void Poll()
        {
            if (!steamService.IsAvailable)
            {
                return;
            }

            if (!seededFromLaunch)
            {
                seededFromLaunch = true;
                if (steamLobbyService.LaunchLobbyId != 0UL)
                {
                    pendingLobbyId = steamLobbyService.LaunchLobbyId;
                    Plugin.Log.LogInfo(
                        $"[steam-invite] Launched from an invite to lobby {pendingLobbyId}.");
                }
            }

            RegisterJoinRequestedCallback();

            if (joining)
            {
                AdvanceJoin();
                return;
            }

            if (pendingLobbyId == 0UL || steamLobbyService.HasPendingCall)
            {
                return;
            }

            var lobbyId = pendingLobbyId;
            pendingLobbyId = 0UL;
            joining = true;

            Plugin.Log.LogInfo($"[steam-invite] Resolving invite to Steam lobby {lobbyId}.");
            steamLobbyService.JoinLobby(lobbyId);
        }

        /// <summary>
        /// Registers for <c>GameLobbyJoinRequested_t</c>, which is how an invite accepted while the
        /// game is already running arrives. Once, and only once Steam is up.
        /// </summary>
        private void RegisterJoinRequestedCallback()
        {
            if (registeredCallback || steamService.Readiness != SteamReadiness.Ready)
            {
                return;
            }

            // Latched before the attempt, not after. If this throws, retrying it every quarter
            // second would turn one failure into a stream of them.
            registeredCallback = true;

            try
            {
                joinRequestedCallback = new SteamCallback
                {
                    CallbackType = Il2CppType.Of<GameLobbyJoinRequested_t>(),
                    Handler = OnJoinRequested,
                };

                CallbackDispatcher.Register(joinRequestedCallback);
                Plugin.Log.LogInfo(
                    "[steam-invite] Listening for invites accepted while the game is running.");
            }
            catch (Exception ex)
            {
                joinRequestedCallback = null;
                Plugin.Log.LogWarning(
                    $"[steam-invite] Could not register for GameLobbyJoinRequested_t, so invites "
                    + $"will only work when the game is closed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// A friend's invite accepted with the game already open.
        ///
        /// <para>The id is recorded and resolved on the next <see cref="Poll"/> rather than acted on
        /// here. This runs inside the game's callback pump, and starting a Steam call from within
        /// it would re-enter the lobby service from a place it does not expect.</para>
        /// </summary>
        private void OnJoinRequested(IntPtr payload)
        {
            var lobbyId = (ulong)Marshal.ReadInt64(payload, JoinRequestedLobbyIdOffset);
            if (lobbyId == 0UL)
            {
                return;
            }

            pendingLobbyId = lobbyId;
            Plugin.Log.LogInfo($"[steam-invite] A friend invited us to lobby {lobbyId}.");
        }

        private void AdvanceJoin()
        {
            if (steamLobbyService.State == SteamLobbyState.Pending)
            {
                return;
            }

            joining = false;

            if (steamLobbyService.State != SteamLobbyState.InLobby)
            {
                // Covers a lobby that has closed, a full one, and a host on an incompatible
                // protocol version — JoinLobby applies that gate itself and leaves again, so its
                // message is the useful one.
                Plugin.Log.LogWarning(
                    $"[steam-invite] Could not join the invited lobby: {steamLobbyService.DescribeStatus()}");
                return;
            }

            // With the Steam transport carrying the session, being in this lobby IS the join, and
            // leaving would end the session we just joined - the session service watches lobby
            // membership and shuts the transport down when it goes. That is exactly what happened
            // on the first internet test: joined, connected, left, shut down, all in one tick.
            //
            // In the bridge world the opposite is true: the Steam lobby is only a signpost holding a
            // matchmaker code, and staying in it would leave a second advertisement of the session
            // lying around.
            if (Configuration.ModConfig.UseSteamTransport.Value)
            {
                PendingJoinCode = steamLobbyService.LobbyCode;
                Plugin.Log.LogInfo(
                    $"[steam-invite] Staying in lobby {steamLobbyService.LobbyId} as room "
                    + $"{PendingJoinCode}; the Steam lobby is the session.");
                return;
            }

            // Read before leaving: once we are out of the lobby its data is no longer ours to read,
            // and the transport marker is what distinguishes the two reasons a room code can be
            // missing.
            var hostTransport = steamLobbyService.GetLobbyData(SteamLobbyKeys.Transport);

            PendingJoinCode = steamLobbyService.GetLobbyData(SteamLobbyKeys.MatchmakerCode);
            steamLobbyService.LeaveLobby();

            if (string.IsNullOrEmpty(PendingJoinCode))
            {
                // Two different failures used to share one message, and it named the wrong one. A
                // host on the Steam transport publishes no matchmaker code because there is no
                // matchmaker — nothing is wrong with their build, the two installs simply disagree
                // about which transport they are on, and that is a setting the player can change.
                if (hostTransport == SteamLobbyKeys.TransportSteam)
                {
                    Plugin.Log.LogWarning(
                        "[steam-invite] This lobby runs on the Steam transport and this install does "
                        + "not. Set Network/UseSteamTransport = true in "
                        + "BepInEx/config/MegabonkTogether.cfg, with the game closed, and accept the "
                        + "invite again. There is deliberately no automatic fallback: joining on the "
                        + "other transport would silently put you in a session nobody else is in.");
                    return;
                }

                Plugin.Log.LogWarning(
                    "[steam-invite] The invited lobby published no room code. The host is probably "
                    + "running a build from before invites existed.");
                return;
            }

            Plugin.Log.LogInfo($"[steam-invite] Invite resolved to room code {PendingJoinCode}.");
        }
    }
}

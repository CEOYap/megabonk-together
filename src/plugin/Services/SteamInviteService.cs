using Il2CppInterop.Runtime;
using Steamworks;
using System;
using System.Runtime.InteropServices;

namespace OmniBonk.Services
{
    /// <summary>
    /// Turns a Steam invite into a room code. Arrives as <c>+connect_lobby &lt;id&gt;</c> when the
    /// game was closed, or a <c>GameLobbyJoinRequested_t</c> callback when it was running.
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

        /// <summary>Kept alive while registered: the IL2CPP dispatcher holds a reference.</summary>
        private SteamCallback joinRequestedCallback;

        private ulong pendingLobbyId;

        public string PendingJoinCode { get; private set; } = "";

        /// <summary>Holds the presence bridge off, which would leave the lobby being read.</summary>
        public bool IsResolving => joining;

        /// <summary>So an invite is offered once.</summary>
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

        private void RegisterJoinRequestedCallback()
        {
            if (registeredCallback || steamService.Readiness != SteamReadiness.Ready)
            {
                return;
            }

            // Latched before the attempt so a throw is not retried every poll.
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
        /// Only records the id: this runs inside the callback pump, so the Steam call waits for
        /// the next <see cref="Poll"/>.
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
                // Closed, full, or version-gated; JoinLobby's status says which.
                Plugin.Log.LogWarning(
                    $"[steam-invite] Could not join the invited lobby: {steamLobbyService.DescribeStatus()}");
                return;
            }

            // Steam transport: the lobby is the session, so stay (leaving ends it). Matchmaker:
            // the lobby only carries the code, so read it and leave.
            if (Configuration.ModConfig.UseSteamTransport.Value)
            {
                PendingJoinCode = steamLobbyService.LobbyCode;
                Plugin.Log.LogInfo(
                    $"[steam-invite] Staying in lobby {steamLobbyService.LobbyId} as room "
                    + $"{PendingJoinCode}; the Steam lobby is the session.");
                return;
            }

            // Read before leaving; it explains a missing room code.
            var hostTransport = steamLobbyService.GetLobbyData(SteamLobbyKeys.Transport);

            PendingJoinCode = steamLobbyService.GetLobbyData(SteamLobbyKeys.MatchmakerCode);
            steamLobbyService.LeaveLobby();

            if (string.IsNullOrEmpty(PendingJoinCode))
            {
                // A Steam-transport host publishes no code: a settings mismatch, not an old build.
                if (hostTransport == SteamLobbyKeys.TransportSteam)
                {
                    Plugin.Log.LogWarning(
                        "[steam-invite] This lobby runs on the Steam transport and this install does "
                        + "not. Set Network/UseSteamTransport = true in "
                        + $"BepInEx/config/{MyPluginInfo.PLUGIN_GUID}.cfg, with the game closed, and accept the "
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

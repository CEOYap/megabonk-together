namespace OmniBonk.Services
{
    /// <summary>
    /// Matchmaker transport only: a host-owned Steam lobby carrying the matchmaker code, so Steam
    /// invites work. It carries no gameplay traffic.
    /// </summary>
    internal class SteamLobbyPresenceService(
        ILobbyViewService lobbyViewService,
        ISteamLobbyService steamLobbyService,
        ISteamService steamService)
    {
        /// <summary>SetLobbyData is a round trip; only rewrite on change.</summary>
        private string publishedCode = "";

        /// <summary>One search per room code, not per tick.</summary>
        private string joinedForCode = "";

        public void Poll()
        {
            // Running outside Steam is supported.
            if (!steamService.IsAvailable)
            {
                return;
            }

            // On the Steam transport the lobby is the room; the session path owns it.
            if (Configuration.ModConfig.UseSteamTransport.Value)
            {
                return;
            }

            if (!lobbyViewService.IsInLobby)
            {
                if (steamLobbyService.State == SteamLobbyState.InLobby)
                {
                    steamLobbyService.LeaveLobby();
                }

                publishedCode = "";
                joinedForCode = "";
                return;
            }

            if (!lobbyViewService.IsLocalPlayerHost)
            {
                FollowHostLobby();
                return;
            }

            switch (steamLobbyService.State)
            {
                case SteamLobbyState.None:
                    steamLobbyService.CreateLobby(maxMembers: 6);
                    return;

                case SteamLobbyState.InLobby:
                    PublishMatchmakerCode();
                    return;

                // Failed is not retried; the invite button stays hidden.
                default:
                    return;
            }
        }

        /// <summary>
        /// Puts a client in its host's Steam lobby (for avatars and personas). Failure is harmless;
        /// tried once per room code.
        /// </summary>
        private void FollowHostLobby()
        {
            var code = lobbyViewService.LobbyCode ?? "";
            if (string.IsNullOrEmpty(code)
                || steamLobbyService.State == SteamLobbyState.InLobby
                || steamLobbyService.HasPendingCall
                || joinedForCode == code)
            {
                return;
            }

            joinedForCode = code;
            Plugin.Log.LogInfo($"[steam-lobby] Looking for the Steam lobby behind room {code}.");
            steamLobbyService.JoinByMatchmakerCode(code);
        }

        private void PublishMatchmakerCode()
        {
            var code = lobbyViewService.LobbyCode ?? "";
            if (code == publishedCode)
            {
                return;
            }

            if (steamLobbyService.SetLobbyData(SteamLobbyKeys.MatchmakerCode, code))
            {
                publishedCode = code;
                Plugin.Log.LogInfo(
                    $"[steam-lobby] Published matchmaker code into Steam lobby {steamLobbyService.LobbyId}; "
                    + "friends who accept an invite can now be pointed at it.");
            }
        }
    }
}

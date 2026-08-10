namespace MegabonkTogether.Services
{
    /// <summary>
    /// Keeps a Steam lobby alive alongside the matchmaker lobby, so there is something for Steam's
    /// invite dialog and the friends list to point at.
    ///
    /// <para><b>This is a bridge, not the migration.</b> Netplay still runs entirely over the
    /// existing transport and the WebSocket matchmaker; the Steam lobby carries no gameplay traffic
    /// and no members that matter. What it carries is the matchmaker's join code, published as
    /// lobby data — so a friend who accepts an invite can read the code out of the Steam lobby and
    /// join the real one with it. That keeps invites working without moving a single byte of
    /// gameplay onto Steam sockets, which is Phase 4's job.</para>
    ///
    /// <para>Host only. A client's Steam lobby would be a second, competing advertisement of the
    /// same session, and Steam's invite dialog would send friends to the wrong one.</para>
    /// </summary>
    internal class SteamLobbyPresenceService(
        ILobbyViewService lobbyViewService,
        ISteamLobbyService steamLobbyService,
        ISteamService steamService)
    {
        /// <summary>
        /// The matchmaker code currently published into the Steam lobby, so a rewrite only happens
        /// when it actually changes. <c>SetLobbyData</c> is a network round trip on Steam's side,
        /// and this is polled.
        /// </summary>
        private string publishedCode = "";

        public void Poll()
        {
            // No Steam, no bridge, and no complaint: an instance launched outside Steam is a
            // supported way to run and simply has no invite button.
            if (!steamService.IsAvailable)
            {
                return;
            }

            var shouldHost = lobbyViewService.IsInLobby && lobbyViewService.IsLocalPlayerHost;

            if (!shouldHost)
            {
                if (steamLobbyService.State == SteamLobbyState.InLobby)
                {
                    steamLobbyService.LeaveLobby();
                    publishedCode = "";
                }

                return;
            }

            switch (steamLobbyService.State)
            {
                case SteamLobbyState.None:
                    // Six, matching the mod's own maximum. The number is Steam's cap on the lobby,
                    // not ours on the session, but there is no reason for them to disagree.
                    steamLobbyService.CreateLobby(maxMembers: 6);
                    return;

                case SteamLobbyState.InLobby:
                    PublishMatchmakerCode();
                    return;

                // Pending resolves itself on the next poll. Failed is left alone deliberately:
                // retrying a refused CreateLobby every quarter second would turn one Steam problem
                // into a stream of them, and the invite button simply stays hidden.
                default:
                    return;
            }
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

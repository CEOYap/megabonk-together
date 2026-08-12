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

        /// <summary>
        /// The room code we last tried to follow, so a host without Steam costs one search rather
        /// than one every tick.
        /// </summary>
        private string joinedForCode = "";

        public void Poll()
        {
            // No Steam, no bridge, and no complaint: an instance launched outside Steam is a
            // supported way to run and simply has no invite button.
            if (!steamService.IsAvailable)
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

        /// <summary>
        /// Puts a client into the same Steam lobby as its host, found by the matchmaker room code
        /// they are both already in.
        ///
        /// <para>Membership is the point, not the code: everything the Steam lobby is for from here
        /// — per-member readiness, personas, avatars — needs both players to actually be in it. A
        /// client that typed a room code never saw a Steam lobby id, and one that accepted an
        /// invite deliberately left again after reading the code, so both arrive here.</para>
        ///
        /// <para><b>Failure is silent and harmless.</b> The session is carried by the matchmaker
        /// either way; without this only the extras hung off the Steam lobby are missing. Attempted
        /// once per room code so a host who is not running Steam does not produce a search every
        /// quarter second forever.</para>
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

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Turns "a friend invited me" into a matchmaker room code.
    ///
    /// <para>When a player accepts an invite while the game is closed, Steam launches it with
    /// <c>+connect_lobby &lt;id&gt;</c>. That id is a <b>Steam</b> lobby, and the session it stands
    /// for is still the WebSocket matchmaker's — so this joins the Steam lobby, reads the room code
    /// the host published into it, leaves again, and hands the code on. The Steam lobby is a
    /// courier, nothing more, which is why we do not stay in it.</para>
    ///
    /// <para><b>Only covers the cold-start case.</b> Accepting an invite while the game is already
    /// running produces a <c>GameLobbyJoinRequested_t</c> callback instead, and there is no launch
    /// argument to read. That is reachable now that an injected <c>CallResult</c> has been shown to
    /// work — <c>Callback</c>'s non-generic base has the same shape — but it is its own piece of
    /// work.</para>
    /// </summary>
    internal class SteamInviteService(
        ISteamService steamService,
        ISteamLobbyService steamLobbyService)
    {
        private enum Step
        {
            NotStarted,
            Joining,
            Done,
        }

        private Step step = Step.NotStarted;

        /// <summary>
        /// The room code an invite resolved to, or empty. Read by the netplay menu to save the
        /// player retyping something they were handed.
        /// </summary>
        public string PendingJoinCode { get; private set; } = "";

        /// <summary>
        /// True while the Steam lobby is being joined and read. The ticker uses this to hold the
        /// presence bridge off: the bridge leaves any Steam lobby it did not create, and would
        /// otherwise drop us out of the one we are reading the code from.
        /// </summary>
        public bool IsResolving => step == Step.Joining;

        /// <summary>Takes the pending code and forgets it, so an invite is offered once.</summary>
        public string ConsumeJoinCode()
        {
            var code = PendingJoinCode;
            PendingJoinCode = "";
            return code;
        }

        public void Poll()
        {
            if (step == Step.Done)
            {
                return;
            }

            switch (step)
            {
                case Step.NotStarted:
                    if (steamLobbyService.LaunchLobbyId == 0UL)
                    {
                        // Much the commonest case: nobody invited us. Settled once, and this
                        // service never costs anything again.
                        step = Step.Done;
                        return;
                    }

                    if (steamService.Readiness != SteamReadiness.Ready)
                    {
                        // Not an error yet. Steam takes a few seconds to come up, and if it never
                        // does, SteamTicker gives up and the invite quietly goes nowhere — which is
                        // the correct outcome for a game launched outside Steam.
                        return;
                    }

                    Plugin.Log.LogInfo(
                        $"[steam-invite] Resolving invite to Steam lobby {steamLobbyService.LaunchLobbyId}.");
                    steamLobbyService.JoinLobby(steamLobbyService.LaunchLobbyId);
                    step = Step.Joining;
                    return;

                case Step.Joining:
                    if (steamLobbyService.State == SteamLobbyState.Pending)
                    {
                        return;
                    }

                    step = Step.Done;

                    if (steamLobbyService.State != SteamLobbyState.InLobby)
                    {
                        // Covers a lobby that has since closed, a full one, and a host on an
                        // incompatible protocol version — JoinLobby applies that gate itself and
                        // leaves again, so its message is the useful one.
                        Plugin.Log.LogWarning(
                            $"[steam-invite] Could not join the invited lobby: "
                            + $"{steamLobbyService.DescribeStatus()}");
                        return;
                    }

                    PendingJoinCode = steamLobbyService.GetLobbyData(SteamLobbyKeys.MatchmakerCode);

                    // Left immediately. The Steam lobby only ever carried the code, the host stays
                    // in it, and remaining a member would leave the presence bridge and this
                    // service arguing over who owns the same lobby.
                    steamLobbyService.LeaveLobby();

                    if (string.IsNullOrEmpty(PendingJoinCode))
                    {
                        Plugin.Log.LogWarning(
                            "[steam-invite] The invited lobby published no room code. The host is "
                            + "probably running a build from before invites existed.");
                        return;
                    }

                    Plugin.Log.LogInfo(
                        $"[steam-invite] Invite resolved to room code {PendingJoinCode}.");
                    return;
            }
        }
    }
}

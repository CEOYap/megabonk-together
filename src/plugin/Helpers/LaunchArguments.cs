using System;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Reads what Steam put on the command line when it launched the game.
    ///
    /// <para>This is half of "join a friend's game". When a player accepts an invite or presses
    /// <b>Join Game</b> while the game is <i>not</i> running, Steam starts it with
    /// <c>+connect_lobby &lt;id&gt;</c> appended, and that is the only signal the mod gets — the
    /// warm case, where the game is already open, arrives as a
    /// <c>GameLobbyJoinRequested_t</c> callback instead, which this install cannot receive.</para>
    ///
    /// <para><b>Deliberately not a Steam call.</b> <c>SteamApps.GetLaunchCommandLine</c> would
    /// answer the same question, but it marshals a string out through a caller-sized buffer, and
    /// the process command line is already sitting in .NET with no interop involved at all. Free,
    /// and it cannot crash.</para>
    /// </summary>
    public static class LaunchArguments
    {
        private const string ConnectLobbyFlag = "+connect_lobby";

        /// <summary>
        /// The lobby id Steam asked us to join at launch, or 0.
        ///
        /// <para>Steam appends the flag and the id as two separate arguments. A malformed or
        /// missing id is treated as absent rather than as an error: the command line is attacker-
        /// adjacent input in the sense that anything can be typed on it, and there is nothing to
        /// gain from failing loudly over a value we simply will not act on.</para>
        /// </summary>
        public static ulong GetConnectLobbyId()
        {
            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-lobby] Could not read the command line: {ex.GetType().Name}: {ex.Message}");
                return 0UL;
            }

            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], ConnectLobbyFlag, StringComparison.OrdinalIgnoreCase)
                    && ulong.TryParse(args[i + 1], out var lobbyId))
                {
                    return lobbyId;
                }
            }

            return 0UL;
        }
    }
}

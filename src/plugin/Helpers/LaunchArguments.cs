using System;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// Reads Steam's <c>+connect_lobby &lt;id&gt;</c>, added when an invite launches the game.
    /// Not <c>SteamApps.GetLaunchCommandLine</c>: that marshals through a caller-sized buffer.
    /// </summary>
    public static class LaunchArguments
    {
        private const string ConnectLobbyFlag = "+connect_lobby";

        /// <summary>0 if absent or malformed.</summary>
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

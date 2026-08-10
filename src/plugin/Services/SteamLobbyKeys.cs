namespace MegabonkTogether.Services
{
    /// <summary>
    /// The keys this mod hangs off a Steam lobby.
    ///
    /// <para>Lobby data is a flat namespace shared with anything else writing to the same lobby,
    /// including the base game, so every key here is prefixed. The protocol version lives in
    /// <c>MegabonkTogether.Common.Protocol.VersionKey</c> instead, because the server and the
    /// plugin both have to agree on it.</para>
    /// </summary>
    public static class SteamLobbyKeys
    {
        /// <summary>The short join code a player can read out or paste. See <c>LobbyCode</c>.</summary>
        public const string Code = "mt_code";

        /// <summary>The host's display name, for a lobby browser that does not exist yet.</summary>
        public const string HostName = "mt_host";

        /// <summary>
        /// Steam's own key for the string that makes <b>Join Game</b> appear on a friend's list.
        /// Not prefixed: the name is Valve's, not ours, and Steam looks for exactly this.
        /// </summary>
        public const string RichPresenceConnect = "connect";

        /// <summary>Per-member readiness, replacing union tags 73 and 74.</summary>
        public const string MemberReady = "ready";
    }
}

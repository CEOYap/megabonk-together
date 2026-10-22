namespace OmniBonk.Services
{
    /// <summary>
    /// Steam lobby data keys, prefixed because the namespace is shared with the game. The protocol
    /// version key lives in <c>OmniBonk.Common.Protocol.VersionKey</c>.
    /// </summary>
    public static class SteamLobbyKeys
    {
        public const string Code = "mt_code";

        public const string HostName = "mt_host";

        /// <summary>Valve's key for friend-list Join Game; must not be prefixed.</summary>
        public const string RichPresenceConnect = "connect";

        /// <summary>Per-member readiness, replacing union tags 73 and 74.</summary>
        public const string MemberReady = "ready";

        /// <summary>
        /// Per-member game <c>ConnectionId</c> (decimal), mapping roster ids to Steam ids without a
        /// wire change. Member data is visible to every member.
        /// </summary>
        public const string MemberConnectionId = "mt_cid";

        /// <summary>"1" once the host's listen socket is open; clients wait for it instead of guessing.</summary>
        public const string ServerReady = "mt_ready";

        /// <summary>The host's Shared Experience setting, so every peer uses the same rules.</summary>
        public const string SharedExperience = "mt_sharedxp";

        /// <summary>
        /// <see cref="TransportSteam"/> for a Steam-socket session. Transport is per-install with no
        /// fallback; absent means a Steam lobby bridging a matchmaker room.
        /// </summary>
        public const string Transport = "mt_transport";

        public const string TransportSteam = "steam";

        /// <summary>Run seed; there is no <c>MatchInfo</c> on the Steam session path.</summary>
        public const string Seed = "mt_seed";

        /// <summary>Matchmaker room code, distinct from <see cref="Code"/>. Bridge-period only.</summary>
        public const string MatchmakerCode = "mt_mm_code";
    }
}

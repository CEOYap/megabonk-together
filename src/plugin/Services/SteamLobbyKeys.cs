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

        /// <summary>
        /// Per-member. The game <c>ConnectionId</c> the peer behind this Steam account is using,
        /// written as a plain decimal string.
        ///
        /// <para><b>This is the join between the two identity spaces</b>, and it exists so that
        /// nothing has to go on the wire to get one. The roster keys everyone by
        /// <c>ConnectionId</c>; Steam keys everyone by <c>CSteamID</c>; and the lobby panel needs
        /// both at once to put a face next to a name. Adding a SteamID to the <c>Player</c> record
        /// would have been the obvious route and the wrong one — MemoryPack serializes positionally,
        /// so it is a protocol break, and <c>Protocol.Version</c> is already at 2 with v1 and v2
        /// lobbies refusing each other.</para>
        ///
        /// <para>Lobby member data is visible to <b>every</b> member, which the transport's own
        /// peer table is not: a client holds a connection to the host and to nobody else, so it
        /// knows one SteamID out of six. Publishing here is what lets every peer resolve every
        /// other peer.</para>
        /// </summary>
        public const string MemberConnectionId = "mt_cid";

        /// <summary>
        /// Set to <c>"1"</c> by the host once its listen socket is open and it can accept peers.
        ///
        /// <para><b>This is what stops the two ends deciding independently when to connect.</b> Only
        /// the lobby owner may write lobby data, so there is exactly one writer and one moment; a
        /// client does not guess whether the host is listening yet, it is told. Connecting before
        /// the socket exists fails in a way that looks like a NAT problem, and both ends retrying on
        /// their own schedule is precisely the shape that broke Steam-backed readiness.</para>
        /// </summary>
        public const string ServerReady = "mt_ready";

        /// <summary>
        /// Whether the host has Shared Experience on, published so every peer applies the same
        /// rules.
        ///
        /// <para>It is the <b>host's</b> setting, not each player's, exactly as on the rendezvous
        /// path where it rides in <c>MatchInfo</c>. A client reading its own config instead would
        /// give two players different XP rules in one run and call it a preference.</para>
        /// </summary>
        public const string SharedExperience = "mt_sharedxp";

        /// <summary>
        /// Set to <see cref="TransportSteam"/> by a host carrying the session over Steam sockets.
        ///
        /// <para><b>Not a version gate — a configuration one.</b> Two peers can be on the same build
        /// and the same protocol and still be unable to play together, because
        /// <c>Network/UseSteamTransport</c> is per-install and there is deliberately no negotiation
        /// or fallback. Without this key a matchmaker-mode client joins, finds no room code, and
        /// leaves blaming the host's build — which is the opposite of what is wrong.</para>
        ///
        /// <para>Absent means the bridge world: a Steam lobby standing in for a matchmaker room.
        /// Old builds publish nothing here and are read correctly as exactly that.</para>
        /// </summary>
        public const string Transport = "mt_transport";

        /// <summary>The value <see cref="Transport"/> carries for a Steam-socket session.</summary>
        public const string TransportSteam = "steam";

        /// <summary>
        /// The run seed, published by the host so every peer generates the same world.
        ///
        /// <para>On the rendezvous path this arrives inside <c>MatchInfo</c>. With the Steam lobby
        /// as the session there is no such message, and the seed has to come from the one place both
        /// ends already agree on.</para>
        /// </summary>
        public const string Seed = "mt_seed";

        /// <summary>
        /// The WebSocket matchmaker's join code, carried inside the Steam lobby.
        ///
        /// <para>Distinct from <see cref="Code"/>, which is the Steam lobby's own. This one exists
        /// only while the migration is half done: it is how a friend who accepts a Steam invite
        /// finds their way into a session that is still being matched and carried by the old
        /// transport. It disappears at Phase 4, when the Steam lobby <i>is</i> the session.</para>
        /// </summary>
        public const string MatchmakerCode = "mt_mm_code";
    }
}

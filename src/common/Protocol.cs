namespace MegabonkTogether.Common
{
    /// <summary>
    /// The wire contract's own version, published as Steam lobby metadata so incompatible lobbies
    /// can be filtered out of the browser before anyone connects.
    ///
    /// <para>This is P1-3 in the only form that works. The same gate was attempted on the
    /// LiteNetLib path and disproved in game: <c>ConnectionRequestEvent</c> never fires after NAT
    /// introduction, because both peers <c>Connect</c> at each other and LiteNetLib reconciles the
    /// cross-connect internally, so there was no point at which a version could be refused. As
    /// lobby data it is checked before a connection is attempted at all, and it covers relayed
    /// sessions too. See <c>docs/netplay/01-critical-fixes.md</c> P1-3.</para>
    /// </summary>
    public static class Protocol
    {
        /// <summary>
        /// <para><b>Not the plugin's semantic version, and deliberately not derived from it.</b> Two
        /// releases that differ only in gameplay or UI stay wire-compatible, and making them refuse
        /// each other would be a self-inflicted split of an already small player base.</para>
        ///
        /// <para><b>Bump this only when a type under <c>Messages/</c> changes</b> — a new field on an
        /// existing message, a changed field order, a message that starts being sent where it was
        /// not before. Appending a new <c>[MemoryPackUnion]</c> tag alone does not require a bump:
        /// an older peer simply never receives that tag. What is not survivable is a changed
        /// *layout*, because MemoryPack serializes positionally and peers on different mod versions
        /// still handshake, so the mismatch corrupts a session silently instead of failing loudly.
        /// That is the whole reason this constant exists.</para>
        /// </summary>
        public const int Version = 2;

        /// <summary>
        /// The lobby metadata key carrying <see cref="Version"/>.
        ///
        /// <para>Prefixed because lobby data is a flat namespace shared with anything else that
        /// writes to the same lobby, including the base game.</para>
        /// </summary>
        public const string VersionKey = "mt_protocol";

        /// <summary>
        /// Whether a lobby advertising <paramref name="publishedVersion"/> can be joined.
        ///
        /// <para><b>A lobby publishing nothing is incompatible, not compatible-by-default.</b> That
        /// asymmetry is the entire point: absent means "created by a build that predates the gate",
        /// and treating absence as permission would mean the first release carrying the gate still
        /// joins the builds it is meant to refuse. The cost is that this release cannot join any
        /// earlier version's lobby, which needs a loud changelog entry rather than a quiet
        /// one.</para>
        /// </summary>
        public static bool IsCompatible(string? publishedVersion) =>
            int.TryParse(publishedVersion, out var version) && version == Version;
    }
}

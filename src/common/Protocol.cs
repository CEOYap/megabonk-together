namespace OmniBonk.Common
{
    /// <summary>
    /// Wire-contract version, published as Steam lobby metadata so incompatible lobbies are filtered
    /// before connecting (P1-3; a LiteNetLib connect-time gate proved impossible).
    /// </summary>
    public static class Protocol
    {
        /// <summary>
        /// Not the plugin version. Bump when any <c>Messages/</c> layout changes (field added or
        /// reordered): MemoryPack is positional and mismatches corrupt silently. Appending a union tag alone needs no bump.
        /// <para>3: OmniBonk 1.0 (tags 80 to 83; <c>ProtocolHello</c> carries this on the matchmaker path).</para>
        /// </summary>
        public const int Version = 3;

        /// <summary>Prefixed: lobby data is a flat namespace shared with the base game.</summary>
        public const string VersionKey = "mt_protocol";

        /// <summary>Absent version means incompatible (a pre-gate build), never compatible-by-default.</summary>
        public static bool IsCompatible(string? publishedVersion) =>
            int.TryParse(publishedVersion, out var version) && version == Version;
    }
}

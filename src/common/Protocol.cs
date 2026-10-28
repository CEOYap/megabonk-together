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
        /// <para>4: <c>LobbySettings.SharedExperience</c>; tag 84 <c>EnemyDamagedBatch</c> (added before 4 shipped:
        /// a build without it drops every host damage batch).</para>
        /// <para>5: tag 85 <c>ItemProjectilesSpawned</c>. Bumped so a protocol-4 build, with or without
        /// tag 84, is refused rather than silently missing either message.</para>
        /// <para>6: tag 86 <c>MineExploded</c>. A protocol-5 client would never draw a host mine's
        /// explosion (OB-58).</para>
        /// <para>7: tag 87 <c>FlaskExploded</c>. A protocol-6 client would never draw a host Poison Flask's
        /// explosion, since clients no longer explode received flasks themselves.</para>
        /// <para>8: tag 88 <c>EnemyHitEffects</c>. A protocol-7 client would draw no weapon hit sparks,
        /// since clients no longer draw their own (OB-60).</para>
        /// <para>9: tag 89 <c>ItemVisualEffects</c> (OB-62). A protocol-8 peer would never draw another
        /// machine's item visuals.</para>
        /// <para>10: <c>LobbySettings.NoEnemyCap</c>, a new field, so older peers would misread it.</para>
        /// <para>11: <c>LobbySettings.SkipChestAnimation</c> (OB-65), a new field, as 10.</para>
        /// </summary>
        public const int Version = 11;

        /// <summary>Prefixed: lobby data is a flat namespace shared with the base game.</summary>
        public const string VersionKey = "mt_protocol";

        /// <summary>Absent version means incompatible (a pre-gate build), never compatible-by-default.</summary>
        public static bool IsCompatible(string? publishedVersion) =>
            int.TryParse(publishedVersion, out var version) && version == Version;
    }
}

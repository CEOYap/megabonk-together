namespace MegabonkTogether.Common
{
    /// <summary>
    /// The wire-format version, exchanged at connect time so two peers on incompatible builds
    /// are refused rather than left to desync.
    /// </summary>
    public static class Protocol
    {
        /// <summary>
        /// Bump on ANY change to a type under <c>Messages/</c> — added field, removed field,
        /// reordered member, changed type, new <c>[MemoryPackUnion]</c> tag. MemoryPack
        /// serializes positionally, so a layout change is silent corruption rather than a
        /// loud failure.
        ///
        /// This is deliberately NOT the plugin's semantic version: two releases that differ
        /// only in gameplay or UI stay compatible and should keep the same number here.
        /// </summary>
        public const int Version = 1;

        /// <summary>
        /// Marks a connect key as ours. Peers on builds predating the version gate send
        /// <c>"yourKey"</c>, which fails to parse and is reported as an outdated peer.
        /// </summary>
        private const string KeyPrefix = "MBT";

        /// <summary>
        /// The connect key sent with a direct peer-to-peer <c>NetManager.Connect</c>.
        /// </summary>
        public static string ConnectKey => $"{KeyPrefix}|{Version}";

        /// <summary>
        /// Parses a connect key produced by <see cref="ConnectKey"/>. Returns false for
        /// anything else, including the pre-gate <c>"yourKey"</c> placeholder.
        /// </summary>
        public static bool TryParseConnectKey(string key, out int version)
        {
            version = 0;

            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            var parts = key.Split('|');
            if (parts.Length != 2 || parts[0] != KeyPrefix)
            {
                return false;
            }

            return int.TryParse(parts[1], out version);
        }
    }
}

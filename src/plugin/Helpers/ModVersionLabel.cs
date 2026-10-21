namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// How the mod names itself on screen. One place, so the main-menu button and the corner
    /// version label cannot disagree.
    /// </summary>
    internal static class ModVersionLabel
    {
        internal static string Name => MyPluginInfo.PLUGIN_NAME;

        /// <summary>
        /// "v1.0" for 1.0.0, "v1.0.1" for 1.0.1: a patch number of 0 is dropped, as players read it.
        /// </summary>
        internal static string ShortVersion =>
            System.Version.TryParse(MyPluginInfo.PLUGIN_VERSION, out var v) && v.Build <= 0
                ? $"v{v.Major}.{v.Minor}"
                : $"v{MyPluginInfo.PLUGIN_VERSION}";

        /// <summary>"OmniBonk v1.0".</summary>
        internal static string Full => $"{Name} {ShortVersion}";
    }
}

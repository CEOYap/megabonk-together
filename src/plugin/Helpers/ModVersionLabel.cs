namespace OmniBonk.Helpers
{
    /// <summary>On-screen mod name, shared by the menu button and corner label.</summary>
    internal static class ModVersionLabel
    {
        internal static string Name => MyPluginInfo.PLUGIN_NAME;

        /// <summary>"v1.0" for 1.0.0, "v1.0.1" for 1.0.1.</summary>
        internal static string ShortVersion =>
            System.Version.TryParse(MyPluginInfo.PLUGIN_VERSION, out var v) && v.Build <= 0
                ? $"v{v.Major}.{v.Minor}"
                : $"v{MyPluginInfo.PLUGIN_VERSION}";

        /// <summary>"OmniBonk v1.0".</summary>
        internal static string Full => $"{Name} {ShortVersion}";
    }
}

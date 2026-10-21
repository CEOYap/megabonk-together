using HarmonyLib;
using MegabonkTogether.Helpers;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// Adds "OmniBonk v1.0" on a line under the game's own "version 1.0.69" in the main menu's corner.
    ///
    /// <para><b>No netplay or ownership guard, on purpose.</b> This changes a menu label on this
    /// machine only and sends nothing, the same as the main-menu button in <c>MainMenu.cs</c>. The
    /// label is shown whether or not a session is running, which is the point of it.</para>
    ///
    /// <para><b>Both methods, because <c>Start</c> does not call <c>Refresh</c>.</b> Decompiled
    /// (build 21750826): <c>Start</c> subscribes <c>Refresh</c> to a static <c>SteamManager</c>
    /// action and then sets <c>t_version.text = "version " + Application.version</c> itself, inline.
    /// <c>Refresh</c> sets the same text again whenever that action fires. Each assignment replaces
    /// the whole string, so appending after each one cannot add the line twice.</para>
    /// </summary>
    [HarmonyPatch(typeof(VersionNumber))]
    internal static class VersionNumberPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(VersionNumber.Start))]
        public static void Start_Postfix(VersionNumber __instance) => AppendModVersion(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(nameof(VersionNumber.Refresh))]
        public static void Refresh_Postfix(VersionNumber __instance) => AppendModVersion(__instance);

        private static void AppendModVersion(VersionNumber versionNumber)
        {
            var label = versionNumber?.t_version;
            if (label == null)
            {
                return;
            }

            label.text = $"{label.text}\n{ModVersionLabel.Full}";
        }
    }
}

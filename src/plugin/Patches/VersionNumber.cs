using HarmonyLib;
using OmniBonk.Helpers;

namespace OmniBonk.Patches
{
    /// <summary>
    /// Adds the OmniBonk version under the game's version label. No netplay guard: local menu label only.
    /// Both methods are patched: decompiled, <c>Start</c> sets the text inline without calling <c>Refresh</c>.
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

using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using TMPro;

namespace OmniBonk.Patches
{
    /// <summary>
    /// Endless: adds rooms and boss loops survived to the end screen's Summary block (Kills / Time Alive / LVL).
    /// <para>No ownership check: display only, nothing is sent, and every peer's counts agree.</para>
    /// </summary>
    [HarmonyPatch(typeof(DeathStatsUi))]
    internal static class DeathStatsUiPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IEndlessRunService endlessRunService = Plugin.Services.GetService<IEndlessRunService>();

        [HarmonyPostfix]
        [HarmonyPatch("Start")]
        public static void Start_Postfix(DeathStatsUi __instance)
        {
            if (!synchronizationService.HasNetplaySessionInitialized() || !endlessRunService.Active)
            {
                return;
            }

            // Start (0x180532DE0, decompiled) builds the block as one string, but Ghidra names its field t_gold,
            // and its field names can be off by a slot (t_stats is 0x20, t_gold 0x28). Take whichever holds it.
            var text = PickSummaryText(__instance.t_gold) ?? PickSummaryText(__instance.t_stats);
            if (text == null)
            {
                Plugin.Log.LogWarning("[endless] End screen has no summary text; rooms survived not shown.");
                return;
            }

            var current = text.text;
            var separator = current.EndsWith("\n") ? "" : "\n";
            text.text = $"{current}{separator}Rooms: {endlessRunService.Transitions} / Loops: {endlessRunService.CompletedLoops}\n";
        }

        private static TextMeshProUGUI PickSummaryText(TextMeshProUGUI text)
            => text != null && !string.IsNullOrEmpty(text.text) && text.text.Contains('\n') ? text : null;
    }
}

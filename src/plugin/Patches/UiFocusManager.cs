using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine.EventSystems;

namespace OmniBonk.Patches
{
    /// <summary>
    /// <c>UiFocusManager.Update</c> (VA 0x180393840, decompiled) dereferences <c>EventSystem.current</c>
    /// unchecked and throws every frame when it is null, which happens in netplay.
    /// <para>Prefix-false only when the original would throw. No ownership check: local UI. Cause
    /// (likely an unfocused window) is <b>LIKELY</b>, not reproduced.</para>
    /// </summary>
    [HarmonyPatch(typeof(UiFocusManager))]
    internal static class UiFocusManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        public static bool Update_Prefix()
            => !synchronizationService.HasNetplaySessionInitialized() || EventSystem.current != null;
    }
}

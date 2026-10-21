using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine.EventSystems;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// <c>UiFocusManager.Update</c> (VA 0x180393840, decompiled) reads <c>EventSystem.current</c>
    /// and dereferences it with no null check, so it throws on every frame in which no event system
    /// is current. In netplay that happens: on 2026-10-21 the host logged 8,564
    /// <c>NullReferenceException</c>s, every one at <c>UiFocusManager.Update</c> (Player.log), from
    /// the lobby onward, and a bot's Player.log the same, each one an exception thrown and caught
    /// per frame. With no event system there is nothing for the method to do, so it is skipped.
    ///
    /// <para>Prefix-false on purpose, and only when the original would throw. No ownership check:
    /// it is this machine's own UI. Why the event system goes missing is not known; a game kept
    /// running while unfocused is the likely case. <b>LIKELY</b>, not reproduced.</para>
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

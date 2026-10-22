using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Interactables
{
    [HarmonyPatch(typeof(InteractableDesertGrave))]
    internal static class InteractableDesertGravePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetRequiredService<ISynchronizationService>();

        /// <summary>Deactivates the desert grave on clients.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(InteractableDesertGrave.Interact))]
        public static void Interact_Postfix(InteractableDesertGrave __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }
            var isHost = synchronizationService.IsServerMode() ?? false;
            if (!isHost)
            {
                __instance.gameObject.SetActive(false);
            }
        }
    }
}

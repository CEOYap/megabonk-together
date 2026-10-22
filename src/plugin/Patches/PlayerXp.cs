using HarmonyLib;
using Inventory__Items__Pickups.Xp_and_Levels;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(PlayerXp))]
    internal static class PlayerXpPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Records the total before the add, so the postfix can send the gain.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerXp.AddXp))]
        public static void AddXp_Prefix(PlayerXp __instance, out int __state)
        {
            __state = __instance.xp;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerXp.AddXp))]
        public static void AddXp_Postfix(PlayerXp __instance, int amount, int __state)
        {
            // "Initialized", not "started": XP gained during a transition barrier must be shared too,
            // as gains cannot re-converge. Same hole as OB-8.
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            if (!synchronizationService.IsSharedExperienceEnabled())
            {
                return;
            }

            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return;
            }

            // Shared XP is replicated as the GAIN, not the total. See OnReceivedAddXp.
            var gained = __instance.xp - __state;
            if (gained <= 0)
            {
                return;
            }

            synchronizationService.PlayerXpAddXp(__instance.xp, gained, __instance.leftOverXp);
        }

    }
}

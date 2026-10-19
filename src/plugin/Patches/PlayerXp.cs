using HarmonyLib;
using Inventory__Items__Pickups.Xp_and_Levels;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(PlayerXp))]
    internal static class PlayerXpPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Synchronize XP changes in shared experience mode.
        /// </summary>
        /// <summary>Records the total before the game adds to it, so the postfix can send the gain.</summary>
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
            // "Initialized", not "started": XP gained during a stage transition's ready barrier
            // (when HasNetplaySessionStarted is false) was never shared, so each peer kept only its
            // own and shared XP, which only ever sends gains, could not re-converge. 2026-10-19:
            // levels 39 / 40 before the final arena's barrier, 42 / 47 after it. Same hole as OB-8.
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

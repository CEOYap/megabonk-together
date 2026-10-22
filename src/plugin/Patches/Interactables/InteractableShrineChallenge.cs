using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Interactables
{
    [HarmonyPatch(typeof(InteractableShrineChallenge))]
    internal static class InteractableShrineChallengePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// OB-15: challenges are host-authoritative; clients skip <c>EnemyDied</c>. Decompiled
        /// (VA <c>0x1804CDFE0</c>): it walks an enemy set a client never populates, because our cosmetic
        /// <c>done = true</c> opens its gate. Same pattern as <see cref="Items.ItemGhostPatches"/> (OB-2).
        /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(InteractableShrineChallenge.EnemyDied))]
        public static bool EnemyDied_Prefix()
        {
            // Initialized, not Started: level transitions leave Started while enemies are pooled.
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            // Host owns the challenge; clients receive its results.
            return synchronizationService.IsServerMode() ?? false;
        }
    }
}

using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(PlayerMovement))]
    internal static class PlayerMovementPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Reset world size when teleporting player back to bounds (used when leaving graveyard boss room)
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerMovement.TeleportPlayerBackToBounds))]
        public static void TeleportPlayerBackToBounds_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            // OB-36. Every peer must quantize positions over the same range, so a change here is
            // worth one line: it is rare (a fall off the map, or leaving the graveyard boss room).
            var before = Plugin.Instance.GetWorldSize().x;
            Plugin.Instance.SetWorldSize(Plugin.Instance.OriginalWorldSize);
            Plugin.Log.LogInfo($"[world] OB-36: teleported back to bounds; position range {before} -> {Plugin.Instance.OriginalWorldSize.x}.");
        }

        /// <summary>
        /// Prevent player movement when dead
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerMovement.MovementTick))]
        public static bool MovementTick_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (GameManager.Instance?.player?.IsDead() is true)
            {
                PlayerMovement.Instance.rb.velocity = Vector3.zero;
                PlayerMovement.Instance.currentMoveSpeed = 0f;

                return false;
            }

            return true;
        }
    }
}

using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(PlayerMovement))]
    internal static class PlayerMovementPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Resets world size on teleport back to bounds (e.g. leaving the graveyard boss room).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerMovement.TeleportPlayerBackToBounds))]
        public static void TeleportPlayerBackToBounds_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            // OB-36: every peer must quantize over the same range; logged since it is rare.
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

using Assets.Scripts.Actors.Player;
using Assets.Scripts.Camera;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Unity
{
    [HarmonyPatch(typeof(MinimapCamera))]
    internal static class MinimapCameraPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// OB-17: prefix-false while there is no local player. Decompiled (VA <c>0x1804B2D60</c>), it
        /// reads <c>MyPlayer.Instance.transform</c> per arrow with no null check. No state is skipped.
        /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MinimapCamera.UpdateArrows))]
        public static bool UpdateArrows_Prefix(MinimapCamera __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            // Unity's operator: a destroyed player reads as null too.
            if (MyPlayer.Instance == null)
            {
                return false;
            }

            PruneDeadArrows(__instance);
            return true;
        }

        private static readonly System.Collections.Generic.List<UnityEngine.Transform> deadArrowKeys = [];

        /// <summary>Drops entries for destroyed avatars (rebuilt at stage transitions), which UpdateArrows would dereference.</summary>
        private static void PruneDeadArrows(MinimapCamera camera)
        {
            var arrows = camera != null ? camera.arrowDict : null;
            if (arrows == null || arrows.Count == 0)
            {
                return;
            }

            deadArrowKeys.Clear();
            foreach (var entry in arrows)
            {
                if (entry.Key == null || entry.Value == null)
                {
                    deadArrowKeys.Add(entry.Key);
                }
            }

            if (deadArrowKeys.Count == 0)
            {
                return;
            }

            foreach (var key in deadArrowKeys)
            {
                if (arrows.TryGetValue(key, out var arrow) && arrow != null)
                {
                    UnityEngine.Object.Destroy(arrow.gameObject);
                }

                arrows.Remove(key);
            }

            Plugin.Log.LogInfo($"[minimap] dropped {deadArrowKeys.Count} arrow(s) whose target was destroyed.");
            deadArrowKeys.Clear();
        }

        /// <summary>
        /// Prefix-false for a duplicate target. <c>AddArrow</c> (VA <c>0x1804B16F0</c>) does an unchecked
        /// Add; a duplicate threw before <c>TrySpotBossSpawner</c> set <c>bossSpotted</c>, every frame.
        /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MinimapCamera.AddArrow))]
        public static bool AddArrow_Prefix(MinimapCamera __instance, UnityEngine.Transform target)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var arrows = __instance.arrowDict;
            return target == null || arrows == null || !arrows.ContainsKey(target);
        }
    }
}

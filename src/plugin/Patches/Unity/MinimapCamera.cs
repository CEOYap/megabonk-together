using Assets.Scripts.Actors.Player;
using Assets.Scripts.Camera;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Unity
{
    [HarmonyPatch(typeof(MinimapCamera))]
    internal static class MinimapCameraPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// OB-17. Skips one frame of minimap arrow updates while there is no local player.
        ///
        /// <para><b>What throws.</b> Decompiled (<c>MinimapCamera$$UpdateArrows</c>, VA
        /// <c>0x1804B2D60</c>), the method enumerates <c>arrowDict.Values</c> and, <i>per entry</i>,
        /// reads <c>MyPlayer.Instance.transform.position</c> to place the arrow. It null-checks
        /// nothing: a null <c>MyPlayer.Instance</c> raises immediately, and because this runs from
        /// <c>MinimapCamera.Update</c> it raises again every frame until a player exists. The host's
        /// log carried <b>62</b> of these on 2026-08-17.</para>
        ///
        /// <para><b>Why a null guard is the right shape here, unlike OB-15.</b> That one was
        /// suppressed rather than null-guarded because a guard would have hidden a client-side
        /// challenge that could never complete — there was a lie to preserve. Here there is none:
        /// positioning an arrow relative to a player that does not exist is not a meaningful
        /// operation, the arrows are re-placed on the next frame that has a player, and no state is
        /// skipped. The window is a teardown/respawn boundary, which is exactly when a minimap has
        /// nothing to point at.</para>
        ///
        /// <para><b>Whether the mod widens that window is not established.</b> The plugin populates
        /// <c>arrowDict</c> for remote players — <c>PlayerManagerService</c> still carries a note
        /// about "the ordering mistake StartGame already made once with the minimap arrow" — so a
        /// session may hold arrows across a transition where singleplayer would have had none, which
        /// would make the loop body run where vanilla's was empty. That is a hypothesis; this guard
        /// is correct either way and does not depend on it.</para>
        ///
        /// <para><b>UNVERIFIED.</b> Not run in-game. Confirms as the host's <c>Player.log</c> losing
        /// its <c>MinimapCamera.UpdateArrows</c> lines, with the minimap arrows still appearing.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MinimapCamera.UpdateArrows))]
        public static bool UpdateArrows_Prefix(MinimapCamera __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            // Unity's operator, so a destroyed player reads as null too — which is precisely the
            // teardown half of the window this exists for.
            if (MyPlayer.Instance == null)
            {
                return false;
            }

            PruneDeadArrows(__instance);
            return true;
        }

        private static readonly System.Collections.Generic.List<UnityEngine.Transform> deadArrowKeys = [];

        /// <summary>
        /// Drops arrows whose target or arrow object has been destroyed, before UpdateArrows reads
        /// them.
        ///
        /// <para><b>What threw.</b> 2026-10-19, host, three players: 72 pairs of
        /// <c>NullReferenceException</c> in <c>Component.get_transform</c> from
        /// <c>MinimapCamera.UpdateArrows</c>, right after a teammate's avatar was rebuilt at a stage
        /// transition (<c>Spawned NetPlayer for …</c>). <c>arrowDict</c> is keyed by each tracked
        /// player's <c>Transform</c>; the old avatar was destroyed but its entry stayed, and
        /// UpdateArrows dereferenced it every frame. Unity's <c>==</c> reads a destroyed object as
        /// null, so a dead entry is found with a plain null check. The dictionary holds a handful of
        /// entries, so this is cheap per frame.</para>
        /// </summary>
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
        /// Skips adding a minimap arrow for a target that already has one.
        ///
        /// <para><b>What threw.</b> <c>AddArrow</c> (VA <c>0x1804B16F0</c>, decompiled) ends in an
        /// unchecked <c>arrowDict.Add(target, arrow)</c>. <c>TrySpotBossSpawner</c> (VA
        /// <c>0x1804B2AE0</c>) calls it once the boss spawner is inside the minimap's range and sets
        /// <c>bossSpotted</c> only <i>after</i> it returns. In netplay the boss spawner's arrow can
        /// already be in the dictionary when that happens: the boss-defeated path adds it when the
        /// boss dies, and on 2026-10-18 the client first walked within range of the spawner after the
        /// host had already killed the boss. So <c>Add</c> threw, <c>bossSpotted</c> was never set,
        /// and it threw again every frame: <b>948</b> <c>ArgumentException: An item with the same
        /// key has already been added. Key: BossSpawner(Clone)</c> on the client, until the stage
        /// ended.</para>
        ///
        /// <para>Returning early lets <c>TrySpotBossSpawner</c> set <c>bossSpotted</c>, so it stops
        /// asking. The arrow that is already there stays, which is what the player should see.</para>
        ///
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

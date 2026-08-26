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
        public static bool UpdateArrows_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            // Unity's operator, so a destroyed player reads as null too — which is precisely the
            // teardown half of the window this exists for.
            return MyPlayer.Instance != null;
        }
    }
}

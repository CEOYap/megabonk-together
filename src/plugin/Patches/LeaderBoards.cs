using Assets.Scripts.Steam;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// Blocks leaderboard score uploads during netplay. This is the mod's only remaining Steam
    /// suppression, and it is the one that matters: uploading a netplay score risks a ban.
    ///
    /// Verified against buildid 21750826 (see docs/reverse-engineering/01-investigation-targets.md):
    ///
    /// - A prefix here is SUFFICIENT. Leaderboards.UploadScore is the only external caller of
    ///   SteamLeaderboardsManagerNew.QueueLeaderboardUpload, which merely enqueues onto a
    ///   Queue&lt;T&gt; drained later by CheckUploadQueue. Blocking entry means nothing is ever
    ///   queued.
    /// - A prefix is also NECESSARY rather than optional. UploadScore itself calls
    ///   LeaderboardsNew_Sus.CheckMods and probes ~10 directory paths, but that check gates only
    ///   ONE of its three QueueLeaderboardUpload calls — the other two run unconditionally. So
    ///   the game's own mod detection does not protect a modded run; only this patch does.
    ///
    /// Achievements and stats are deliberately NOT suppressed — see NETPLAY_CHANGES.md.
    /// </summary>
    [HarmonyPatch(typeof(Leaderboards))]
    internal static class LeaderBoardsPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Prevents the original leaderboard score upload when running a netplay session.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Leaderboards.UploadScore))]
        public static bool UploadScore_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            Plugin.Log.LogInfo("Blocking leaderboard upload");
            return false;
        }
    }
}

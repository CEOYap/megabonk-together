using Assets.Scripts.Steam;
using HarmonyLib;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// Blocks leaderboard score uploads **unconditionally** while this mod is loaded — netplay
    /// session or not. Uploading a score from a modded game risks a ban, and the risk does not
    /// depend on whether a multiplayer session happens to be running.
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
    /// **Why unconditional rather than netplay-only.** That same CheckMods routine detects the
    /// mod by probing directories on disk, so a solo run with this mod installed is just as
    /// detectable as a netplay one. Gating on HasNetplaySessionInitialized() would leave every
    /// singleplayer score exposed. The player's route to a legitimate leaderboard entry is to
    /// remove or disable the mod.
    ///
    /// Achievements and stats are deliberately NOT suppressed — see NETPLAY_CHANGES.md.
    /// </summary>
    [HarmonyPatch(typeof(Leaderboards))]
    internal static class LeaderBoardsPatches
    {
        /// <summary>
        /// Prevents the original leaderboard score upload whenever the mod is loaded.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Leaderboards.UploadScore))]
        public static bool UploadScore_Prefix()
        {
            Plugin.Log.LogInfo("Blocking leaderboard upload (MegabonkTogether is installed)");
            return false;
        }
    }
}

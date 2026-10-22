using Assets.Scripts.Steam;
using HarmonyLib;

namespace OmniBonk.Patches
{
    /// <summary>
    /// Blocks leaderboard uploads <b>unconditionally</b> (no netplay guard on purpose): a modded
    /// score risks a ban, and the game detects the mod on disk even in singleplayer.
    /// <para>Verified on buildid 21750826: <c>UploadScore</c> is the only caller of
    /// <c>QueueLeaderboardUpload</c>, and the game's own <c>CheckMods</c> gates only one of its
    /// three upload calls. Achievements and stats are deliberately not suppressed.</para>
    /// </summary>
    [HarmonyPatch(typeof(Leaderboards))]
    internal static class LeaderBoardsPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Leaderboards.UploadScore))]
        public static bool UploadScore_Prefix()
        {
            Plugin.Log.LogInfo("Blocking leaderboard upload (OmniBonk is installed)");
            return false;
        }
    }
}

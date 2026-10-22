using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Utility;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Enemies
{
    /// <summary>
    /// OB-13: prefix-false on enemy damage while paused by a reward window, at the three entry
    /// points every source converges on. Shared experience only: otherwise a paused host would
    /// freeze damage for a teammate still fighting. Stateless, so nothing can strand enemies invulnerable.
    /// <para>Not <c>Enemy.MakeInvulnerable</c> (VA <c>0x1804AED80</c>): it tints enemies, clears all
    /// debuffs, and its flag is only read on the kill-trigger path.</para>
    /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
    /// </summary>
    [HarmonyPatch(typeof(Enemy))]
    internal static class EnemyDamageWhilePausedPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.DamageFromPlayerWeapon))]
        public static bool DamageFromPlayerWeapon_Prefix() => AllowDamage();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.DamageFromPlayerOther))]
        public static bool DamageFromPlayerOther_Prefix() => AllowDamage();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.DamageExternal))]
        public static bool DamageExternal_Prefix() => AllowDamage();

        private static bool AllowDamage()
        {
            if (IsSuppressedForPause())
            {
                RecordSuppressed();
                return false;
            }

            return true;
        }

        /// <summary>Shared with <c>Enemy.Damage_Prefix</c> so the gates cannot disagree.</summary>
        internal static bool IsSuppressedForPause()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return false;
            }

            if (!synchronizationService.IsSharedExperienceEnabled())
            {
                return false;
            }

            return MyTime.paused;
        }

        private static int suppressedSinceReport = 0;
        private static float lastReportTime = -999f;
        private const float ReportIntervalSeconds = 2f;

        /// <summary>Throttled proof the gate fires. Unscaled time: the game clock is stopped.</summary>
        internal static void RecordSuppressed()
        {
            suppressedSinceReport++;

            var now = UnityEngine.Time.unscaledTime;
            if (now - lastReportTime < ReportIntervalSeconds)
            {
                return;
            }

            Plugin.Log.LogInfo(
                $"OB-13: suppressed {suppressedSinceReport} enemy hit(s) while paused " +
                $"(host: {synchronizationService.IsServerMode() ?? false}).");
            suppressedSinceReport = 0;
            lastReportTime = now;
        }
    }
}

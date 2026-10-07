using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Utility;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Enemies
{
    /// <summary>
    /// OB-13. Stops enemies taking damage while this peer's game is paused by a reward window.
    ///
    /// <para><b>The bug.</b> Under shared experience every peer is paused by the reward pop-up, so
    /// nobody is legitimately still fighting — yet bosses and enemies kept losing health. The game's
    /// own projectile paths are not the culprit: <c>ProjectileBase.Update</c>,
    /// <c>ProjectileBase.FixedUpdate</c> and <c>Rocket.FixedUpdate</c> all gate on
    /// <c>MyTime.paused</c>, and a full session of <c>LogEncounterPause</c> showed the pause holding
    /// for every window — 213 samples open-and-paused, zero where it broke. The damage therefore
    /// arrives from something that never consults the flag, and <c>MyTime.paused</c> is read in
    /// exactly one other file in this plugin.</para>
    ///
    /// <para><b>Why here rather than per-source.</b> These three methods are where every damage
    /// source converges, so one gate covers weapons, projectiles, auras, damage-over-time and the
    /// mod's own replicated damage alike — including whatever is actually doing it, which has not
    /// been identified. Chasing the source would fix one path and leave the rest.</para>
    ///
    /// <para><b>Why NOT <c>Enemy.MakeInvulnerable</c></b>, which is the obvious-looking lever and was
    /// the first suggestion. Decompiled (VA <c>0x1804AED80</c>) it does three things, and two of them
    /// are wrong here: it calls <c>EnemyRenderer.SetInvulnerable</c>, which visibly tints every
    /// enemy on screen, and <c>QueueClearAllDebuffs</c>, which would <b>strip poison, burn and every
    /// other stack off the whole field each time a reward window opened</b>. That is a gameplay
    /// change wearing a pause's clothing. It also would not have worked: the flag it sets is read by
    /// <c>Enemy.CanTakeDamage</c>, and an xref gives that method exactly one caller —
    /// <c>KillEnemyTrigger.OnTriggerEnter</c> — so it is not on the weapon-damage path at all.</para>
    ///
    /// <para><b>Gated on shared experience on purpose.</b> With it on, every peer pauses together, so
    /// suppressing damage while paused is symmetric and total. With it off only the acting peer
    /// pauses — and since damage is host-authoritative, a host paused in its own chest window would
    /// otherwise freeze damage for a teammate who is still fighting. That would trade this bug for a
    /// worse one.</para>
    ///
    /// <para>Covers every window that pauses, which is what was asked for: level-up, chest,
    /// microwave, moai and shady guy all reach <c>MyTime.Pause()</c>, so all of them are handled by
    /// reading the flag rather than by enumerating them.</para>
    ///
    /// <para><b>Stateless, which is the safety argument.</b> Nothing is set on an enemy and nothing
    /// has to be cleared, so a missed unpause, a dropped peer or an exception cannot strand the
    /// field invulnerable. The gate stops applying the moment <c>MyTime.paused</c> goes false.</para>
    ///
    /// <para><b>UNVERIFIED.</b> Not run in-game. Confirms as a boss's health bar holding still for
    /// the whole reward window on both peers. <b>Watch for the opposite failure too</b>: damage that
    /// never resumes after the window closes would mean the flag is being read as stuck-true.</para>
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

        /// <summary>False suppresses the damage. See the class remarks for why each check is here.</summary>
        private static bool AllowDamage()
        {
            if (IsSuppressedForPause())
            {
                RecordSuppressed();
                return false;
            }

            return true;
        }

        /// <summary>
        /// The OB-13 rule, shared with <c>Enemy.Damage_Prefix</c> so the gate at the funnel and the
        /// gates at the three entry points can never disagree.
        /// </summary>
        internal static bool IsSuppressedForPause()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return false;
            }

            // Only the mode where every peer is paused together. See the class remarks.
            if (!synchronizationService.IsSharedExperienceEnabled())
            {
                return false;
            }

            return MyTime.paused;
        }

        private static int suppressedSinceReport = 0;
        private static float lastReportTime = -999f;
        private const float ReportIntervalSeconds = 2f;

        /// <summary>
        /// Counts suppressed hits and reports them at Info, throttled.
        ///
        /// <para>The first version of this fix logged nothing, so "still not working" could not be
        /// told apart from "never fired" -- the same trap OB-11 fell into. A window that opens with
        /// no line here means the gate is not being reached at all; a window with lines and health
        /// still dropping means the damage has found a fifth way in. Unscaled time, because the game
        /// clock is stopped while this is firing.</para>
        /// </summary>
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

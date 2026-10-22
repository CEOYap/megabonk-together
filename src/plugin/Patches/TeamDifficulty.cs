using Assets.Scripts.Game.Combat;
using Assets.Scripts.Game.Spawning.New;
using Assets.Scripts.Game.Spawning.New.Summoners;
using Assets.Scripts.Inventory.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    /// <summary>
    /// OB-26. Enemy HP, speed, knockback resistance and spawns follow the team's mean Difficulty.
    /// <para>Scoped to nine decompiled readers only: <c>EnemyStats.GetDamage</c> must keep the victim's
    /// own value. Substituted in <c>PlayerStatsNew.GetStat</c>, which <c>PlayerStats.GetStat</c>
    /// forwards to (VA 0x18042B6A0), to avoid a second trampoline on the hottest stat path.</para>
    /// <para>Depth counter, since scopes nest; finalizers, so a throw cannot leave a scope open.</para>
    /// </summary>
    internal static class TeamDifficulty
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        internal static int Depth;

        /// <summary>Opens a scope in netplay only. Returns whether it did, for the paired <see cref="Exit"/>.</summary>
        internal static bool Enter()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return false;
            }

            Depth++;
            return true;
        }

        internal static void Exit(bool entered)
        {
            if (entered && Depth > 0)
            {
                Depth--;
            }
        }
    }

    [HarmonyPatch(typeof(PlayerStatsNew))]
    internal static class PlayerStatsNewTeamDifficultyPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerStatsSyncService playerStatsSyncService = Plugin.Services.GetService<IPlayerStatsSyncService>();

        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerStatsNew.GetStat))]
        public static void GetStat_Postfix(EStat stat, ref float __result)
        {
            // Cheapest check first: this runs on every stat read in the game.
            if (TeamDifficulty.Depth == 0 || stat != EStat.Difficulty)
            {
                return;
            }

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            // The nine readers only run on the host; guarded anyway so a client never substitutes.
            if (!(Plugin.Instance?.NetworkHandler?.IsHost ?? false))
            {
                return;
            }

            __result = playerStatsSyncService.GetTeamDifficulty(__result);
        }
    }

    [HarmonyPatch(typeof(EnemyStats))]
    internal static class EnemyStatsTeamDifficultyPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyStats.GetHp))]
        public static void GetHp_Prefix(out bool __state) => __state = TeamDifficulty.Enter();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(EnemyStats.GetHp))]
        public static void GetHp_Finalizer(bool __state) => TeamDifficulty.Exit(__state);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyStats.GetSpeed))]
        public static void GetSpeed_Prefix(out bool __state) => __state = TeamDifficulty.Enter();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(EnemyStats.GetSpeed))]
        public static void GetSpeed_Finalizer(bool __state) => TeamDifficulty.Exit(__state);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyStats.GetKnockbackResistance))]
        public static void GetKnockbackResistance_Prefix(out bool __state) => __state = TeamDifficulty.Enter();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(EnemyStats.GetKnockbackResistance))]
        public static void GetKnockbackResistance_Finalizer(bool __state) => TeamDifficulty.Exit(__state);
    }

    [HarmonyPatch(typeof(CombatScaling))]
    internal static class CombatScalingTeamDifficultyPatches
    {
        // GetHpMultiplierAddition must NOT be hooked: with three `out float` params the trampoline
        // throws on every call and the game gets 0 (enemies lose HP scaling). Its caller, EnemyStats.GetHp, is scoped below.

        [HarmonyPrefix]
        [HarmonyPatch(nameof(CombatScaling.GetStageHpMultiplier))]
        public static void GetStageHpMultiplier_Prefix(out bool __state) => __state = TeamDifficulty.Enter();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(CombatScaling.GetStageHpMultiplier))]
        public static void GetStageHpMultiplier_Finalizer(bool __state) => TeamDifficulty.Exit(__state);
    }

    [HarmonyPatch(typeof(BaseSummoner))]
    internal static class BaseSummonerTeamDifficultyPatches
    {
        // Protected in the game, so named by string.
        [HarmonyPrefix]
        [HarmonyPatch("GetMultiplier")]
        public static void GetMultiplier_Prefix(out bool __state) => __state = TeamDifficulty.Enter();

        [HarmonyFinalizer]
        [HarmonyPatch("GetMultiplier")]
        public static void GetMultiplier_Finalizer(bool __state) => TeamDifficulty.Exit(__state);
    }

    // The overrides, not BaseSummoner's: these are the bodies that read Difficulty, and Harmony
    // patches a method body, not a virtual slot.
    [HarmonyPatch(typeof(StageSummoner))]
    internal static class StageSummonerTeamDifficultyPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(StageSummoner.GetNumTargetEnemies))]
        public static void GetNumTargetEnemies_Prefix(out bool __state) => __state = TeamDifficulty.Enter();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(StageSummoner.GetNumTargetEnemies))]
        public static void GetNumTargetEnemies_Finalizer(bool __state) => TeamDifficulty.Exit(__state);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(StageSummoner.GetSummonInterval))]
        public static void GetSummonInterval_Prefix(out bool __state) => __state = TeamDifficulty.Enter();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(StageSummoner.GetSummonInterval))]
        public static void GetSummonInterval_Finalizer(bool __state) => TeamDifficulty.Exit(__state);
    }

    [HarmonyPatch(typeof(SwarmSummoner))]
    internal static class SwarmSummonerTeamDifficultyPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(SwarmSummoner.GetNumTargetEnemies))]
        public static void GetNumTargetEnemies_Prefix(out bool __state) => __state = TeamDifficulty.Enter();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(SwarmSummoner.GetNumTargetEnemies))]
        public static void GetNumTargetEnemies_Finalizer(bool __state) => TeamDifficulty.Exit(__state);
    }
}

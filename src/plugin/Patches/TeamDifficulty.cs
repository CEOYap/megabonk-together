using Assets.Scripts.Game.Combat;
using Assets.Scripts.Game.Spawning.New;
using Assets.Scripts.Game.Spawning.New.Summoners;
using Assets.Scripts.Inventory.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// OB-26. The shared world — enemy HP, speed, knockback resistance, spawn count and spawn rate —
    /// follows the team's mean Difficulty instead of the host's alone.
    ///
    /// <para><b>Why a scope, not a global substitution.</b> <c>EnemyStats.GetDamage</c> also reads
    /// Difficulty, on the victim's own machine, and must keep that player's own value: a cursed
    /// player still pays personally. So the team value is handed only to the nine readers below,
    /// each found by decompiling all 128 callers of <c>PlayerStats.GetStat</c>
    /// (<c>docs/netplay/16-session-handover.md</c>).</para>
    ///
    /// <para><b>Why the substitution sits on <c>PlayerStatsNew.GetStat</c>.</b> Decompiled at VA
    /// <c>0x18042B6A0</c>, <c>PlayerStats.GetStat</c> is a direct forward to the local player's
    /// <c>PlayerStatsNew.GetStat</c>, which the mod already detours. Patching the static one would
    /// put a second native-to-managed trampoline on the game's hottest stat path.</para>
    ///
    /// <para>A depth counter rather than a flag: <c>EnemyStats.GetHp</c> calls
    /// <c>CombatScaling.GetHpMultiplierAddition</c>, and the inner finalizer must not close the
    /// outer scope. Finalizers, not postfixes, so a throw cannot leave the scope open.</para>
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
        [HarmonyPrefix]
        [HarmonyPatch(nameof(CombatScaling.GetHpMultiplierAddition))]
        public static void GetHpMultiplierAddition_Prefix(out bool __state) => __state = TeamDifficulty.Enter();

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(CombatScaling.GetHpMultiplierAddition))]
        public static void GetHpMultiplierAddition_Finalizer(bool __state) => TeamDifficulty.Exit(__state);

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

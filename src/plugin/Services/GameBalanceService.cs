using OmniBonk.Configuration;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Managers;
using System;
using System.Linq;

namespace OmniBonk.Services
{
    public interface IGameBalanceService
    {
        public float GetCreditsTimerMultiplier();
        public float GetEnemyHpMultiplier(EEnemyFlag enemyFlag);

        /// <summary>Endless (Phase 4): enemy damage to players. 1 outside Endless.</summary>
        public float GetEnemyDamageMultiplier();
        public float GetFreeChestSpawnRateMultiplier();
        public int GetPickupXpValue();
        public void Initialize();
        public int GetMaxEnemiesSpawnable();
        public float GetBossLampRequiredCharge();
    }

    public enum DifficultyLevel
    {
        None,
        Duo,
        Trio,
        Quad,
        Five,
        Six
    }

    internal class GameBalanceService(IPlayerManagerService playerManagerService, IEndlessRunService endlessRunService) : IGameBalanceService
    {
        private const float hpScalingPerAdditionalPlayer = Tunables.HP_SCALING_PER_ADDITIONAL_PLAYER;
        // FIX P1-4: non-allocating count; read by six multiplier getters.
        private int PlayersCount => playerManagerService.GetAlivePlayerCount();
        private const float baseBossLampInitialChargeTimeSeconds = 3.0f;

        // Keyed on instance id. `.Pointer` does not compile on Unity types here: the csproj's
        // unity-libs UnityEngine.Object does not derive from Il2CppObjectBase.
        private static int cachedStageInstanceId;
        private static int cachedStageIndex;

        /// <summary>
        /// PERF 04 item 3: stage index memoised on the current stage's identity, so it cannot go
        /// stale. Read per enemy spawn and per pickup.
        /// <para><b>UNVERIFIED:</b> that stage assets outlive every caller (GetInstanceID on a destroyed object).</para>
        /// <para><b>UNVERIFIED alternative:</b> <c>MapController.GetStageIndex()</c> exists but its
        /// indexing basis is unknown; check the dump before switching.</para>
        /// </summary>
        private static int StageIndex
        {
            get
            {
                var currentStage = MapController.currentStage;
                var stageInstanceId = currentStage?.GetInstanceID() ?? 0;

                if (stageInstanceId == cachedStageInstanceId)
                {
                    return cachedStageIndex;
                }

                cachedStageInstanceId = stageInstanceId;
                cachedStageIndex = MapController.runConfig?.mapData.stages.IndexOf(currentStage) ?? 0;

                return cachedStageIndex;
            }
        }


        /// <summary>Stage for the balance tables. In Endless, run progress (max of index and transitions), capped at the last stage.</summary>
        private int BalanceStageIndex => endlessRunService.Active
            ? System.Math.Min(2, System.Math.Max(StageIndex, endlessRunService.Transitions))
            : StageIndex;

        /// <summary>Endless (Phase 4): compounding per boss loop cleared. 1 otherwise.</summary>
        private float LoopFactor(float perLoop) => endlessRunService.Active && endlessRunService.CompletedLoops > 0
            ? (float)System.Math.Pow(perLoop, endlessRunService.CompletedLoops)
            : 1f;

        public float GetEnemyDamageMultiplier() => LoopFactor(Tunables.ENDLESS_LOOP_DAMAGE_MULTIPLIER);

        public int GetMaxEnemiesSpawnable()
        {
            if (GameManager.Instance.IsFinalSwarm())
            {
                return 400; // Keep the original cap during Final Swarm
            }

            return GetDifficultyLevelByPlayers() switch
            {
                DifficultyLevel.Quad or DifficultyLevel.Five or DifficultyLevel.Six => 600,
                _ => 500,
            };
        }

        public float GetCreditsTimerMultiplier()
        {
            float baseMultiplier = GetDifficultyLevelByPlayers() switch
            {
                DifficultyLevel.Duo => 1.01f,
                DifficultyLevel.Trio => 1.02f,
                DifficultyLevel.Quad => 1.03f,
                DifficultyLevel.Five => 1.04f,
                DifficultyLevel.Six => 1.05f,
                _ => 1.0f,
            };

            float stageMultiplier = BalanceStageIndex switch
            {
                0 => 1.0f,
                1 => 1.05f,
                2 => 1.07f,
                _ => 1.0f
            };

            return baseMultiplier * stageMultiplier;
        }

        public float GetEnemyHpMultiplier(EEnemyFlag enemyFlag)
        {
            float baseMultiplier = enemyFlag switch
            {
                EEnemyFlag.Boss => 1.2f,
                EEnemyFlag.Elite => 1.05f,
                EEnemyFlag.FinalBoss => 1.25f,
                EEnemyFlag.SummonerMiniboss => 1.1f,
                EEnemyFlag.AnyBoss => 1.1f,
                EEnemyFlag.Challenge => 1.15f,
                _ => 1f
            };

            float playerScaling = 1f + (PlayersCount - 1) * hpScalingPerAdditionalPlayer;

            float stageMultiplier = BalanceStageIndex switch
            {
                0 => 1.0f,
                1 => 1.1f,
                2 => 1.2f,
                _ => 1.0f
            };

            return baseMultiplier * playerScaling * stageMultiplier * LoopFactor(Tunables.ENDLESS_LOOP_HP_MULTIPLIER);
        }

        public float GetFreeChestSpawnRateMultiplier()
        {
            float baseMultiplier = GetDifficultyLevelByPlayers() switch
            {
                DifficultyLevel.Duo => 1.5f,
                DifficultyLevel.Trio => 1.8f,
                DifficultyLevel.Quad => 2.0f,
                DifficultyLevel.Five => 2.2f,
                DifficultyLevel.Six => 2.4f,
                _ => 1f,
            };

            float stageMultiplier = BalanceStageIndex switch
            {
                0 => 1.0f,
                1 => 1.1f,
                2 => 1.15f,
                _ => 1.0f
            };

            return baseMultiplier * stageMultiplier;
        }

        public int GetPickupXpValue()
        {
            return PlayersCount switch
            {
                1 => 1,
                >= 2 and <= 4 => 2,
                5 => 3,
                _ => 1
            };
        }

        public void Initialize()
        {
            var creditsMultiplier = GetCreditsTimerMultiplier();
            var enemyHpMultiplier = GetEnemyHpMultiplier(EEnemyFlag.None);
            var chestSpawnMultiplier = GetFreeChestSpawnRateMultiplier();
            var xpValue = GetPickupXpValue();

            Plugin.Log.LogInfo($"[GameBalance] Initialized for {PlayersCount} players, Stage {StageIndex + 1} (balanced as {BalanceStageIndex + 1}), Difficulty: {GetDifficultyLevelByPlayers()}" + (endlessRunService.Active ? $", Endless loop {endlessRunService.CompletedLoops}, enemy damage x{GetEnemyDamageMultiplier():F2}" : ""));
            Plugin.Log.LogInfo($"[GameBalance] Credits Timer Multiplier (Disabled): {creditsMultiplier:F2}x");
            Plugin.Log.LogInfo($"[GameBalance] Basic Enemy HP Base Multiplier: {enemyHpMultiplier:F2}x");
            Plugin.Log.LogInfo($"[GameBalance] Free Chest Spawn Rate Multiplier: {chestSpawnMultiplier:F2}x");

            if (Plugin.Instance.Mode.EnabledSharedExperience.HasValue && !Plugin.Instance.Mode.EnabledSharedExperience.Value)
            {
                Plugin.Log.LogInfo($"[GameBalance] XP Value: {xpValue}");
            }
        }

        private DifficultyLevel GetDifficultyLevelByPlayers()
        {
            return PlayersCount switch
            {
                2 => DifficultyLevel.Duo,
                3 => DifficultyLevel.Trio,
                4 => DifficultyLevel.Quad,
                5 => DifficultyLevel.Five,
                6 => DifficultyLevel.Six,
                _ => DifficultyLevel.None
            };
        }

        public float GetBossLampRequiredCharge()
        {
            return GetDifficultyLevelByPlayers() switch
            {
                DifficultyLevel.Duo => baseBossLampInitialChargeTimeSeconds * 2f,
                DifficultyLevel.Trio => baseBossLampInitialChargeTimeSeconds * 2.5f,
                DifficultyLevel.Quad => baseBossLampInitialChargeTimeSeconds * 3f,
                DifficultyLevel.Five => baseBossLampInitialChargeTimeSeconds * 3.5f,
                DifficultyLevel.Six => baseBossLampInitialChargeTimeSeconds * 4f,
                _ => baseBossLampInitialChargeTimeSeconds,
            };
        }

    }
}

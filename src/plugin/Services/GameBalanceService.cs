using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Managers;
using System;
using System.Linq;

namespace MegabonkTogether.Services
{
    public interface IGameBalanceService
    {
        public float GetCreditsTimerMultiplier();
        public float GetEnemyHpMultiplier(EEnemyFlag enemyFlag);
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

    internal class GameBalanceService(IPlayerManagerService playerManagerService) : IGameBalanceService
    {
        private const float hpScalingPerAdditionalPlayer = 0.1f;
        // FIX P1-4: was GetAllPlayersAlive().Count(), which materialised a Player[] plus two LINQ
        // iterators just to read its length. Six multiplier getters read this property.
        private int PlayersCount => playerManagerService.GetAlivePlayerCount();
        private const float baseBossLampInitialChargeTimeSeconds = 3.0f;

        // Keyed on the native pointer. StageData is a ScriptableObject, so `==` would work here —
        // but Unity's overload is a native call per comparison, while Pointer is a managed field
        // read, and it does not depend on Il2CppInterop handing back the same wrapper each time.
        private static IntPtr cachedStagePointer = IntPtr.Zero;
        private static int cachedStageIndex;

        /// <summary>
        /// PERF 04 item 3: was an <c>IndexOf</c> over the stage list on **every access** — an
        /// interop equality call per element — and the multiplier getters that read it run per
        /// enemy spawn and per pickup.
        ///
        /// <para>Memoised against the current stage's own identity rather than invalidated by an
        /// event: the answer is a pure function of that reference, so checking it is both cheaper
        /// than the lookup and cannot go stale. `04-performance-and-gc.md` suggested caching on
        /// stage-change events, which needs a subscription and would silently serve a wrong
        /// difficulty if one were ever missed — this cannot.</para>
        ///
        /// <para>The one theoretical hole is a freed stage object whose pointer is reused by a
        /// different one. Stages are asset references that live for the run, so that does not
        /// happen here; it is worth knowing before copying this pattern onto runtime objects.</para>
        ///
        /// <para><b>UNVERIFIED alternative:</b> the game has its own
        /// <c>MapController.GetStageIndex()</c> (static, returns Int32 — confirmed present in the
        /// interop metadata, body unknown). If it indexes the same list this does, it replaces all
        /// of this. It is not used yet because a different indexing basis — global progression
        /// rather than position within the map's stage list — would silently shift every
        /// difficulty multiplier. Check it against the dump before switching.</para>
        /// </summary>
        private static int StageIndex
        {
            get
            {
                var currentStage = MapController.currentStage;
                var stagePointer = currentStage?.Pointer ?? IntPtr.Zero;

                if (stagePointer == cachedStagePointer)
                {
                    return cachedStageIndex;
                }

                cachedStagePointer = stagePointer;
                cachedStageIndex = MapController.runConfig?.mapData.stages.IndexOf(currentStage) ?? 0;

                return cachedStageIndex;
            }
        }


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

            float stageMultiplier = StageIndex switch
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

            float stageMultiplier = StageIndex switch
            {
                0 => 1.0f,
                1 => 1.1f,
                2 => 1.2f,
                _ => 1.0f
            };

            return baseMultiplier * playerScaling * stageMultiplier;
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

            float stageMultiplier = StageIndex switch
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

            Plugin.Log.LogInfo($"[GameBalance] Initialized for {PlayersCount} players, Stage {StageIndex + 1}, Difficulty: {GetDifficultyLevelByPlayers()}");
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

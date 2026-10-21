using MegabonkTogether.Configuration;
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
        // FIX P1-4: was GetAllPlayersAlive().Count(), which materialised a Player[] plus two LINQ
        // iterators just to read its length. Six multiplier getters read this property.
        private int PlayersCount => playerManagerService.GetAlivePlayerCount();
        private const float baseBossLampInitialChargeTimeSeconds = 3.0f;

        // Keyed on the instance id. StageData is a ScriptableObject, so `==` would work here — but
        // Unity's overload is a native call per comparison *and* it does not depend on
        // Il2CppInterop handing back the same wrapper each time, which the id does not either.
        //
        // This was written as a `Pointer` read, which does not compile: the csproj references
        // UnityEngine.CoreModule from `unity-libs`, where UnityEngine.Object extends System.Object.
        // Only the `interop` copy extends Il2CppSystem.Object -> Il2CppObjectBase, which is where
        // `Pointer` is declared. So no Unity-derived type has `Pointer` at compile time in this
        // project, however it may look at runtime — every other `.Pointer` in the repo is on a
        // variable already typed as Il2CppObjectBase. GetInstanceID() is one native call, against
        // the IndexOf-over-the-stage-list it replaces; the memoisation is still the win.
        private static int cachedStageInstanceId;
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
        /// <para>The one theoretical hole is a destroyed stage object: <c>GetInstanceID()</c> on a
        /// destroyed <c>UnityEngine.Object</c> is not the safe read a pointer field would have
        /// been. Stages are asset references that live for the run, so that does not happen here;
        /// it is worth knowing before copying this pattern onto runtime objects. <b>UNVERIFIED:</b>
        /// that stage assets outlive every caller has not been observed in-game, only reasoned
        /// from their being map assets.</para>
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


        /// <summary>
        /// The stage the balance tables use. Outside Endless, the stage's place in its map. In
        /// Endless the room's place in its map says nothing about how far the run is, and a
        /// stage-1 room late in a run used to get stage-1 balance: there it is the run's progress
        /// instead, the stage index or the number of stage changes so far, whichever is greater,
        /// held at the last stage's tables.
        /// </summary>
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

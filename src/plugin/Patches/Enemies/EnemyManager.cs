using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Managers;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches.Enemies
{
    /// <summary>
    /// No Enemy Cap, the third limit. <c>SpawnEnemy</c> (0x18041A3F0) takes each enemy from
    /// <c>PoolManager.enemyPool</c> (0xC0) and gives up on null. That pool's create function,
    /// <c>PoolManager.&lt;Start&gt;b__107_3</c> (0x18040E8D0, built in <c>Start</c> 0x18040AF40), returns null
    /// once the pool holds <c>EnemyManager.maxNumEnemiesPooled</c> (static 0x10) enemies. That stopped a
    /// hands-off test at 700 alive with both other caps lifted. Raised while the lobby setting is on, on
    /// every peer, since clients draw the host's enemies from their own pool; the game's value comes back
    /// on <see cref="Restore"/>.
    /// </summary>
    internal static class EnemyPoolSize
    {
        private static int? original;

        internal static void Apply(bool noEnemyCap)
        {
            if (!noEnemyCap)
            {
                Restore();
                return;
            }

            original ??= EnemyManager.maxNumEnemiesPooled;
            EnemyManager.maxNumEnemiesPooled = EnemyManagerPatches.NO_ENEMY_CAP_LIMIT;
        }

        internal static void Restore()
        {
            if (original.HasValue)
            {
                EnemyManager.maxNumEnemiesPooled = original.Value;
                original = null;
            }
        }
    }

    [HarmonyPatch(typeof(EnemyManager))]
    internal class EnemyManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IEnemyManagerService enemyManagerService = Plugin.Services.GetService<IEnemyManagerService>();
        private static readonly IGameBalanceService gameBalanceService = Plugin.Services.GetService<IGameBalanceService>();
        private static readonly ILobbySettingsService lobbySettingsService = Plugin.Services.GetService<ILobbySettingsService>();

        /// <summary>
        /// What <see cref="GetNumMaxEnemies"/> reports under No Enemy Cap: past any crowd a run reaches, and
        /// small enough that the game's float math on it (the Final Swarm target) stays exact.
        /// </summary>
        internal const int NO_ENEMY_CAP_LIMIT = 100000;

        /// <summary>Host-only spawning, with our own max-enemies limit unless the lobby set No Enemy Cap.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyManager.SpawnEnemy), [typeof(EnemyData), typeof(int), typeof(bool), typeof(EEnemyFlag), typeof(bool)])]
        public static bool SpawnEnemy_Prefix(bool forceSpawn, EnemyManager __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
            }

            if (!forceSpawn && !lobbySettingsService.NoEnemyCap && __instance.numEnemies >= gameBalanceService.GetMaxEnemiesSpawnable())
            {
                return false;
            }

            return true;
        }

        //[HarmonyPrefix]
        //[HarmonyPatch(nameof(EnemyManager.SpawnEnemy), [typeof(EnemyData), typeof(Vector3), typeof(int), typeof(bool), typeof(EEnemyFlag), typeof(bool)])]
        //public static void SpawnEnemy_Prefix(ref EnemyData enemyData, Vector3 pos, int waveNumber, bool forceSpawn, EEnemyFlag flag, bool canBeElite)
        //{

        //    if (enemyData.enemyName == EEnemy.MinibossPig)
        //    {
        //        enemyData = DataManager.Instance.GetEnemyData(EEnemy.MinibossGolem);
        //    }
        //}


        [HarmonyPostfix]
        [HarmonyPatch(nameof(EnemyManager.SpawnEnemy), [typeof(EnemyData), typeof(Vector3), typeof(int), typeof(bool), typeof(EEnemyFlag), typeof(bool), typeof(float)])]
        public static void SpawnEnemy_Postfix(EnemyData enemyData, Vector3 pos, int waveNumber, bool forceSpawn, EEnemyFlag flag, bool canBeElite, float extraSizeMultiplier, Enemy __result)
        {
            if (__result == null)
            {
                return;
            }

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                synchronizationService.OnSpawnedEnemy(__result, enemyData.enemyName, pos, waveNumber, forceSpawn, flag, canBeElite, extraSizeMultiplier);
            }
        }

        /// <summary>
        /// Host owns boss spawning; clients receive SpawnedEnemy instead.
        /// <para>Expected, harmless client error: <c>InteractableCoffin.Interact</c> (VA <c>0x1804C7360</c>)
        /// logs "Failed to spawn miniboss from coffin" for each suppressed spawn.</para>
        /// <para>OB-11: <c>pos</c> is by ref so the host can re-centre a miniboss before it exists.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyManager.SpawnBoss))]
        public static bool SpawnBoss_Prefix(ref Vector3 pos, EEnemyFlag enemyFlag)
        {
            // OB-8: "initialized", not "started": bosses spawned during the ready barrier must not spawn on clients.
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
            }

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                Plugin.Log.LogInfo($"[boss] host SpawnBoss before the session started (flag {(int)enemyFlag}); its spawn will not be replicated.");
                return true;
            }

            // OB-16: arena bosses stay where the map put them (moving the final boss broke its fight).
            const EEnemyFlag arenaBossFlags = EEnemyFlag.FinalBoss | EEnemyFlag.StageBoss;
            if ((enemyFlag & arenaBossFlags) != 0)
            {
                Plugin.Log.LogInfo(
                    $"OB-11: leaving an arena boss where the map put it (flag {(int)enemyFlag}).");
                return true;
            }

            // Bosses spawned from an object stay at the object; OB-11 only concerns the timeline miniboss.
            if (ObjectBossSpawns.Source != null || Plugin.Instance.CurrentReviver.HasValue)
            {
                Plugin.Log.LogInfo(
                    $"OB-11: leaving a boss spawned by {ObjectBossSpawns.Source ?? "a revive coffin"} where it came from (flag {(int)enemyFlag}).");
                return true;
            }

            // Logged unconditionally: proves the patch fired.
            Plugin.Log.LogInfo($"OB-11: re-centring a boss spawn (flag {(int)enemyFlag}) from {pos}.");

            // Try at the call site: a MissingMethodException fires at JIT time of the callee.
            try
            {
                pos = enemyManagerService.RecentreBossSpawnOnRandomPlayer(pos);
            }
            catch (System.Exception ex)
            {
                // Fairness only. A miniboss at the vanilla position beats no miniboss at all.
                Plugin.Log.LogWarning($"OB-11: could not re-centre a miniboss spawn, using the game's position: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// The game's own limit. Decompiled (0x180419D60): 550, or in the Final Swarm 400 then 300 after
        /// 120 s. Its callers are <c>SpawnEnemy</c> (0x18041A3F0, refuses a non-forced spawn at the limit),
        /// <c>CanSpawnEnemy</c> and <c>HasMaxEnemies</c> (the summoners stop), and
        /// <c>SwarmSummoner.GetNumTargetEnemies</c> (0x180489420), whose Final Swarm target is
        /// min(time x 2.5, limit) x multiplier + 50. So the mod's own cap
        /// (<see cref="SpawnEnemy_Prefix"/>) is the one that bites, at 1000 here; No Enemy Cap lifts both.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(EnemyManager.GetNumMaxEnemies))]
        public static void GetNumMaxEnemies(ref int __result, EnemyManager __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            __result = lobbySettingsService.NoEnemyCap ? NO_ENEMY_CAP_LIMIT : 1000;

            //Plugin.Log.LogInfo($"GetNumMaxEnemies: {__instance.numEnemies} / {__result} ");
        }
    }
    /// <summary>
    /// Callers of <c>EnemyManager.SpawnBoss</c> (VA 0x18041A200, xrefs) that spawn at an object; while one
    /// runs, OB-11 re-centring is skipped. No guards needed: they only set a scope the host reads.
    /// <b>UNVERIFIED, not run in-game.</b>
    /// </summary>
    [HarmonyPatch]
    internal static class ObjectBossSpawns
    {
        internal static string Source { get; private set; }

        private static void Enter(string source, out string previous)
        {
            previous = Source;
            Source = source;
        }

        private static void Exit(string previous) => Source = previous;

        [HarmonyPrefix, HarmonyPatch(typeof(InteractableCoffin), nameof(InteractableCoffin.Interact))]
        public static void Coffin_Prefix(out string __state) => Enter("a coffin", out __state);

        [HarmonyFinalizer, HarmonyPatch(typeof(InteractableCoffin), nameof(InteractableCoffin.Interact))]
        public static void Coffin_Finalizer(string __state) => Exit(__state);

        [HarmonyPrefix, HarmonyPatch(typeof(InteractableDesertGrave), "SpawnEnemy")]
        public static void DesertGrave_Prefix(out string __state) => Enter("a desert grave", out __state);

        [HarmonyFinalizer, HarmonyPatch(typeof(InteractableDesertGrave), "SpawnEnemy")]
        public static void DesertGrave_Finalizer(string __state) => Exit(__state);

        [HarmonyPrefix, HarmonyPatch(typeof(InteractableCharacterFight), "SpawnEnemy")]
        public static void CharacterFight_Prefix(out string __state) => Enter("a challenge character", out __state);

        [HarmonyFinalizer, HarmonyPatch(typeof(InteractableCharacterFight), "SpawnEnemy")]
        public static void CharacterFight_Finalizer(string __state) => Exit(__state);

        [HarmonyPrefix, HarmonyPatch(typeof(InteractableSkeletonKingStatue), "SpawnEnemy")]
        public static void SkeletonKingStatue_Prefix(out string __state) => Enter("the Skeleton King statue", out __state);

        [HarmonyFinalizer, HarmonyPatch(typeof(InteractableSkeletonKingStatue), "SpawnEnemy")]
        public static void SkeletonKingStatue_Finalizer(string __state) => Exit(__state);

        [HarmonyPrefix, HarmonyPatch(typeof(GraveyardBossRoom), "CheckSpawnBoss")]
        public static void GraveyardBossRoom_Prefix(out string __state) => Enter("the Ghost King's arena", out __state);

        [HarmonyFinalizer, HarmonyPatch(typeof(GraveyardBossRoom), "CheckSpawnBoss")]
        public static void GraveyardBossRoom_Finalizer(string __state) => Exit(__state);
    }
}

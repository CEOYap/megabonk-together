using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Managers;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace MegabonkTogether.Patches.Enemies
{
    [HarmonyPatch(typeof(EnemyManager))]
    internal class EnemyManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IEnemyManagerService enemyManagerService = Plugin.Services.GetService<IEnemyManagerService>();
        private static readonly IGameBalanceService gameBalanceService = Plugin.Services.GetService<IGameBalanceService>();

        /// <summary>
        /// Only the server is allowed to spawn
        /// Also manually enforce max enemies limit
        /// </summary>
        /// <returns></returns>
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

            if (!forceSpawn && __instance.numEnemies >= gameBalanceService.GetMaxEnemiesSpawnable())
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


        /// <summary>
        /// Synchronize enemy spawn
        /// </summary>
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
        ///
        /// <para><b>Expect the game to log an error on the client because of this, and do not chase
        /// it.</b> Suppressing the original makes <c>SpawnBoss</c> return null, and callers that
        /// null-check the result log their own complaint. The known one is Graveyard's coffin:
        /// <c>InteractableCoffin.Interact</c> (VA <c>0x1804C7360</c>) spawns its minibosses in a loop
        /// and calls <c>Debug.LogError("Failed to spawn miniboss from coffin")</c> for each null —
        /// so a Graveyard session logs roughly <c>numCoffins</c> (4) x 2-3 of them, client only.</para>
        ///
        /// <para><b>It is cosmetic and the mechanic still works.</b> The coffin gates its crypt key
        /// on its private <c>minibossEnemies</c> set draining to empty, and
        /// <c>SynchronizationService.OnReceivedSpawnedEnemy</c> adds each replicated GhostGrave1-4 to
        /// that set precisely because this prefix stopped the game from doing it. Both peers then
        /// reach <c>keyPickup.SetActive(true)</c> on their own with no message involved — confirmed
        /// in a two-player Graveyard run where both players received the key.</para>
        ///
        /// <para><b>The absence of crypt-key network traffic proves nothing either way</b>, which
        /// cost a session's analysis: the key's *appearance* is local to each peer, and
        /// <c>InteractableUsed.IsCryptKey</c> only replicates the *pickup*, on a path with no log
        /// statement at all.</para>
        ///
        /// <para><b><c>pos</c> is taken by reference to fix OB-11.</b> The game's spawner anchors
        /// every position on the local player, so on the host every miniboss lands near the host —
        /// see <c>docs/netplay/08-observed-bugs.md#ob-11</c>. This is the one point on the path where
        /// the position can be corrected before the enemy exists, which is why the correction lives
        /// here and not in <see cref="SpawnEnemy_Postfix"/>. It runs after the host check above, and
        /// must: two peers each picking their own player would spawn two minibosses.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyManager.SpawnBoss))]
        public static bool SpawnBoss_Prefix(ref Vector3 pos, EEnemyFlag enemyFlag)
        {
            // OB-8: "initialized", not "started", for the client. Game code that spawns a boss during
            // the ready barrier (the final arena's cinematic does) must not spawn a local copy on a
            // client; only the host's boss is real.
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

            // OB-16 / OB-11 scope correction. Arena bosses must spawn where the map put them.
            // FinalFightController.SpawnBoss calls this with flag 0x24 -- FinalBoss (32) plus
            // StageBoss (4) -- after resolving bossSpawnPoint and raycasting it to ground, and it
            // assigns the *return value* to FinalFightController.boss. Re-centring that position on
            // a random player moved the final boss out of its arena, and a SpawnEnemy that then
            // fails returns null, which leaves `boss` null and throws later at
            // FinalFightController.OnPylonCharged when the last pylon charges -- observed once on
            // the host, 2026-08-17.
            //
            // OB-11 is about minibosses roaming to the host; it was never about arena bosses, and
            // the original patch was simply too broad.
            const EEnemyFlag arenaBossFlags = EEnemyFlag.FinalBoss | EEnemyFlag.StageBoss;
            if ((enemyFlag & arenaBossFlags) != 0)
            {
                Plugin.Log.LogInfo(
                    $"OB-11: leaving an arena boss where the map put it (flag {(int)enemyFlag}).");
                return true;
            }

            // A boss that comes out of an object spawns at the object: a Graveyard coffin's ghosts
            // (whose fight raises a barrier round the coffin), a Desert grave's, a bush or bandit
            // challenge, the Skeleton King statue, the Ghost King's arena, and our revive coffin's
            // ghost. The game gives those the object's position on purpose; only the timeline's
            // miniboss (SummonerController.EventMiniboss) is placed round the local player, which is
            // all OB-11 was about. 2026-10-21: coffin and grave bosses appeared next to a random
            // player.
            if (ObjectBossSpawns.Source != null || Plugin.Instance.CurrentReviver.HasValue)
            {
                Plugin.Log.LogInfo(
                    $"OB-11: leaving a boss spawned by {ObjectBossSpawns.Source ?? "a revive coffin"} where it came from (flag {(int)enemyFlag}).");
                return true;
            }

            // Logged unconditionally, at Info, because this line is the only thing that
            // distinguishes "the re-centring ran and chose the host" from "the patch never fired".
            // Two runs were called fixed on the strength of a counter that only counted exceptions,
            // and neither log contained a single OB-11 line of any kind. SpawnBoss is called a
            // handful of times per run, so there is no volume argument against it.
            Plugin.Log.LogInfo($"OB-11: re-centring a boss spawn (flag {(int)enemyFlag}) from {pos}.");

            // The try is here rather than inside the service on purpose: a MissingMethodException
            // from an interop signature that did not resolve is raised when the *containing* method
            // is JIT-compiled, so a try within RecentreBossSpawnOnRandomPlayer could never run. It
            // has to sit at the call site, one level up. Plugin.Load and CountInteractableAsUsed
            // both carry this same note.
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
        /// When Max limit is reached, enemies will start being less aggressive.
        /// This is an attempt to prevent that
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(EnemyManager.GetNumMaxEnemies))]
        public static void GetNumMaxEnemies(ref int __result, EnemyManager __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            __result = 1000; //Bait the game to keep monster aggressive; TODO: is it really working ?

            //Plugin.Log.LogInfo($"GetNumMaxEnemies: {__instance.numEnemies} / {__result} ");
        }
    }
    /// <summary>
    /// The callers of <c>EnemyManager.SpawnBoss</c> (VA 0x18041A200, xrefs) that spawn a boss out of
    /// an object, at that object. While one runs, <see cref="Source"/> names it, and the OB-11
    /// re-centring in <c>SpawnBoss_Prefix</c> leaves its position alone. The other callers are the
    /// timeline miniboss (re-centred, by design), the stage and final bosses (exempt by flag) and
    /// the dungeon's overtime spawns in <c>GameManager</c> (unchanged).
    ///
    /// <para>Void prefixes and finalizers: they only name the scope, on whichever peer runs the
    /// method; only the host's <c>SpawnBoss</c> reads it. <b>UNVERIFIED, not run in-game.</b></para>
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

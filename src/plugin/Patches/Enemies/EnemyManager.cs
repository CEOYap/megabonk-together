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
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
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
                return true;
            }

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
}

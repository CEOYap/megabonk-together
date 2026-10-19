using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(EffectManager))]
    internal static class EffectManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Only the server is allowed to spawn chests
        /// </summary>
        /// <returns></returns>

        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.CheckChestSpawn))]
        public static bool CheckChestSpawn_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode();
            if (isServer.HasValue && isServer.Value)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        ///  Only the server is allowed to spawn pickup orbs unless allowed
        /// </summary>
        /// <returns></returns>

        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.SpawnPickupOrb))]
        public static bool SpawnPickupOrb_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;

            if (!isServer)
            {
                return Plugin.CAN_SPAWN_PICKUPS;
            }

            return true;
        }

        /// <summary>
        /// Synchronize pickup orb spawns from the server
        [HarmonyPostfix]
        [HarmonyPatch(nameof(EffectManager.SpawnPickupOrb))]
        public static void SpawnPickupOrb_Postfix(EPickup ePickup, ref UnityEngine.Vector3 position)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode();
            if (isServer == false)
            {
                return;
            }

            synchronizationService.OnPickupOrbSpawned(ePickup, position);
        }


        /// <summary>
        /// Only the server is allowed to make enemies explode unless allowed
        /// </summary>
        /// <param name="enemy"></param>
        /// <returns></returns>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.ExploderEnemy))]
        public static bool ExploderEnemy_Prefix(Enemy enemy)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }
            var isServer = synchronizationService.IsServerMode() ?? false;

            if (isServer)
            {
                return true;
            }

            if (Plugin.CAN_ENEMY_EXPLODE)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Synchronize enemy exploder from the server
        /// </summary>
        /// <param name="enemy"></param>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(EffectManager.ExploderEnemy))]
        public static void ExploderEnemy_Postfix(Enemy enemy)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return;
            }

            synchronizationService.OnEnemyExploder(enemy);
        }

        /// <summary>
        /// Prevent client spawning quests/desertGraves (sent by the server)
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.OnMapGenerationComplete))]
        public static bool OnMapGenerationComplete_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }
            var isServer = synchronizationService.IsServerMode() ?? false;

            if (!isServer)
            {
                Plugin.Log.LogInfo("Skipping Quest/Graves spawning on client");

                Plugin.Instance.AddPrefab(EffectManager.Instance.bananaQuest);
                Plugin.Instance.AddPrefab(EffectManager.Instance.banditQuest);
                Plugin.Instance.AddPrefab(EffectManager.Instance.boomboxQuest);
                Plugin.Instance.AddPrefab(EffectManager.Instance.bushQuest);
                Plugin.Instance.AddPrefab(EffectManager.Instance.katanaQuest);
                Plugin.Instance.AddPrefab(EffectManager.Instance.luckTomeQuest);
                Plugin.Instance.AddPrefab(EffectManager.Instance.shotgunQuest);
                Plugin.Instance.AddPrefab(EffectManager.Instance.presentQuest);

                foreach (var desertGraves in EffectManager.Instance.desertGraves)
                {
                    Plugin.Instance.AddPrefab(desertGraves);
                }

                return false;
            }

            return true;
        }

        /// <summary>
        /// Prevent client spawning tornadoes (sent by the server)
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.SpawnTornadoes))]
        public static bool SpawnTornadoes_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (Plugin.Instance.CAN_SPAWN_TORNADOES)
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;

            return isServer;
        }

        /// <summary>
        /// Synchronize tornado spawns from the server
        /// </summary>
        /// <param name="amount"></param>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(EffectManager.SpawnTornadoes))]
        public static void SpawnTornadoes_Postfix(int amount)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }
            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return;
            }
            synchronizationService.OnTornadoesSpawned(amount);
        }

        /// <summary>
        /// OB-32. Only the host spawns tumbleweeds, and only once the session has started, so
        /// every one it spawns is replicated by <c>InteractableTumbleWeed.OnEnable_Postfix</c>.
        ///
        /// <para><b>Why "initialized", not "started".</b> Decompiled: <c>MapEventsManager.FixedUpdate</c>
        /// (VA <c>0x180499EC0</c>) calls <c>MapEventsDesert.Tick</c> (VA <c>0x180473EB0</c>) on every
        /// Desert stage but the final arena, and <c>MyTime.Pause</c> does not stop MonoBehaviour
        /// <c>FixedUpdate</c> - it only sets <c>paused</c> and switches physics to script mode.
        /// <c>Tick</c> spawns when <c>lastSpawnedTumbleweedTime + 2.2 &lt;= MyTime.time</c>; a fresh
        /// <c>MapEventsDesert</c> starts at 0 and <c>MyTime.time</c> is never reset per stage, so the
        /// first tick of every Desert stage spawns one at once - inside the ready barrier. With the
        /// old guard both peers ran it: the host's went out unreplicated (its despawn later logs
        /// "TumbleWeed not found ... OnTumbleWeedDespawned", once per Desert stage in the 2026-10-19
        /// host log), and the client kept one of its own that froze when the session started and
        /// could never despawn.</para>
        ///
        /// <para>So: a client in a session never spawns one (it instantiates the host's from
        /// <c>TumbleWeedSpawned</c> directly); the host drops a spawn before Started. Dropping is
        /// enough - <c>Tick</c> has already moved <c>lastSpawnedTumbleweedTime</c> on, and the next
        /// one comes 2.2 s after the barrier releases.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.SpawnTumbleWeeds))]
        public static bool SpawnTumbleWeeds_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            // Prefix-false on purpose: the host spawns them and the client receives TumbleWeedSpawned.
            var isHost = synchronizationService.IsServerMode() ?? false;
            if (!isHost)
            {
                return false;
            }

            // Prefix-false on purpose: before Started the OnEnable postfix does not replicate (see above).
            return synchronizationService.HasNetplaySessionStarted();
        }
    }
}

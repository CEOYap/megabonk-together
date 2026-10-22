using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(EffectManager))]
    internal static class EffectManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Host only spawns chests.</summary>
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

        /// <summary>Host only spawns pickup orbs, unless a client is applying the host's.</summary>
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


        /// <summary>Host only explodes enemies, unless a client is applying the host's.</summary>
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

        /// <summary>Clients do not spawn quests/desert graves; the host sends them.</summary>
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

        /// <summary>Clients do not spawn tornadoes; the host sends them.</summary>
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
        /// OB-32. Only the host spawns tumbleweeds, and only once Started, so each is replicated.
        /// <para>Gated on Initialized: decompiled <c>MapEventsDesert.Tick</c> (VA 0x180473EB0) runs
        /// even while paused and spawns one at once on each Desert stage, inside the ready barrier.
        /// Dropping it is safe; the next comes 2.2 s later.</para>
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

            // Prefix-false on purpose: before Started the OnEnable postfix does not replicate.
            return synchronizationService.HasNetplaySessionStarted();
        }
    }
}

using Assets.Scripts.Managers;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using System;
using UnityEngine;

namespace OmniBonk.Patches.Unity
{
    [HarmonyPatch(typeof(UnityEngine.Object))]
    internal static class UnityObjectPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        /// <summary>Host only spawns chests, unless a client is applying the host's.</summary>

        [HarmonyPrefix]
        [HarmonyPatch(nameof(UnityEngine.Object.Instantiate), [typeof(UnityEngine.Object), typeof(UnityEngine.Vector3), typeof(UnityEngine.Quaternion)])]
        public static bool Instantiate_Postfix(UnityEngine.Object original, Vector3 position, Quaternion rotation)
        {
            if ((EffectManager.Instance != null && EffectManager.Instance.openChestNormal == original) || original.name.Contains("OpenChest"))
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

                return Plugin.CAN_SPAWN_CHESTS;
            }

            return true;
        }


        /// <summary>Boss spawner instantiation on ProceduralMesh maps.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(UnityEngine.Object.Instantiate), [typeof(UnityEngine.Object), typeof(UnityEngine.Vector3), typeof(UnityEngine.Quaternion)])]
        public static bool Instantiate_Prefix(UnityEngine.Object original, Vector3 position, Quaternion rotation)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                if (original.name.Contains("BossSpawner"))
                {
                    return false; //Will be sent by the server later and client spawn without position/rotation
                }
            }

            return true;
        }

        /// <summary>Host: sends chest and procedural boss spawner spawns, registers final boss orbs. Client: registers received chests.</summary>

        [HarmonyPostfix]
        [HarmonyPatch(nameof(UnityEngine.Object.Instantiate), [typeof(UnityEngine.Object), typeof(UnityEngine.Vector3), typeof(UnityEngine.Quaternion)])]
        public static void Instantiate_Postfix(Il2CppObjectBase __result, UnityEngine.GameObject original, Vector3 position, Quaternion rotation)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                HandleQuestObjectsSpawned(original, __result);
                HandleBossSpawnerOnProceduralMap(__result);
                HandleDesertGraves(original, __result);
            }

            if (original.name.Contains("OpenChest") || (EffectManager.Instance != null && EffectManager.Instance.openChestNormal == original))
            {
                if (__result == null)
                {
                    return;
                }

                var resultAsObj = IL2CPP.PointerToValueGeneric<UnityEngine.Object>(__result.Pointer, false, false);

                if (isServer)
                {
                    synchronizationService.OnSpawnedChest(position, rotation, resultAsObj);
                }
                else
                {
                    if (Plugin.CAN_SPAWN_CHESTS)
                    {
                        var chestManagerService = Plugin.Services.GetService<Services.IChestManagerService>();
                        chestManagerService.SetNextChest(resultAsObj);
                    }
                }
            }

            if (MusicController.Instance != null && MusicController.Instance.finalFightController != null)
            {
                if (original == MusicController.Instance.finalFightController.orbBleed)
                {
                    var finalOrbManagerService = Plugin.Services.GetService<IFinalBossOrbManagerService>();
                    var nextTarget = finalOrbManagerService.PeakNextTarget();
                    if (nextTarget != null)
                    {
                        (uint nextTargetId, uint orbId) = nextTarget;
                        var resultAsGameObject = IL2CPP.PointerToValueGeneric<GameObject>(__result.Pointer, false, false);
                        finalOrbManagerService.SetOrbTarget(nextTargetId, resultAsGameObject, orbId);
                    }
                }

                if (original == MusicController.Instance.finalFightController.orbFollowing)
                {
                    var finalOrbManagerService = Plugin.Services.GetService<IFinalBossOrbManagerService>();
                    var nextTarget = finalOrbManagerService.PeakNextTarget();
                    if (nextTarget != null)
                    {
                        (uint nextTargetId, uint orbId) = nextTarget;
                        var resultAsGameObject = IL2CPP.PointerToValueGeneric<GameObject>(__result.Pointer, false, false);
                        finalOrbManagerService.SetOrbTarget(nextTargetId, resultAsGameObject, orbId);
                    }
                }

                if (original == MusicController.Instance.finalFightController.orbShooty)
                {
                    var finalOrbManagerService = Plugin.Services.GetService<IFinalBossOrbManagerService>();
                    var nextTarget = finalOrbManagerService.PeakNextTarget();
                    if (nextTarget != null)
                    {
                        (uint nextTargetId, uint orbId) = nextTarget;

                        var resultAsGameObject = IL2CPP.PointerToValueGeneric<GameObject>(__result.Pointer, false, false);
                        finalOrbManagerService.SetOrbTarget(nextTargetId, resultAsGameObject, orbId);
                    }
                }
            }
        }

        private static void HandleBossSpawnerOnProceduralMap(Il2CppObjectBase result)
        {
            if (MapController.runConfig.mapData.mapType == Assets.Scripts._Data.MapsAndStages.EMapType.ProceduralMesh) //Desert Map handles portals differently
            {
                var resultAsGameObject = IL2CPP.PointerToValueGeneric<GameObject>(result.Pointer, false, false);

                if (resultAsGameObject.name.StartsWith("BossSpawner"))
                {
                    var dynamic = DynamicData.For(resultAsGameObject);
                    var hasBeenSet = dynamic.Get<bool?>("hasBeenSetByServer");
                    if (!hasBeenSet.HasValue)
                    {
                        synchronizationService.OnSpawnedObject(resultAsGameObject);
                        dynamic.Set("hasBeenSetByServer", true);
                    }
                }
            }
        }

        private static void HandleQuestObjectsSpawned(UnityEngine.GameObject original, Il2CppObjectBase result)
        {
            if (EffectManager.Instance == null)
            {
                return;
            }

            var resultAsGameObject = IL2CPP.PointerToValueGeneric<GameObject>(result.Pointer, false, false);

            if (EffectManager.Instance.bananaQuest == original ||
               EffectManager.Instance.banditQuest == original ||
               EffectManager.Instance.boomboxQuest == original ||
               EffectManager.Instance.bushQuest == original ||
               EffectManager.Instance.katanaQuest == original ||
               EffectManager.Instance.luckTomeQuest == original ||
               EffectManager.Instance.shotgunQuest == original ||
               EffectManager.Instance.presentQuest == original ||
               EffectManager.Instance.frogQuest1 == original ||
               EffectManager.Instance.frogQuest2 == original ||
               EffectManager.Instance.frogQuest3 == original)
            {
                //if (EffectManager.Instance.bushQuest == original)
                //{
                //    resultAsGameObject.transform.position = GameManager.Instance.player.transform.position + GameManager.Instance.player.transform.forward * 2f;
                //}

                synchronizationService.OnSpawnedObject(resultAsGameObject);
            }
        }

        private static void HandleDesertGraves(GameObject original, Il2CppObjectBase result)
        {
            if (EffectManager.Instance == null)
            {
                return;
            }

            if (EffectManager.Instance.desertGraves.Contains(original) && Plugin.CAN_SEND_MESSAGES)
            {
                var resultAsGameObject = IL2CPP.PointerToValueGeneric<GameObject>(result.Pointer, false, false);
                synchronizationService.OnSpawnedObject(resultAsGameObject);
            }
        }

        /// <summary>Synchronizes final boss orb destruction.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(UnityEngine.Object.Destroy), [typeof(UnityEngine.Object)])]
        public static void Destroy_Postfix(Il2CppObjectBase obj)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            // FIX 6/6: Destroy(null) is legal; an NRE here escapes into the IL2CPP trampoline uncaught.
            if (obj == null)
            {
                return;
            }

            var finalOrbManagerService = Plugin.Services.GetService<IFinalBossOrbManagerService>();
            var resultAsObj = IL2CPP.PointerToValueGeneric<GameObject>(obj.Pointer, false, false);

            if (resultAsObj != null && finalOrbManagerService.ContainsOrbTarget(resultAsObj))
            {
                var removed = finalOrbManagerService.RemoveOrbTarget(resultAsObj);

                if (removed.HasValue)
                {
                    synchronizationService.OnFinalBossOrbDestroyed(removed.Value);
                }
            }

        }
    }
}

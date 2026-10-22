using OmniBonk.Helpers;
using HarmonyLib;
using OmniBonk.Common.Messages;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using System.Linq;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(FinalFightController))]
    internal static class FinalFightControllerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly ISpawnedObjectManagerService spawnedObjectManagerService = Plugin.Services.GetService<ISpawnedObjectManagerService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IFinalBossOrbManagerService finalBossOrbManagerService = Plugin.Services.GetService<IFinalBossOrbManagerService>();

        /// <summary>Use a custom seed to ensure same pylon spawn locations across all clients.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(FinalFightController.StartPylons))]
        public static void StartPylons_Postfix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            UnityEngine.Random.InitState(playerManagerService.GetSeed());
        }

        /// <summary>
        /// OB-7: fixed pylon ids by array index, not <c>AddSpawnedObject</c> (peers' counters diverge).
        /// <c>pylons</c> is a serialized array (<c>dump.cs</c> offset <c>0x30</c>), same order on every peer.
        /// </summary>
        private const uint PYLON_ID_BASE = 0x70000000;

        /// <summary>Register the pylons with fixed ids so every peer can resolve the host's charge messages.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(FinalFightController.StartPylons))]
        public static void StartPylons_Prefix(FinalFightController __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var pylons = __instance.pylons;
            if (pylons == null)
            {
                return;
            }

            for (var i = 0; i < pylons.Length; i++)
            {
                var pylon = pylons[i];
                if (pylon == null)
                {
                    continue;
                }

                var netplayId = PYLON_ID_BASE + (uint)i;

                // Runs once per pylon phase on the same pylons; already registered is normal.
                if (spawnedObjectManagerService.GetSpawnedObject(netplayId) == pylon.gameObject)
                {
                    continue;
                }

                spawnedObjectManagerService.SetSpawnedObject(netplayId, pylon.gameObject);
                DynamicData.For(pylon.gameObject).Set("netplayId", netplayId);
            }

            Plugin.Log.LogInfo($"OB-7: registered {pylons.Length} pylon(s) with fixed ids {PYLON_ID_BASE}+.");
        }

        /// <summary>
        /// OB-8: host-only spawn, held until the session starts. Guarded on "initialized": the
        /// cinematic (VA <c>0x180491D60</c>, <c>0x1804A0230</c>) calls this during the ready barrier,
        /// when "started" is still false, which made each peer spawn its own boss.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(FinalFightController.SpawnBoss))]
        public static bool SpawnBoss_Prefix(FinalFightController __instance)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            // Prefix-false on purpose: the host spawns it and the client receives SpawnedEnemy.
            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
            }

            if (synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            // Prefix-false on purpose: held until the session starts, then re-run (see above).
            Plugin.Log.LogInfo("[boss] final boss spawn held until the session starts (ready barrier still up).");
            CoroutineRunner.Instance.Run(SpawnBossOnceStarted(__instance));
            return false;
        }

        private static System.Collections.IEnumerator SpawnBossOnceStarted(FinalFightController controller)
        {
            while (synchronizationService.HasNetplaySessionInitialized() && !synchronizationService.HasNetplaySessionStarted())
            {
                yield return null;
            }

            if (controller == null || !synchronizationService.HasNetplaySessionStarted())
            {
                Plugin.Log.LogWarning("[boss] held final boss spawn dropped: the session ended or the arena is gone.");
                yield break;
            }

            Plugin.Log.LogInfo("[boss] session started; spawning the held final boss.");
            controller.SpawnBoss();
        }

        /// <summary>Skip on clients; the host sends the message.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(FinalFightController.SpecialAttacks))]
        public static bool SpecialAttacks_Prefix()
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

            return true;
        }


        /// <summary>Intercept orb bleed spawn so we can add a random target</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(FinalFightController.SpawnOrbsBleed))]
        public static bool SpawnOrbsBleed_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                // FIX P1-4 / P1-6: guarded against an empty set; a throw here escapes into the IL2CPP trampoline.
                var targetPlayerId = playerManagerService.GetRandomPlayerAliveConnectionId();
                if (targetPlayerId.HasValue)
                {
                    finalBossOrbManagerService.QueueNextTarget(targetPlayerId.Value);
                }
            }

            return true;
        }

        /// <summary>Intercept orb following spawn so we can add a random target</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(FinalFightController.SpawnOrbsFollowing))]
        public static bool SpawnOrbsFollowing_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                // FIX P1-4 / P1-6: guarded against an empty set; a throw here escapes into the IL2CPP trampoline.
                var targetPlayerId = playerManagerService.GetRandomPlayerAliveConnectionId();
                if (targetPlayerId.HasValue)
                {
                    finalBossOrbManagerService.QueueNextTarget(targetPlayerId.Value);
                }
            }

            return true;
        }

        /// <summary>Intercept orb shooty spawn so we can add a random target</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(FinalFightController.SpawnOrbsShooty))]
        public static bool SpawnOrbsShooty_Prefix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                // FIX P1-4 / P1-6: guarded against an empty set; a throw here escapes into the IL2CPP trampoline.
                var targetPlayerId = playerManagerService.GetRandomPlayerAliveConnectionId();
                if (targetPlayerId.HasValue)
                {
                    finalBossOrbManagerService.QueueNextTarget(targetPlayerId.Value);
                }
            }

            return true;
        }

        /// <summary>Synchronize orb bleed spawn</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(FinalFightController.SpawnOrbsBleed))]
        public static void SpawnOrbsBleed_Postfix(FinalFightController __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                synchronizationService.OnFinalBossOrbsSpawned(Orb.Bleed);
            }
        }

        /// <summary>Synchronize orb following spawn</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(FinalFightController.SpawnOrbsFollowing))]
        public static void SpawnOrbsFollowing_Postfix(FinalFightController __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }
            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                synchronizationService.OnFinalBossOrbsSpawned(Orb.Following);
            }
        }

        /// <summary>Synchronize orb shooty spawn</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(FinalFightController.SpawnOrbsShooty))]
        public static void SpawnOrbsShooty_Postfix(FinalFightController __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }
            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                synchronizationService.OnFinalBossOrbsSpawned(Orb.Shooty);
            }
        }
    }
}

using HarmonyLib;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using System.Linq;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(FinalFightController))]
    internal static class FinalFightControllerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly ISpawnedObjectManagerService spawnedObjectManagerService = Plugin.Services.GetService<ISpawnedObjectManagerService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IFinalBossOrbManagerService finalBossOrbManagerService = Plugin.Services.GetService<IFinalBossOrbManagerService>();

        /// <summary>
        /// Use a custom seed to ensure same pylon spawn locations across all clients.
        /// </summary>
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
        /// OB-7. Ids for the final fight's pylons, outside the range the shared counter hands out.
        ///
        /// <para><b>Why not <c>AddSpawnedObject</c>.</b> That counter is only advanced by the host
        /// for everything the host spawns and replicates (tumbleweeds, revive coffins, ...), while
        /// the client registers those with the host's id and leaves its own counter alone. Both
        /// peers then took pylon ids from their <i>own</i> counter, so the ids agreed only when the
        /// host had spawned nothing counted on the final stage before the pylons. That is how OB-7
        /// was "confirmed fixed" on 2026-08-18 without any code change, and why on 2026-10-18 the
        /// client could not find a single pylon the host charged.</para>
        ///
        /// <para><b>Why index is enough.</b> <c>pylons</c> is a serialized <c>BossPylon[]</c> on the
        /// controller (<c>dump.cs</c>, offset <c>0x30</c>), so both peers walk the same array in the
        /// same order. The base is far above anything the counter reaches in a stage, which
        /// <c>ResetForNextLevel</c> sets back to zero.</para>
        /// </summary>
        private const uint PYLON_ID_BASE = 0x70000000;

        /// <summary>
        /// Register the pylons with fixed ids so every peer can resolve the host's charge messages.
        /// </summary>
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
                spawnedObjectManagerService.SetSpawnedObject(netplayId, pylon.gameObject);
                DynamicData.For(pylon.gameObject).Set("netplayId", netplayId);
            }

            Plugin.Log.LogInfo($"OB-7: registered {pylons.Length} pylon(s) with fixed ids {PYLON_ID_BASE}+.");
        }

        /// <summary>
        /// Skip boss spawn on clients (the server will send the message)
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(FinalFightController.SpawnBoss))]
        public static bool SpawnBoss_Prefix()
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

        /// <summary>
        /// Skip special attacks on clients (the server will send the message)
        /// </summary>
        /// <returns></returns>
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


        /// <summary>
        /// Intercept orb bleed spawn so we can add a random target
        /// </summary>
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
                // FIX P1-4 / P1-6: was a Player[] plus iterators, enumerated twice (Count then
                // ElementAt), with the unguarded shape P1-6 fixed in ReTargetEnemies —
                // Random.Range(0, 0) returns 0 and ElementAt(0) throws on an empty set. This is a
                // Harmony prefix, so that throw would escape into the IL2CPP trampoline rather
                // than being caught. GetRandomPlayerAliveConnectionId is guarded and allocates
                // one list.
                var targetPlayerId = playerManagerService.GetRandomPlayerAliveConnectionId();
                if (targetPlayerId.HasValue)
                {
                    finalBossOrbManagerService.QueueNextTarget(targetPlayerId.Value);
                }
            }

            return true;
        }

        /// <summary>
        /// Intercept orb following spawn so we can add a random target
        /// </summary>
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
                // FIX P1-4 / P1-6: was a Player[] plus iterators, enumerated twice (Count then
                // ElementAt), with the unguarded shape P1-6 fixed in ReTargetEnemies —
                // Random.Range(0, 0) returns 0 and ElementAt(0) throws on an empty set. This is a
                // Harmony prefix, so that throw would escape into the IL2CPP trampoline rather
                // than being caught. GetRandomPlayerAliveConnectionId is guarded and allocates
                // one list.
                var targetPlayerId = playerManagerService.GetRandomPlayerAliveConnectionId();
                if (targetPlayerId.HasValue)
                {
                    finalBossOrbManagerService.QueueNextTarget(targetPlayerId.Value);
                }
            }

            return true;
        }

        /// <summary>
        /// Intercept orb shooty spawn so we can add a random target
        /// </summary>
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
                // FIX P1-4 / P1-6: was a Player[] plus iterators, enumerated twice (Count then
                // ElementAt), with the unguarded shape P1-6 fixed in ReTargetEnemies —
                // Random.Range(0, 0) returns 0 and ElementAt(0) throws on an empty set. This is a
                // Harmony prefix, so that throw would escape into the IL2CPP trampoline rather
                // than being caught. GetRandomPlayerAliveConnectionId is guarded and allocates
                // one list.
                var targetPlayerId = playerManagerService.GetRandomPlayerAliveConnectionId();
                if (targetPlayerId.HasValue)
                {
                    finalBossOrbManagerService.QueueNextTarget(targetPlayerId.Value);
                }
            }

            return true;
        }

        /// <summary>
        /// Synchronize orb bleed spawn
        /// </summary>
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

        /// <summary>
        /// Synchronize orb following spawn
        /// </summary>
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

        /// <summary>
        /// Synchronize orb shooty spawn
        /// </summary>
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

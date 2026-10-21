using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(GraveyardBossRoom))]
    internal static class GraveyardBossRoomPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly ISpawnedObjectManagerService spawnedObjectManagerService = Plugin.Services.GetService<ISpawnedObjectManagerService>();

        /// <summary>
        /// Set another custom world size specifically because the boss spawn low enough to be outside normal world bounds
        /// Also register all lamps and the boss leave interactable for synchronization
        /// </summary>
        /// <param name="__instance"></param>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(GraveyardBossRoom.Activate))]
        public static void Activate_Prefix(GraveyardBossRoom __instance)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            Plugin.Instance.SetWorldSize(new UnityEngine.Vector3(7500f, 7500f, 7500f));

            var isHost = synchronizationService.IsServerMode() ?? false;

            //Should be the same order across all players
            foreach (var lamp in __instance.lamps)
            {
                uint netplayId = 0;
                if (isHost)
                {
                    netplayId = spawnedObjectManagerService.AddSpawnedObject(lamp.gameObject);
                }
                else
                {
                    netplayId = spawnedObjectManagerService.SetSpawnedObjectAtLast(lamp.gameObject);
                }

                if (netplayId == 0)
                {
                    Plugin.Log.LogWarning("Failed to assign netplayId to graveyard boss room lamp!");
                    return;
                }
                DynamicData.For(lamp.gameObject).Set("netplayId", netplayId);
            }

            uint bossLeaveNetplayId = 0;
            if (isHost)
            {
                bossLeaveNetplayId = spawnedObjectManagerService.AddSpawnedObject(__instance.interactableGhostBossLeave.gameObject);
            }
            else
            {
                bossLeaveNetplayId = spawnedObjectManagerService.SetSpawnedObjectAtLast(__instance.interactableGhostBossLeave.gameObject);
            }

            if (bossLeaveNetplayId == 0)
            {
                Plugin.Log.LogWarning("Failed to assign netplayId to graveyard boss room boss leave!");
                return;
            }

            DynamicData.For(__instance.interactableGhostBossLeave.gameObject).Set("netplayId", bossLeaveNetplayId);
        }

        /// <summary>
        /// Client: the Ghost King's spawn, without the spawn. <c>CheckSpawnBoss</c> (VA 0x180505F90,
        /// decompiled; Ghidra's labels are a slot off, read against dump.cs offsets) waits for
        /// <c>isFightingBoss</c> and <c>spawnBossAtTime</c>, sets <c>hasSpawnedBoss</c>, calls
        /// <c>EnemyManager.SpawnBoss</c> and then uses the boss it returns (armor, lamp colliders),
        /// plays <c>musicTrackBoss</c> and updates the objective. On a client <c>SpawnBoss</c> returns
        /// null by design (only the host's boss is real), so the method threw on the null boss once a
        /// fight and the client never got the boss music (2026-10-21, a bot in the Ghost King's room).
        ///
        /// <para>Prefix-false on purpose, on clients: the same check and flag, then the music. The boss,
        /// its armor and the objective arrive with the host's <c>SpawnedEnemy</c>
        /// (<c>ProcessSpawnedEnemy</c>'s Ghost King branch). The lamp colliders only matter to the
        /// host's physics. <b>UNVERIFIED, not run in-game.</b></para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("CheckSpawnBoss")]
        public static bool CheckSpawnBoss_Prefix(GraveyardBossRoom __instance)
        {
            if (!synchronizationService.HasNetplaySessionInitialized() || (synchronizationService.IsServerMode() ?? false))
            {
                return true;
            }

            if (!__instance.hasSpawnedBoss && __instance.isFightingBoss && __instance.spawnBossAtTime <= Assets.Scripts.Utility.MyTime.time)
            {
                __instance.hasSpawnedBoss = true;
                if (MusicController.Instance != null && __instance.musicTrackBoss != null)
                {
                    MusicController.Instance.PlayMusicTrack(__instance.musicTrackBoss);
                }
            }

            return false;
        }
    }
}

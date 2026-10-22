using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(GraveyardBossRoom))]
    internal static class GraveyardBossRoomPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly ISpawnedObjectManagerService spawnedObjectManagerService = Plugin.Services.GetService<ISpawnedObjectManagerService>();

        /// <summary>
        /// Enlarges world bounds (the boss spawns below them) and registers lamps and the exit for sync.
        /// </summary>
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
        /// Client: <c>CheckSpawnBoss</c> (VA 0x180505F90, decompiled) dereferences the boss <c>SpawnBoss</c>
        /// returns, which is null on clients. Prefix-false on clients: same check and flag, then the music;
        /// the boss arrives via the host's <c>SpawnedEnemy</c>. <b>UNVERIFIED, not run in-game.</b>
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

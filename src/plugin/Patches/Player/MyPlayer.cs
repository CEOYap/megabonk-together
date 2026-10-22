using Assets.Scripts.Actors.Player;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches.Player
{
    [HarmonyPatch(typeof(MyPlayer))]
    internal class MyPlayerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Answers with a remote player's position while one is queued (e.g. spawning their projectiles).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MyPlayer.GetFeetPosition))]
        public static bool GetFeetPosition_Prefix(ref Vector3 __result)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (playerManagerService.TryGetProjectileToSpawn(out uint netplayerId))
            {
                var netPlayer = playerManagerService.GetNetPlayerByNetplayId(netplayerId);

                if (netPlayer == null)
                {
                    return true;
                }
                __result = netPlayer.Model.transform.position;
                return false;
            }

            return true;
        }
    }
}

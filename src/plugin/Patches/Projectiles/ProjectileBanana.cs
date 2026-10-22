using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileBanana))]
    internal static class ProjectileBananaPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();


        /// <summary>Use netplayer position if projectile belongs to a netplayer.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileBanana.MyFixedUpdate))]
        private static bool MyFixedUpdate_Prefix(ProjectileBanana __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;
            if (!isHost)
            {
                return false;
            }

            var netPlayer = Plugin.Services.GetService<IPlayerManagerService>().GetNetPlayerByWeapon(__instance.weaponBase);
            if (netPlayer == null)
            {
                return true;
            }

            playerManagerService.AddGetNetplayerPositionRequest(netPlayer.ConnectionId);

            return true;
        }

        /// <summary>Unqueue the previous request</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ProjectileBanana.MyFixedUpdate))]
        private static void MyFixedUpdate_Postfix(ProjectileBanana __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;
            if (!isHost)
            {
                return;
            }

            var netPlayer = Plugin.Services.GetService<IPlayerManagerService>().GetNetPlayerByWeapon(__instance.weaponBase);
            if (netPlayer == null)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();

        }
    }
}

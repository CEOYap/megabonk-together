using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(PickupOrb))]
    internal static class PickupOrbPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Only the host processes a pickup orb hitting the ground.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PickupOrb.OnCollisionEnter))]
        public static bool OnCollisionEnter_Prefix()
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


        /// <summary>Client destroys the orb; the host's resulting pickup spawn is replicated separately.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(PickupOrb.OnCollisionEnter))]
        public static void OnCollisionEnter_Postfix(PickupOrb __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                GameObject.Destroy(__instance.gameObject);
            }
        }
    }
}

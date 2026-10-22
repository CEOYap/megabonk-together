using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using UnityEngine;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(Pickup))]
    internal static class PickupPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IPickupManagerService pickupManagerService = Plugin.Services.GetService<IPickupManagerService>();

        /// <summary>Applies only pickups the local player owns (time/magnet and shared XP excepted).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Pickup.ApplyPickup))]
        public static bool ApplyPickup_Prefix(Pickup __instance, ref bool? __state)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }
            __state = true;

            if (__instance.ePickup == EPickup.Time || __instance.ePickup == EPickup.Magnet)
            {
                return true;
            }

            var dynPickup = DynamicData.For(__instance);
            var ownerId = dynPickup.Get<uint?>("ownerId");
            if (ownerId.HasValue)
            {
                var isRemote = playerManagerService.IsRemoteConnectionId(ownerId.Value);

                if (isRemote)
                {
                    __state = false;


                    return false;
                }
            }

            return true;
        }


        /// <summary>Broadcasts a locally applied pickup.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Pickup.ApplyPickup))]
        public static void ApplyPickup_Postfix(Pickup __instance, bool? __state)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (__state.HasValue && !__state.Value) //Ignore if prefix already handled it
            {
                DynamicData.For(__instance).Data.Clear();
                PickupManager.Instance.DespawnPickup(__instance);
                return;
            }

            var dynPickup = DynamicData.For(__instance);
            var ownerId = dynPickup.Get<uint?>("ownerId");
            if (ownerId.HasValue && playerManagerService.IsRemoteConnectionId(ownerId.Value))
            {
                return;
            }

            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return;
            }

            synchronizationService.OnPickupApplied(__instance);
        }

        /// <summary>Follows if owned, else asks the host; skipped while a request is pending (e.g. a pot).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Pickup.StartFollowingPlayer))]
        public static bool StartFollowingPlayer_Prefix(Pickup __instance, ref Transform target)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var netplayerId = playerManagerService.PeakNetplayerPositionRequest();
            if (netplayerId.HasValue)
            {
                target = playerManagerService.GetNetPlayerByNetplayId(netplayerId.Value).Model.transform;
                return true;
            }

            __instance.pickedUp = false;

            var dynInstance = DynamicData.For(__instance);

            var ownerId = dynInstance.Get<uint?>("ownerId");
            if (ownerId.HasValue)
            {
                return true;
            }

            var hasSent = dynInstance.Get<bool?>("hasSentAlready");
            if (hasSent.HasValue && hasSent.Value)
            {
                return false;
            }

            synchronizationService.OnWantToStartFollowingPickup(__instance);
            return false;
        }
    }
}

using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using UnityEngine;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(PickupManager))]
    internal static class PickupManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IPickupManagerService pickupManagerService = Plugin.Services.GetService<IPickupManagerService>();

        /// <summary>Host only, unless explicitly allowed.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PickupManager.SpawnPickup))]
        public static bool SpawnPickup_Prefix(ref bool useRandomOffsetPosition)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            useRandomOffsetPosition = false;

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                return true;
            }

            return Plugin.CAN_SPAWN_PICKUPS;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(PickupManager.SpawnPickup))]
        public static void SpawnPickup_Postfix(Pickup __result, EPickup ePickup, ref Vector3 pos, int value)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (__result == null)
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;

            // OB-5 diagnostic, read before the Clear below. Decompiled SpawnPickup (VA 0x1804DED60)
            // merges into an existing overlapping pickup and returns it, so a prior pickupId means
            // one object is about to get a second id. Counted only; nothing changed yet.
            if (isServer)
            {
                var priorId = DynamicData.For(__result).Get<uint?>("pickupId");
                if (priorId.HasValue)
                {
                    RecordMerge(priorId.Value, ePickup);
                }
            }

            DynamicData.For(__result).Data.Clear();

            if (isServer)
            {
                synchronizationService.OnPickupSpawned(__result, ePickup, pos, value);
            }
        }

        private static int mergesSinceReport = 0;
        private static int mergesTotal = 0;
        private static float lastMergeReport = -999f;

        /// <summary>Reports at most every 10 s; silent when nothing merges.</summary>
        private static void RecordMerge(uint priorId, EPickup ePickup)
        {
            mergesSinceReport++;
            mergesTotal++;

            var now = Time.unscaledTime;
            if (now - lastMergeReport < 10f)
            {
                return;
            }

            Plugin.Log.LogInfo(
                $"OB-5: {mergesSinceReport} pickup merge(s) re-registered under a new id " +
                $"(latest: {ePickup} formerly id {priorId}; {mergesTotal} this session). " +
                "Each leaves its old id pointing at the same object.");
            mergesSinceReport = 0;
            lastMergeReport = now;
        }

        /// <summary>Host only: XP goes to a random alive player, not just the local one (magnet shrines).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PickupManager.PickupAllXp))]
        public static bool PickupAllXp_Postfix()
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

            var xpPickups = pickupManagerService.GetAllPickupXp();
            foreach ((var pickupId, var pickup) in xpPickups)
            {
                var dynPickup = DynamicData.For(pickup);

                var randomConnectionId = playerManagerService.GetRandomPlayerAliveConnectionId();
                if (randomConnectionId.HasValue)
                {
                    if (playerManagerService.IsLocalConnectionId(randomConnectionId.Value))
                    {
                        dynPickup.Set("ownerId", playerManagerService.GetLocalPlayer().ConnectionId);
                        pickup.StartFollowingPlayer(GameManager.Instance.player.transform);
                        synchronizationService.SendPickupFollowingPlayer(playerManagerService.GetLocalPlayer().ConnectionId, pickupId);
                        continue;
                    }
                    else
                    {
                        var randomNetPlayer = playerManagerService.GetNetPlayerByNetplayId(randomConnectionId.Value);
                        if (randomNetPlayer != null)
                        {
                            dynPickup.Set("ownerId", randomNetPlayer.ConnectionId);
                            pickup.StartFollowingPlayer(randomNetPlayer.Model.transform);
                            synchronizationService.SendPickupFollowingPlayer(randomNetPlayer.ConnectionId, pickupId);
                            continue;
                        }
                    }
                }
            }

            return false;
        }
    }
}

using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using UnityEngine;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(PickupManager))]
    internal static class PickupManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IPickupManagerService pickupManagerService = Plugin.Services.GetService<IPickupManagerService>();

        /// <summary>
        /// Spawn pickups only on the server unless explicitly allowed
        /// </summary>
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

        /// <summary>
        /// Synchronize spawned pickups to clients
        /// </summary>
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

            // OB-5 diagnostic. Read BEFORE the Clear below, which is what destroys the evidence.
            //
            // PickupManager.SpawnPickup (VA 0x1804DED60, decompiled) calls CheckOverlap and, when a
            // pickup of the same kind is already there, does Pickup.AddValue on the EXISTING one and
            // returns it -- no new pickup exists. A __result that already carries a pickupId is
            // therefore a merge, and this postfix then wipes that id and OnPickupSpawned registers the
            // same object a second time under a new one. Two ids, one host object: once it is
            // consumed under one id the other dangles, and a client pulling it gets nothing -- the
            // shape of OB-5's residual drops. Whether merges happen often enough to matter is what
            // this counts; nothing is changed yet.
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

        /// <summary>
        /// Counts host-side pickup merges and reports at Info, at most every 10 s of unscaled time.
        /// Silent when nothing merges, so a clean log is evidence rather than absence.
        /// </summary>
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

        /// <summary>
        /// Use random player instead of only local player (Trigerred by magnet shrines and other ?). Server side only.
        /// </summary>
        /// <returns></returns>
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

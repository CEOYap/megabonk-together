using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Extensions;
using MonoMod.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MegabonkTogether.Services
{
    public interface IPickupManagerService
    {
        public IEnumerable<PickupModel> GetAllPickups();
        public IEnumerable<(uint, Pickup)> GetAllPickupXp();
        public uint AddSpawnedPickup(Pickup pickup);
        public void SetSpawnedPickup(uint pickupId, Pickup pickup);
        public Pickup GetPickupById(uint id);
        public KeyValuePair<uint, Pickup> GetPickupByReference(Pickup pickup);
        public Pickup GetSpawnedPickupById(uint id);
        public void RemoveSpawnedPickupById(uint id);
        public void ResetForNextLevel();
        public int StopFollowingDepartedPlayer(uint connectionId);
    }
    internal class PickupManagerService : IPickupManagerService
    {
        private readonly ConcurrentDictionary<uint, Pickup> spawnedPickups = [];
        // FIX P0-3: see EnemyManagerService — int so allocation can go through Interlocked,
        // 0 stays reserved as the failure sentinel.
        private int currentPickupId = 0;

        public IEnumerable<PickupModel> GetAllPickups()
        {
            return spawnedPickups.Select(kv => kv.Value.ToModel(kv.Key)).ToList();
        }

        public Pickup GetPickupById(uint id)
        {
            if (spawnedPickups.TryGetValue(id, out var pickup))
            {
                return pickup;
            }
            return null;
        }


        /// <summary>
        /// Server side
        /// </summary>
        public uint AddSpawnedPickup(Pickup pickup)
        {
            // FIX P0-3: atomic allocation, single read.
            var newId = (uint)Interlocked.Increment(ref currentPickupId);

            if (!spawnedPickups.TryAdd(newId, pickup))
            {
                Plugin.Log.LogWarning($"Attempted to add an pickup that already exists. PickupId: {newId}");
                return 0;
            }

            return newId;
        }

        /// <summary>
        /// Client side
        /// </summary>
        public void SetSpawnedPickup(uint pickupId, Pickup pickup)
        {
            if (!spawnedPickups.TryAdd(pickupId, pickup))
            {
                Plugin.Log.LogWarning($"Attempted to add an pickup that already exists. PickupId: {pickupId}");
            }
        }

        public KeyValuePair<uint, Pickup> GetPickupByReference(Pickup pickup)
        {
            return spawnedPickups.FirstOrDefault(kv => kv.Value == pickup);
        }

        public Pickup GetSpawnedPickupById(uint id)
        {
            if (spawnedPickups.TryGetValue(id, out var pickup))
            {
                return pickup;
            }
            return null;
        }

        public void RemoveSpawnedPickupById(uint id)
        {
            if (!spawnedPickups.TryRemove(id, out var pickup))
            {
                return;
            }

            if (pickup.ePickup == EPickup.Xp) //Properly cleanup XP pickups
            {
                PickupManager.Instance.xpList.RemovePickup(pickup);
            }
        }

        public IEnumerable<(uint, Pickup)> GetAllPickupXp()
        {
            return spawnedPickups
                .ToList()
                .Where(p => p.Value.ePickup == EPickup.Xp)
                .Select(p => (p.Key, p.Value));
        }

        /// <summary>
        /// Stops any pickup that was flying toward <paramref name="connectionId"/>, and returns how
        /// many were stopped.
        ///
        /// <para><b>Why (Run A, fourth repeat).</b> The game's own <c>Pickup</c> stores the
        /// <c>Transform</c> handed to <c>StartFollowingPlayer</c> and reads it every frame while it
        /// homes in. The mod passes a NetPlayer's <c>Model.transform</c> there
        /// (<c>Patches/PickupManager.cs:106</c>, <c>Patches/Pickup.cs:107</c>), so when that
        /// NetPlayer is destroyed on disconnect the pickup keeps following a destroyed Transform
        /// forever — game code, per frame, on a bare UnityEngine.Transform.</para>
        ///
        /// <para>That is every property the diagnostic measured: the destroyed instance is a
        /// <c>UnityEngine.Transform</c> rather than a component; the caller carries no
        /// MegabonkTogether frames because the read is the game's follow logic; and the rate held at
        /// ~143/s across two runs where the enemy count moved 17 -> 2, because it never depended on
        /// enemies at all. A handful of pickups mid-flight is exactly the "small fixed number of
        /// long-lived holders" the rate implied.</para>
        ///
        /// <para><b>UNVERIFIED:</b> that clearing <c>pickedUp</c> and the stored owner is enough to
        /// make the game drop its Transform reference. The <c>Pickup</c> body is a stub, so whether
        /// it re-reads the target only while <c>pickedUp</c> is set — or holds the Transform in a
        /// field this cannot reach — is unknown. If the fallback rate does not drop, that is the
        /// answer, and the next step is destroying the pickup outright rather than un-following
        /// it.</para>
        /// </summary>
        public int StopFollowingDepartedPlayer(uint connectionId)
        {
            var stopped = 0;

            foreach (var kv in spawnedPickups.ToList())
            {
                var pickup = kv.Value;
                if (pickup == null) // Unity's overloaded == also catches a destroyed pickup
                {
                    continue;
                }

                try
                {
                    var owner = DynamicData.For(pickup).Get<uint?>("ownerId");
                    if (owner != connectionId)
                    {
                        continue;
                    }

                    pickup.pickedUp = false;
                    DynamicData.For(pickup).Set("ownerId", (uint?)null);
                    stopped++;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"Could not stop pickup {kv.Key} following departed player {connectionId}: {ex.Message}");
                }
            }

            return stopped;
        }

        public void ResetForNextLevel()
        {
            Interlocked.Exchange(ref currentPickupId, 0);
            //spawnedPickups.Select(kv => kv.Value).ToList().ForEach(p => GameObject.Destroy(p.gameObject));
            spawnedPickups.Clear();
        }
    }
}

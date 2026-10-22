using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using OmniBonk.Common.Models;
using OmniBonk.Extensions;
using MonoMod.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OmniBonk.Services
{
    public interface IPickupManagerService
    {
        public IEnumerable<PickupModel> GetAllPickups();
        public IEnumerable<(uint, Pickup)> GetAllPickupXp();
        public uint AddSpawnedPickup(Pickup pickup);

        /// <summary>Desync detector. Entries in the registry; OB-5 merges can alias two ids to one object.</summary>
        int TrackedPickupCount { get; }
        public void SetSpawnedPickup(uint pickupId, Pickup pickup);
        public Pickup GetPickupById(uint id);
        public KeyValuePair<uint, Pickup> GetPickupByReference(Pickup pickup);
        public Pickup GetSpawnedPickupById(uint id);
        public void RemoveSpawnedPickupById(uint id);
        public void ResetForNextLevel();
        public int StopFollowingDepartedPlayer(uint connectionId, UnityEngine.Transform departingTransform);
    }
    internal class PickupManagerService : IPickupManagerService
    {
        private readonly ConcurrentDictionary<uint, Pickup> spawnedPickups = [];

        public int TrackedPickupCount => spawnedPickups.Count;
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

            // OB-5: lets a later "not found" say whether this peer ever knew the id.
            PickupLifetimeDiagnostics.RecordRegistered(newId);

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

            // OB-5: recorded even when TryAdd loses; the id is known either way.
            PickupLifetimeDiagnostics.RecordRegistered(pickupId);
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
        /// Stops pickups homing on a departed player; returns how many. A pickup follows the
        /// Transform it was given every frame, so a destroyed NetPlayer leaves it dangling.
        /// <para>dump.cs: <c>Pickup</c> holds <c>private Transform target</c> at 0x38 and
        /// <c>pickedUp</c> at 0x30, so matching and nulling <c>target</c> is what releases it.</para>
        /// <para><b>UNVERIFIED:</b> whether the game re-populates <c>target</c> afterwards, and
        /// whether pickups account for all of the ~143/s fallback rate.</para>
        /// </summary>
        public int StopFollowingDepartedPlayer(uint connectionId, UnityEngine.Transform departingTransform)
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
                    // Match on target, not ownerId: not every path sets ownerId.
                    var target = pickup.target;
                    if (!IsFollowingDepartedPlayer(target, departingTransform))
                    {
                        continue;
                    }

                    // Null target itself; clearing pickedUp alone did not release it.
                    pickup.target = null;
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

        /// <summary>
        /// Matches the departing transform, or any destroyed one (Unity null, managed non-null).
        /// Never matches a live player's target.
        /// </summary>
        private static bool IsFollowingDepartedPlayer(UnityEngine.Transform target, UnityEngine.Transform departingTransform)
        {
            if (target is null)
            {
                return false;
            }

            if (target == null) // destroyed: Unity's operator, not a managed null
            {
                return true;
            }

            return departingTransform is not null
                && departingTransform != null
                && target == departingTransform;
        }

        public void ResetForNextLevel()
        {
            Interlocked.Exchange(ref currentPickupId, 0);
            //spawnedPickups.Select(kv => kv.Value).ToList().ForEach(p => GameObject.Destroy(p.gameObject));
            spawnedPickups.Clear();
        }
    }
}

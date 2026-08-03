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
        public int StopFollowingDepartedPlayer(uint connectionId, UnityEngine.Transform departingTransform);
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
        /// <para><b>The first attempt at this did nothing, and the dump says why.</b> It keyed on
        /// our own <c>ownerId</c> and cleared only <c>pickedUp</c>. In the run that followed, no
        /// "Stopped N pickup(s)" line appeared at all and the fallbacks continued unchanged — the
        /// sweep matched nothing. Only <c>Patches/PickupManager.cs:106</c> sets <c>ownerId</c>;
        /// <c>Patches/Pickup.cs:107</c> hands over a <c>Model.transform</c> and sets nothing, so
        /// pickups routed that way were invisible to it.</para>
        ///
        /// <para><c>megabonk-re/build-21750826/dump.cs</c> shows <c>Pickup</c> holds
        /// <c>private Transform target</c> at 0x38 alongside <c>private bool pickedUp</c> at 0x30,
        /// and ticks both <c>Update()</c> and <c>FixedUpdate()</c>. So the reference lives in
        /// <c>target</c>, and clearing <c>pickedUp</c> could never release it if the tick reads
        /// <c>target</c> outside that flag. This now matches on <c>target</c> itself and nulls it,
        /// neither of which depends on our bookkeeping or on knowing which method reads it.</para>
        ///
        /// <para><b>UNVERIFIED:</b> whether the game re-populates <c>target</c> afterwards, and
        /// whether pickups are the whole of the ~143/s. Five runs have held that rate across every
        /// other variable changing, and no candidate so far has moved it. If this does not either,
        /// the instance-id counter is the next step rather than a sixth candidate.</para>
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
                    // Matched on the Transform the pickup is actually holding, not on our own
                    // ownerId bookkeeping. The first attempt keyed on ownerId and found zero
                    // pickups in a run where the fallbacks continued unchanged — because only
                    // PickupManager.cs:106 sets that key, while Pickup.cs:107 hands over a
                    // Model.transform and sets nothing. Reading the field the game itself uses
                    // cannot miss a pickup for want of bookkeeping.
                    var target = pickup.target;
                    if (!IsFollowingDepartedPlayer(target, departingTransform))
                    {
                        continue;
                    }

                    // Clearing target, not just pickedUp. The dump shows Pickup holds
                    // `private Transform target` at 0x38 and ticks Update()/FixedUpdate() every
                    // frame; the first attempt cleared pickedUp and left target set, which cannot
                    // drop the reference if Update reads it outside that flag. Nulling the field
                    // the reference lives in does not depend on knowing which.
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
        /// True when a pickup's <paramref name="target"/> belongs to the player who is leaving.
        ///
        /// <para>Two cases, because the sweep's position relative to the NetPlayer's destruction is
        /// not fixed. Called from RemovePlayer it runs <b>before</b> destruction, so the match is
        /// reference identity against the departing player's own transform. Called after — or for a
        /// pickup left over from an earlier disconnect — the transform is already destroyed, which
        /// Unity's <c>==</c> reports as null while the managed reference is still non-null. Either
        /// is a pickup that must let go.</para>
        ///
        /// <para>A pickup following a live player fails both tests and is left alone, which is the
        /// property that matters: this must never steal a pickup mid-flight to someone still
        /// playing.</para>
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

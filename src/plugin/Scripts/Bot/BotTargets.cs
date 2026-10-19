using Assets.Scripts.Inventory__Items__Pickups.Chests;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using MegabonkTogether.Scripts.Interactables;
using MegabonkTogether.Services;
using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Scripts.Bot
{
    /// <summary>
    /// Test bot: what it may walk to. Chests and shrines it is nearer to than the host (design goal 6),
    /// and the host's revive coffin.
    ///
    /// <para><b>Chests and shrines only.</b> An allowlist rather than "any interactable": portals and
    /// boss spawners are the owner's objectives, a shady guy opens a shop the bot has no use for, and
    /// the cursed shrine adds a boss curse to everyone's run. Candidates come from
    /// <c>Physics.OverlapSphere</c> on the game's own interactable layer
    /// (<c>DetectInteractables.whatIsInteractable</c>), at most once a second.</para>
    ///
    /// <para><b>The coffin is the mod's <see cref="InteractableReviver"/></b>, not the game's
    /// <c>InteractableDesertGrave</c>: <c>SpawnReviver</c> destroys the grave component and adds the
    /// reviver. The Desert map's own puzzle graves are <c>InteractableDesertGrave</c> too, so
    /// searching for that type would have found the wrong objects.</para>
    /// </summary>
    internal static class BotTargets
    {
        private const float SCAN_INTERVAL_SECONDS = 1f;

        private static readonly HashSet<int> blacklist = [];
        private static float nextScanTime;
        private static BaseInteractable cachedDetour;

        /// <summary>True when <paramref name="target"/> is strictly closer to the bot than to the host.</summary>
        internal static bool IsCloserToBot(Vector3 target, Vector3 bot, Vector3 owner)
            => (target - bot).sqrMagnitude < (target - owner).sqrMagnitude;

        /// <summary>Gives up on a target for the rest of the stage.</summary>
        internal static void Blacklist(BaseInteractable target)
        {
            if (target != null)
            {
                blacklist.Add(target.GetInstanceID());
            }

            cachedDetour = null;
        }

        /// <summary>Called on a stage transition: new map, new interactables.</summary>
        internal static void ResetStage()
        {
            blacklist.Clear();
            cachedDetour = null;
            nextScanTime = 0f;
        }

        /// <summary>The nearest allowed chest or shrine within <paramref name="maxRange"/>, or null.</summary>
        internal static BaseInteractable FindDetour(Vector3 bot, Vector3 owner, float maxRange, DetectInteractables detect)
        {
            if (Time.unscaledTime < nextScanTime)
            {
                return cachedDetour != null && cachedDetour.CanInteract() ? cachedDetour : null;
            }

            nextScanTime = Time.unscaledTime + SCAN_INTERVAL_SECONDS;
            cachedDetour = null;
            if (detect == null)
            {
                return null;
            }

            // Not Physics.OverlapSphere directly: see Il2CppFindHelper.RuntimeOverlapSphere.
            var hits = Helpers.Il2CppFindHelper.RuntimeOverlapSphere(bot, maxRange, detect.whatIsInteractable);
            if (hits == null)
            {
                return null;
            }

            var bestDistance = float.MaxValue;
            foreach (var hit in hits)
            {
                var interactable = hit != null ? hit.GetComponentInParent<BaseInteractable>() : null;
                if (interactable == null
                    || blacklist.Contains(interactable.GetInstanceID())
                    || !IsAllowed(interactable.gameObject)
                    || !interactable.CanInteract())
                {
                    continue;
                }

                var position = interactable.transform.position;
                if (!IsCloserToBot(position, bot, owner))
                {
                    continue;
                }

                var distance = (position - bot).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    cachedDetour = interactable;
                }
            }

            return cachedDetour;
        }

        private static bool IsAllowed(GameObject obj)
            => obj.GetComponent<InteractableChest>() != null
            || obj.GetComponent<ChargeShrine>() != null
            || obj.GetComponent<InteractableShrineMagnet>() != null
            || obj.GetComponent<InteractableShrineMoai>() != null
            || obj.GetComponent<InteractableShrineBalance>() != null
            || obj.GetComponent<InteractableShrineGreed>() != null
            || obj.GetComponent<InteractableShrineChallenge>() != null;

        /// <summary>The host's revive coffin that can still be used, or null.</summary>
        internal static InteractableReviver FindOwnerCoffin(uint ownerId, ISpawnedObjectManagerService spawnedObjects)
        {
            foreach (var (_, obj) in spawnedObjects.GetAllSpawnedObjects())
            {
                var reviver = obj != null ? obj.GetComponent<InteractableReviver>() : null;
                if (reviver != null && reviver.OwnerId == ownerId && reviver.CanInteract())
                {
                    return reviver;
                }
            }

            return null;
        }
    }
}

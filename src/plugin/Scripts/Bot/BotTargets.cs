// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using Assets.Scripts.Inventory__Items__Pickups.Chests;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using OmniBonk.Scripts.Interactables;
using OmniBonk.Services;
using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Scripts.Bot
{
    /// <summary>
    /// Test bot targets: chests and shrines nearer to it than the host (allowlist), and the host's
    /// revive coffin, which is the mod's <see cref="InteractableReviver"/>, not <c>InteractableDesertGrave</c>.
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
#endif

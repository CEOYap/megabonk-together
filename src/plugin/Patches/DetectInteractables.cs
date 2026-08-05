using Assets.Scripts.Inventory__Items__Pickups.Chests;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using HarmonyLib;
using MegabonkTogether.Helpers;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(DetectInteractables))]
    public static class DetectInteractablesPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Send interaction to other players
        /// </summary>

        [HarmonyPrefix]
        [HarmonyPatch(nameof(DetectInteractables.TryInteract))]
        public static bool TryInteract_Prefix(DetectInteractables __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (!Plugin.CAN_SEND_MESSAGES) //Prevent sending messages on received events
            {
                return true;
            }

            if (__instance.currentInteractable == null)
            {
                return true;
            }

            if (!CanSynchronize(__instance))
            {
                return true;
            }

            synchronizationService.OnInteractableUsed(__instance.currentInteractable);

            var isHost = synchronizationService.IsServerMode() ?? false;
            if (!isHost)
            {
                return CanSimulateClientSide(__instance.currentInteractable);
            }

            return true;
        }

        private static bool CanSimulateClientSide(BaseInteractable interactable)
        {
            var challengeShrine = interactable.GetComponentInChildren<InteractableShrineChallenge>();
            if (challengeShrine != null)
            {
                challengeShrine.done = true;
                challengeShrine.fx.SetActive(true);
                GameObject.Destroy(challengeShrine.alertIcon);
                return false;
            }

            var egg = interactable.GetComponentInChildren<InteractableEgg>();
            if (egg != null)
            {
                egg.done = true;
                egg.breakFx.SetActive(true);
                interactable.gameObject.SetActive(false);
                return false;
            }

            return true;

        }

        private static bool CanSynchronize(DetectInteractables __instance)
        {
            if (!__instance.CanInteract() || !__instance.currentInteractable.CanInteract())
            {
                Plugin.Log.LogWarning($"Cant interact with {__instance?.currentInteractable}");
                return false;
            }

            var microwave = __instance.currentInteractable.GetComponentInChildren<InteractableMicrowave>();
            if (microwave != null)
            {
                // hasItem first, deliberately. Collecting an already-cooked item charges nothing —
                // InteractableMicrowave.Interact()'s price and rarity guards are both on the
                // !hasItem branch only (CONFIRMED by decompiling it, see MicrowaveHelper). The gold
                // check used to run above this and blocked a broke player from collecting their own
                // cooked item, so nothing was sent and the other peer never saw the interaction.
                if (microwave.hasItem)
                {
                    Plugin.Log.LogDebug($"Microwave already has an item!");
                    return true;
                }

                if (!MicrowaveHelper.CanAfford(microwave))
                {
                    Plugin.Log.LogDebug($"Not enough gold to interact with microwave! Required: {microwave.GetPrice()}, Current: {GameManager.Instance.player.inventory.gold}");
                    return false;
                }

                if (!MicrowaveHelper.HasEnoughItemsInRarity(microwave))
                {
                    Plugin.Log.LogDebug($"Not enough items of rarity {microwave.rarity} to interact with microwave");
                    return false;
                }
            }

            var chest = __instance.currentInteractable.GetComponentInChildren<InteractableChest>();
            if (chest != null)
            {
                if (!chest.CanAfford())
                {
                    return false;
                }
            }

            var portal = __instance.currentInteractable.GetComponentInChildren<InteractablePortal>();
            if (portal != null)
            {
                if (Plugin.Instance.IS_MANUAL_INVINCIBLE)
                {
                    Plugin.Instance.IS_MANUAL_INVINCIBLE = false;
                    GameManager.Instance.player.isTeleporting = false;
                    return true;
                }

                if (GameManager.Instance.player.isTeleporting)
                {
                    return false;
                }
            }

            var finalPortal = __instance.currentInteractable.GetComponentInChildren<InteractableBossSpawnerFinal>();
            if (finalPortal != null)
            {
                if (Plugin.Instance.IS_MANUAL_INVINCIBLE)
                {
                    Plugin.Instance.IS_MANUAL_INVINCIBLE = false;
                    GameManager.Instance.player.isTeleporting = false;
                    return true;
                }

                if (GameManager.Instance.player.isTeleporting)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

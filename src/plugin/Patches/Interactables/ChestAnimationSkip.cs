using Assets.Scripts.Inventory__Items__Pickups.Chests;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Utility;
using HarmonyLib;
using Microsoft.Extensions.DependencyInjection;
using OmniBonk.Services;
using UnityEngine;

namespace OmniBonk.Patches.Interactables
{
    /// <summary>
    /// OB-65: the lobby's Skip Chest Animation. The game reads <c>CFGameSettings.skip_chest_animation</c>
    /// in exactly these three methods (decompiled: <c>OpenChest.OnTriggerStay</c> 0x18046B7F0,
    /// <c>InteractableChest.Interact</c> 0x180452FB0, <c>InteractableChest.OpenChestImplementation</c>
    /// 0x180453990); 1 calls <c>ChestUtility.OpenChestNoAnimation</c> instead of opening a chest window.
    /// Each patch sets the session's value for the length of the call and puts the player's own back,
    /// so the saved setting is never changed.
    /// </summary>
    [HarmonyPatch]
    internal static class ChestAnimationSkipPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly ILobbySettingsService lobbySettingsService = Plugin.Services.GetService<ILobbySettingsService>();

        /// <summary>Per physics step while anything touches a dropped chest: cheapest checks first.</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(OpenChest), nameof(OpenChest.OnTriggerStay))]
        public static void OpenChest_Prefix(Collider other, OpenChest __instance, out int __state)
        {
            __state = -1;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (__instance.pickedup || __instance.readyForPickupTime > MyTime.time)
            {
                return;
            }

            if (other != GameManager.Instance.player.GetComponent<Collider>())
            {
                return;
            }

            __state = Override();
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(OpenChest), nameof(OpenChest.OnTriggerStay))]
        public static void OpenChest_Finalizer(int __state) => Restore(__state);

        /// <summary>Runs for the opener and, under Shared XP, on every other peer (InteractableUsed).</summary>
        [HarmonyPrefix, HarmonyPatch(typeof(InteractableChest), nameof(InteractableChest.Interact))]
        public static void Interact_Prefix(out int __state)
            => __state = synchronizationService.HasNetplaySessionStarted() ? Override() : -1;

        [HarmonyFinalizer, HarmonyPatch(typeof(InteractableChest), nameof(InteractableChest.Interact))]
        public static void Interact_Finalizer(int __state) => Restore(__state);

        [HarmonyPrefix, HarmonyPatch(typeof(InteractableChest), "OpenChestImplementation")]
        public static void OpenChestImplementation_Prefix(out int __state)
            => __state = synchronizationService.HasNetplaySessionStarted() ? Override() : -1;

        [HarmonyFinalizer, HarmonyPatch(typeof(InteractableChest), "OpenChestImplementation")]
        public static void OpenChestImplementation_Finalizer(int __state) => Restore(__state);

        private static int skippedChests;
        private static float nextSkipLogTime;

        /// <summary>Counts chests opened with no window on this peer; logged at most every 30 s.</summary>
        [HarmonyPostfix, HarmonyPatch(typeof(ChestUtility), nameof(ChestUtility.OpenChestNoAnimation))]
        public static void OpenChestNoAnimation_Postfix()
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            skippedChests++;
            if (Time.unscaledTime < nextSkipLogTime)
            {
                return;
            }

            nextSkipLogTime = Time.unscaledTime + 30f;
            Plugin.Log.LogInfo($"[chest-skip] since launch: {skippedChests} chest(s) opened here with no window.");
        }

        /// <summary>Returns the player's own value to restore, or -1 when it already matched.</summary>
        private static int Override()
        {
            var settings = SaveManager.Instance?.config?.cfGameSettings;
            if (settings == null)
            {
                return -1;
            }

            var own = settings.skip_chest_animation;
            var wanted = lobbySettingsService.SkipsChestAnimation() ? 1 : 0;
            if (own == wanted)
            {
                return -1;
            }

            settings.skip_chest_animation = wanted;
            return own;
        }

        private static void Restore(int own)
        {
            if (own < 0)
            {
                return;
            }

            var settings = SaveManager.Instance?.config?.cfGameSettings;
            if (settings != null)
            {
                settings.skip_chest_animation = own;
            }
        }
    }
}

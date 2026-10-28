using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches.Interactables
{
    [HarmonyPatch(typeof(InteractableShadyGuy))]
    internal static class InteractableShadyGuyPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly ISpawnedObjectManagerService spawnedObjectManagerService = Plugin.Services.GetService<ISpawnedObjectManagerService>();

        /// <summary>Client uses the host's rarity.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(InteractableShadyGuy.Start))]
        public static void Start_Prefix(InteractableShadyGuy __instance)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;

            if (isServer) return;

            var rarity = DynamicData.For(__instance).Get<EItemRarity?>("rarity");
            if (rarity.HasValue)
            {
                spawnedObjectManagerService.AddShadyGuyRarityRequest(rarity.Value);
            }

            return;
        }


        /// <summary>Host sends it from Start, once rarity is set.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(InteractableShadyGuy.Start))]
        public static void Start_Postfix(InteractableShadyGuy __instance)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                synchronizationService.OnSpawnedObject(__instance.gameObject);
            }
        }

        /// <summary>
        /// OB-67: <c>OnShadyGuyDone</c> runs on every Shady Guy when anyone's purchase finishes, but
        /// marks only the one this peer was shopping at (decompiled 0x1804CD5F0). Captures the flag so the
        /// postfix sends only on the real transition.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("OnShadyGuyDone")]
        public static void OnShadyGuyDone_Prefix(InteractableShadyGuy __instance, out bool __state)
            => __state = synchronizationService.HasNetplaySessionStarted() && __instance.done;

        /// <summary>Local purchase only: a replicated one never calls this method.</summary>
        [HarmonyPostfix]
        [HarmonyPatch("OnShadyGuyDone")]
        public static void OnShadyGuyDone_Postfix(InteractableShadyGuy __instance, bool __state)
        {
            if (!synchronizationService.HasNetplaySessionStarted() || !Plugin.CAN_SEND_MESSAGES)
            {
                return;
            }

            if (!__state && __instance.done)
            {
                synchronizationService.OnShadyGuyDone(__instance);
            }
        }
    }
}

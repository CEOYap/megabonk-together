using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Managers;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(WeaponInventory))]
    public static class WeaponInventoryPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// OB-19. Latched so one bad add reports once rather than once per weapon returned — the
        /// final boss hands several back at a time and a per-add line would bury the answer.
        /// </summary>
        private static bool hasReportedForeignInventory = false;

        /// <summary>
        /// Synchronizes weapon additions in netplay
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponInventory.AddWeapon))]
        public static void AddWeapon_Post(WeaponInventory __instance, WeaponData weaponData, List<StatModifier> upgradeOffer)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return;
            }

            // OB-19, diagnostic only -- this does NOT change behaviour yet, deliberately.
            //
            // The message this postfix sends is stamped OwnerId = local player, whatever inventory
            // __instance actually is, and remote players hold real WeaponInventory objects on this
            // machine. So an add on somebody else's inventory would be broadcast as though the local
            // player had gained that weapon. The hole is proven by reading OnWeaponAdded; what is
            // not proven is that anything ever reaches here with a foreign inventory.
            //
            // The final-boss weapon return is the reported case, so play to it: a single line below
            // joins the hole to the symptom, and its absence says the cause is elsewhere and an
            // ownership check would have been a fix for nothing.
            ReportIfForeignInventory(__instance);

            synchronizationService.OnWeaponAdded(__instance, weaponData, upgradeOffer);

        }

        /// <summary>
        /// OB-19. Says whether this add was on an inventory that is not the local player's.
        ///
        /// <para><b>Compares references, not types.</b> Both the local and a replicated inventory are
        /// <c>WeaponInventory</c>, so only identity distinguishes them. Il2CppInterop proxies compare
        /// by the underlying native pointer, which is what makes this meaningful at all — and the
        /// reason it does not use <c>is</c> or <c>as</c>, which test the managed wrapper.</para>
        /// </summary>
        private static void ReportIfForeignInventory(WeaponInventory instance)
        {
            if (hasReportedForeignInventory || instance == null)
            {
                return;
            }

            var localInventory = GameManager.Instance?.player?.inventory?.weaponInventory;
            if (localInventory == null)
            {
                // Cannot tell, so claim nothing. A missing local inventory is its own oddity but not
                // evidence either way about this one.
                return;
            }

            if (instance.Pointer == localInventory.Pointer)
            {
                return;
            }

            hasReportedForeignInventory = true;

            var owner = playerManagerService.GetAllSpawnedNetPlayers()
                .FirstOrDefault(np => np != null
                                      && np.Inventory != null
                                      && np.Inventory.weaponInventory != null
                                      && np.Inventory.weaponInventory.Pointer == instance.Pointer);

            Plugin.Log.LogWarning(
                "[weapon-owner] OB-19 CONFIRMED: AddWeapon fired on an inventory that is NOT the " +
                $"local player's{(owner != null ? $" (it belongs to peer {owner.ConnectionId})" : " (owner unidentified)")}, " +
                "and the broadcast stamps it as the local player's weapon. Reported once per " +
                "session. See OB-19 in docs/netplay/08-observed-bugs.md.");
        }

        /// <summary>OB-19. Re-arms the one-shot report so each session can produce its own.</summary>
        public static void ResetForeignInventoryReport()
        {
            hasReportedForeignInventory = false;
        }

        /// <summary>
        /// Prevent weapons tick on client (Server side only)
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(WeaponInventory.Tick))]
        public static bool Tick_Prefix(WeaponInventory __instance)
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

            return true;
        }

        /// <summary>
        /// Synchronizes weapon toggles
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponInventory.ToggleWeapon))]
        public static void ToggleWeapon_Postfix(WeaponInventory __instance, EWeapon eWeapon, bool enable)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return;
            }

            synchronizationService.OnWeaponToggled(__instance, eWeapon, enable);
        }
    }
}

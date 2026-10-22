using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Managers;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(WeaponInventory))]
    public static class WeaponInventoryPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>OB-19: report once per session.</summary>
        private static bool hasReportedForeignInventory = false;

        /// <summary>Synchronizes weapon additions in netplay</summary>
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

            // OB-19, diagnostic only: the send is stamped with the local owner even for a remote
            // player's inventory. Logs if that ever happens; behaviour unchanged.
            ReportIfForeignInventory(__instance);

            synchronizationService.OnWeaponAdded(__instance, weaponData, upgradeOffer);

        }

        /// <summary>OB-19. Compares by native pointer identity; <c>is</c>/<c>as</c> test the managed wrapper.</summary>
        private static void ReportIfForeignInventory(WeaponInventory instance)
        {
            if (hasReportedForeignInventory || instance == null)
            {
                return;
            }

            var localInventory = GameManager.Instance?.player?.inventory?.weaponInventory;
            if (localInventory == null)
            {
                // Cannot tell; claim nothing.
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

        /// <summary>OB-19.</summary>
        public static void ResetForeignInventoryReport()
        {
            hasReportedForeignInventory = false;
        }

        /// <summary>Prevent weapons tick on client (Server side only)</summary>
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

        /// <summary>Synchronizes weapon toggles</summary>
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

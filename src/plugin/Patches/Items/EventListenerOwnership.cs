using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive.Implementations;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.WeaponPassives;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Items
{
    /// <summary>
    /// OB-39: listeners on the static enemy-hit/death events fired for every player's copy (OB-14),
    /// applying effects to the local player. Now each runs once, for its owner's event:
    /// <list type="bullet">
    /// <item><b>Owner's machine</b> for player-side effects (heal, self-damage, stat stacks):
    /// Lifesteal, Demon Blade, Kevin, Demonic Soul/Blood.</item>
    /// <item><b>Host</b> for simulation effects (damage, projectiles, pickups): Joe's Dagger, Borgor,
    /// Soul Harvester, Zap, Plague, Bullseye, Blood Magic.</item>
    /// </list>
    /// <para>Classification from decompiled bodies. <b>UNVERIFIED.</b> Not run in-game.</para>
    /// </summary>
    internal static class EventListenerOwnership
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        private static bool InSession => synchronizationService.HasNetplaySessionStarted();

        private static bool IsHost => synchronizationService.IsServerMode() ?? false;

        private static uint? LocalId => playerManagerService.GetLocalPlayer()?.ConnectionId;

        /// <summary>Who made the hit or kill being handled. On the host, an unscoped hit is the host's.</summary>
        private static uint? Actor() => trackerService.GetCurrentPlayerId() ?? (IsHost ? LocalId : null);

        /// <summary>Local copy only, for the local player's own event.</summary>
        internal static bool RunOnOwnerMachine(bool isLocalCopy)
        {
            if (!InSession)
            {
                return true;
            }

            var local = LocalId;
            return isLocalCopy && local.HasValue && Actor() == local;
        }

        /// <summary>Host only, the copy belonging to whoever made the event.</summary>
        internal static bool RunOnHost(uint? copyOwner)
        {
            if (!InSession)
            {
                return true;
            }

            if (!IsHost)
            {
                return false;
            }

            var actor = Actor();
            return actor.HasValue && copyOwner.HasValue && actor.Value == copyOwner.Value;
        }

        internal static uint? OwnerOf(ItemBase item)
        {
            var inventory = item?.itemInventoryRef;
            if (inventory == null)
            {
                return null;
            }

            var local = GameManager.Instance?.player?.inventory?.itemInventory;
            if (local != null && local.Pointer == inventory.Pointer)
            {
                return LocalId;
            }

            return playerManagerService.GetItemInventoryOwnerId(inventory.Pointer);
        }

        internal static uint? OwnerOf(PassiveAbility passive)
        {
            if (passive == null)
            {
                return null;
            }

            var local = GameManager.Instance?.player?.inventory?.passiveAbility;
            if (local != null && local.Pointer == passive.Pointer)
            {
                return LocalId;
            }

            return playerManagerService.GetPassiveAbilityOwnerId(passive.Pointer);
        }

        internal static uint? OwnerOf(WeaponPassive passive)
        {
            var weapon = passive?.weaponBase;
            if (weapon == null)
            {
                return null;
            }

            var localWeapons = GameManager.Instance?.player?.inventory?.weaponInventory?.weapons;
            if (localWeapons != null && localWeapons.ContainsValue(weapon))
            {
                return LocalId;
            }

            return playerManagerService.GetWeaponOwnerId(weapon);
        }

        internal static bool IsLocalCopy(ItemBase item)
        {
            var owner = OwnerOf(item);
            return owner.HasValue && owner == LocalId;
        }

        internal static bool IsLocalHealth(PlayerHealth health)
        {
            var local = GameManager.Instance?.player?.inventory?.playerHealth;
            return local != null && health != null && local.Pointer == health.Pointer;
        }
    }

    // Prefix-false throughout is deliberate: each original only applies the listener's effect.

    [HarmonyPatch(typeof(PlayerHealth))]
    internal static class PlayerHealthListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDamaged")]
        public static bool OnEnemyDamaged_Prefix(PlayerHealth __instance)
            => EventListenerOwnership.RunOnOwnerMachine(EventListenerOwnership.IsLocalHealth(__instance));

        [HarmonyPrefix]
        [HarmonyPatch("TryLifestealHit")]
        public static bool TryLifestealHit_Prefix(PlayerHealth __instance)
            => EventListenerOwnership.RunOnOwnerMachine(EventListenerOwnership.IsLocalHealth(__instance));
    }

    [HarmonyPatch(typeof(ItemDemonBlade))]
    internal static class ItemDemonBladeListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDamaged")]
        public static bool Prefix(ItemDemonBlade __instance)
            => EventListenerOwnership.RunOnOwnerMachine(EventListenerOwnership.IsLocalCopy(__instance));
    }

    [HarmonyPatch(typeof(ItemKevin))]
    internal static class ItemKevinListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDamaged")]
        public static bool Prefix(ItemKevin __instance)
            => EventListenerOwnership.RunOnOwnerMachine(EventListenerOwnership.IsLocalCopy(__instance));
    }

    [HarmonyPatch(typeof(ItemDemonicSoul))]
    internal static class ItemDemonicSoulListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDied")]
        public static bool Prefix(ItemDemonicSoul __instance)
            => EventListenerOwnership.RunOnOwnerMachine(EventListenerOwnership.IsLocalCopy(__instance));
    }

    [HarmonyPatch(typeof(ItemDemonicBlood))]
    internal static class ItemDemonicBloodListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDied")]
        public static bool Prefix(ItemDemonicBlood __instance)
            => EventListenerOwnership.RunOnOwnerMachine(EventListenerOwnership.IsLocalCopy(__instance));
    }

    [HarmonyPatch(typeof(ItemJoesDagger))]
    internal static class ItemJoesDaggerListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDamage")]
        public static bool Prefix(ItemJoesDagger __instance)
            => EventListenerOwnership.RunOnHost(EventListenerOwnership.OwnerOf(__instance));
    }

    [HarmonyPatch(typeof(ItemBorgor))]
    internal static class ItemBorgorListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDied")]
        public static bool Prefix(ItemBorgor __instance)
            => EventListenerOwnership.RunOnHost(EventListenerOwnership.OwnerOf(__instance));
    }

    [HarmonyPatch(typeof(ItemSoulHarvester))]
    internal static class ItemSoulHarvesterListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDied")]
        public static bool Prefix(ItemSoulHarvester __instance)
            => EventListenerOwnership.RunOnHost(EventListenerOwnership.OwnerOf(__instance));
    }

    [HarmonyPatch(typeof(PassiveAbilityZooma))]
    internal static class PassiveAbilityZoomaListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDamage")]
        public static bool Prefix(PassiveAbilityZooma __instance)
            => EventListenerOwnership.RunOnHost(EventListenerOwnership.OwnerOf(__instance));
    }

    [HarmonyPatch(typeof(PassiveAbilityPlague))]
    internal static class PassiveAbilityPlagueListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDied")]
        public static bool Prefix(PassiveAbilityPlague __instance)
            => EventListenerOwnership.RunOnHost(EventListenerOwnership.OwnerOf(__instance));
    }

    [HarmonyPatch(typeof(PassiveAbilityBullseye))]
    internal static class PassiveAbilityBullseyeListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDamaged")]
        public static bool Prefix(PassiveAbilityBullseye __instance)
            => EventListenerOwnership.RunOnHost(EventListenerOwnership.OwnerOf(__instance));
    }

    [HarmonyPatch(typeof(WeaponPassiveBloodMagic))]
    internal static class WeaponPassiveBloodMagicListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDied")]
        public static bool Prefix(WeaponPassiveBloodMagic __instance)
            => EventListenerOwnership.RunOnHost(EventListenerOwnership.OwnerOf(__instance));
    }
}

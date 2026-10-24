using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive.Implementations;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.WeaponPassives;
using Assets.Scripts.Actors.Enemies;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

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

        // Host only: who killed each enemy, by Unity instance id. Pool reuse overwrites an entry at
        // the next death, so nothing is removed until the session resets.
        private static readonly Dictionary<int, uint> killers = [];

        internal static void ResetKillers() => killers.Clear();

        /// <summary>Called as an enemy dies, inside the kill's attribution scope.</summary>
        internal static void NoteKiller(Enemy enemy)
        {
            if (!InSession || !IsHost || enemy == null)
            {
                return;
            }

            var actor = Actor();
            if (actor.HasValue)
            {
                killers[enemy.GetInstanceID()] = actor.Value;
            }
        }

        /// <summary>
        /// <see cref="RunOnHost"/> for a listener that fires after the kill's scope has closed, using the
        /// killer noted at death. <paramref name="killer"/> is set when it ran for a recorded kill.
        /// </summary>
        internal static bool RunOnHostForKill(uint? copyOwner, Enemy enemy, out uint? killer)
        {
            killer = null;
            if (!InSession)
            {
                return true;
            }

            if (!IsHost)
            {
                return false;
            }

            if (enemy == null || !killers.TryGetValue(enemy.GetInstanceID(), out var noted))
            {
                // Released without a recorded death (despawned): the original rule.
                return RunOnHost(copyOwner);
            }

            if (!copyOwner.HasValue || copyOwner.Value != noted)
            {
                return false;
            }

            killer = noted;
            return true;
        }

        /// <summary>
        /// OB-57. A listener on one of the local player's own events (evade, damage taken): only the
        /// local player's passive runs. There is no hit to attribute, so this does not go through
        /// <see cref="Actor"/>, which is null on a client outside a replayed hit.
        /// </summary>
        internal static bool RunForLocalPlayerEvent(PassiveAbility passive)
            => !InSession || IsLocalPassive(passive);

        /// <summary>
        /// The local player's own passive, by pointer. Not via <see cref="LocalId"/>: a null id
        /// would turn off the owner's own passive rather than only the teammates' copies.
        /// </summary>
        internal static bool IsLocalPassive(PassiveAbility passive)
        {
            var local = GameManager.Instance?.player?.inventory?.passiveAbility;
            return local != null && passive != null && local.Pointer == passive.Pointer;
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

    /// <summary>
    /// Despite its name, <c>OnEnemyDied</c> is subscribed to <c>Enemy.A_EnemyReleasedFromPool</c>
    /// (decompiled <c>Init</c> 0x180465A20 writes static offset 0x20; dump.cs names 0x20), so it runs when
    /// the corpse returns to the pool, after the kill's scope has closed. With <see cref="EventListenerOwnership.RunOnHost"/>
    /// every death looked like the host's and a teammate's copy never fired. The call is scoped to the
    /// noted killer so the souls are shared as theirs.
    /// </summary>
    [HarmonyPatch(typeof(ItemSoulHarvester))]
    internal static class ItemSoulHarvesterListenerPatches
    {
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDied")]
        public static bool Prefix(ItemSoulHarvester __instance, Enemy enemy, out uint? __state)
        {
            __state = null;
            if (!EventListenerOwnership.RunOnHostForKill(EventListenerOwnership.OwnerOf(__instance), enemy, out var killer))
            {
                return false;
            }

            if (killer.HasValue && trackerService.GetCurrentPlayerId() == null)
            {
                trackerService.SetCurrentPlayerId(killer.Value);
                __state = killer;
            }

            return true;
        }

        [HarmonyFinalizer]
        [HarmonyPatch("OnEnemyDied")]
        public static void Finalizer(uint? __state)
        {
            if (__state.HasValue)
            {
                trackerService.UnsetCurrentPlayerId();
            }
        }
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

    /// <summary>
    /// OB-57. <c>OnEvade</c> (VA 0x18047D210) is on the static <c>PlayerHealth.A_Evaded</c>, so a
    /// teammate's copy executed enemies on this machine's evades. It damages the evaded enemy with
    /// its current HP (a boss: a wave-scaled amount) through <c>DamageFromPlayerOther</c>, source
    /// <c>"Shadowstep"</c>. The owner's own execute reaches the host through
    /// <c>EnemyPatch.AllowedDamageSource</c>.
    /// </summary>
    [HarmonyPatch(typeof(PassiveAbilityShadowstep))]
    internal static class PassiveAbilityShadowstepListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnEvade")]
        public static bool Prefix(PassiveAbilityShadowstep __instance)
            => EventListenerOwnership.RunForLocalPlayerEvent(__instance);
    }

    /// <summary>
    /// OB-57. <c>OnCheckStopDamage</c> (VA 0x180477FA0) is on the static
    /// <c>PlayerHealth.A_CheckStopDamage</c>. When the shared static cooldown is up it zeroes the
    /// local player's damage and calls <c>UseFlex</c> (0x180478340): that copy gains a stack and a
    /// damage stat, hits enemies around the local player (source <c>"Flex"</c>), and starts the
    /// cooldown for every copy. So a teammate's copy blocked our hits and took the cooldown from
    /// our own Flex.
    /// </summary>
    [HarmonyPatch(typeof(PassiveAbilityFlex))]
    internal static class PassiveAbilityFlexListenerPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch("OnCheckStopDamage")]
        public static bool Prefix(PassiveAbilityFlex __instance)
            => EventListenerOwnership.RunForLocalPlayerEvent(__instance);
    }
}

using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive.Implementations;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.WeaponPassives;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Items
{
    /// <summary>
    /// OB-39. Item, passive and weapon-passive listeners on the global enemy-hit and enemy-death
    /// events react only to their owner's hits and kills, on the one machine that should apply the
    /// effect.
    ///
    /// <para><b>The defect.</b> These are static game events (<c>Enemy</c>'s damage and death
    /// actions), raised on a machine for every hit and kill applied there, whoever made it. Each
    /// machine holds a live copy of every player's items and passive (OB-14, RemoteItemCopies), so
    /// every copy reacted to every player's events, and the effect went to the local player:</para>
    /// <list type="bullet">
    /// <item><b>Lifesteal</b> (<c>PlayerHealth.OnEnemyDamaged</c>, VA 0x180429320, and
    /// <c>TryLifestealHit</c>, 0x18042A330): rolls the local Lifesteal stat and heals the local
    /// player. The host healed off everyone's hits; each client off every replayed hit.</item>
    /// <item><b>Demon Blade</b> (heal on crit, 0x1804409B0), <b>Kevin</b> (counts hits, then damages
    /// its owner, 0x18045F900), <b>Demonic Soul</b> and <b>Demonic Blood</b> (kill stacks,
    /// 0x180454910 / 0x180454280): the same, with stacks and heals.</item>
    /// <item><b>Joe's Dagger</b> (stacks its pre-attack bonus, 0x18045ED60), <b>Borgor</b> (drops a
    /// burger, 0x18043BCE0), <b>Soul Harvester</b> (death projectile, 0x180465B70), the <b>Zap</b>,
    /// <b>Plague</b> and <b>Bullseye</b> passives (0x1804800F0, 0x18047B730, 0x180474EA0) and
    /// <b>Blood Magic</b> (weapon stacks, 0x1804334C0): every copy fired, so these hit or stacked
    /// once per player per event, and on clients the damage applied there only.</item>
    /// </list>
    ///
    /// <para><b>Two rules,</b> by where the effect lives:</para>
    /// <list type="bullet">
    /// <item><b>Owner's machine</b> (the effect is on a player: heal, self-damage, stat stacks): only
    /// the local player's copy runs, and only for its own hit or kill. Clients own their HP and
    /// their stats travel to the host, so this is the one place it is real.</item>
    /// <item><b>Host</b> (the effect is in the simulation: damage, a projectile, a pickup, stacks
    /// that feed a hit the host builds): only on the host, and only the copy belonging to whoever
    /// made the hit or kill.</item>
    /// </list>
    /// <para>"Whose hit" is the tracker: on the host the attacker the hit was scoped to (null is the
    /// host's own), on a client the replay's <c>AttackerId</c> or the death's
    /// <c>DiedByOwnerId</c>.</para>
    ///
    /// <para><b>UNVERIFIED.</b> Not run in-game. The classification of each listener comes from
    /// reading its decompiled body; an item that also does something not listed here would need
    /// another look.</para>
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

        /// <summary>A player-side effect: the local copy only, for the local player's own event.</summary>
        internal static bool RunOnOwnerMachine(bool isLocalCopy)
        {
            if (!InSession)
            {
                return true;
            }

            var local = LocalId;
            return isLocalCopy && local.HasValue && Actor() == local;
        }

        /// <summary>A simulation effect: on the host, the copy belonging to whoever made the event.</summary>
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

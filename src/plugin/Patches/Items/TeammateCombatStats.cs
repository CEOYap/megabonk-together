using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using Assets.Scripts.Menu.Shop;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace OmniBonk.Patches.Items
{
    /// <summary>
    /// OB-38: a teammate's hit, built on the host, reads the teammate's combat stats. Decompiled:
    /// the static <c>PlayerStats.GetStat</c> always answers for the local player.
    /// <para>Only <see cref="CombatStats"/> are substituted, and only while a teammate's weapon scope or
    /// tracker scope is active. <c>GetNewDamage</c> with item modifiers is rescaled by the DamageMultiplier ratio.</para>
    /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
    /// </summary>
    internal static class TeammateCombatStats
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();
        private static readonly IPlayerStatsSyncService playerStatsSyncService = Plugin.Services.GetService<IPlayerStatsSyncService>();

        /// <summary>The stats a hit is built from, as EStat ints. Must stay inside TrackedStats.</summary>
        internal static readonly HashSet<int> CombatStats =
        [
            (int)EStat.DamageMultiplier,
            (int)EStat.EffectDurationMultiplier,
            (int)EStat.CritChance,
            (int)EStat.CritDamage,
            (int)EStat.FireDamage,
            (int)EStat.IceDamage,
            (int)EStat.LightningDamage,
            (int)EStat.EliteDamageMultiplier,
            (int)EStat.KnockbackMultiplier,
            (int)EStat.BurnChance,
            (int)EStat.FreezeChance,
            (int)EStat.PoisonDamageMultiplier,

            // OB-43: read by on-hit items and GetWeaponDamage; consistent with the OB-1 corrections.
            (int)EStat.Thorns,
            (int)EStat.SizeMultiplier,
            (int)EStat.DurationMultiplier,
            (int)EStat.ProjectileSpeedMultiplier,
        ];

        // Active with a null owner = host's own weapon, overriding a surrounding tracker scope.
        private static bool weaponScopeActive;
        private static uint? weaponScopeOwner;

        /// <summary>Set while reading the host's own value on purpose.</summary>
        internal static bool Bypass;

        private static bool loggedSubstitution = false;

        internal readonly struct WeaponScope(bool active, uint? owner)
        {
            public bool Active { get; } = active;
            public uint? Owner { get; } = owner;
        }

        internal static WeaponScope EnterWeapon(WeaponBase weaponBase)
        {
            var saved = new WeaponScope(weaponScopeActive, weaponScopeOwner);
            if (!IsHostSession())
            {
                return saved;
            }

            weaponScopeActive = true;
            weaponScopeOwner = weaponBase == null ? null : playerManagerService.GetWeaponOwnerId(weaponBase);
            return saved;
        }

        internal static void ExitWeapon(WeaponScope saved)
        {
            weaponScopeActive = saved.Active;
            weaponScopeOwner = saved.Owner;
        }

        /// <summary>Host only: the teammate whose hit is in scope, or null.</summary>
        internal static uint? CurrentTeammate()
        {
            uint? owner;
            if (weaponScopeActive)
            {
                owner = weaponScopeOwner;
            }
            else
            {
                owner = trackerService.GetCurrentPlayerId();
            }

            if (!owner.HasValue || !IsHostSession() || playerManagerService.IsLocalConnectionId(owner.Value))
            {
                return null;
            }

            return owner;
        }

        internal static float? RemoteStat(uint owner, int stat) => playerStatsSyncService.GetRemoteStat(owner, stat);

        internal static void NoteSubstitution(uint owner)
        {
            if (loggedSubstitution)
            {
                return;
            }

            loggedSubstitution = true;
            Plugin.Log.LogInfo($"[stats] OB-38: building {owner}'s hit from their own combat stats (logged once per session).");
        }

        internal static void ResetDiagnostics() => loggedSubstitution = false;

        /// <summary>Swaps the raw <c>MyPlayer.baseDamage</c> field; returns the value to restore, or null.</summary>
        internal static float? EnterBaseDamage(uint owner)
        {
            var me = MyPlayer.Instance;
            var theirs = RemoteStat(owner, PlayerStatsSyncService.BaseDamageKey);
            if (me == null || !theirs.HasValue)
            {
                return null;
            }

            var saved = me.baseDamage;
            me.baseDamage = theirs.Value;
            return saved;
        }

        internal static void ExitBaseDamage(float? saved)
        {
            var me = MyPlayer.Instance;
            if (saved.HasValue && me != null)
            {
                me.baseDamage = saved.Value;
            }
        }

        private static bool IsHostSession()
            => synchronizationService.HasNetplaySessionStarted() && (synchronizationService.IsServerMode() ?? false);
    }

    /// <summary>
    /// OB-43: "act as this teammate" via the position-request queue. Only pushes when the queue is empty.
    /// </summary>
    internal static class PositionRequest
    {
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        internal static bool Enter(uint owner)
        {
            if (playerManagerService.PeakNetplayerPositionRequest().HasValue)
            {
                return false;
            }

            playerManagerService.AddGetNetplayerPositionRequest(owner);
            return true;
        }

        internal static void Exit(bool pushed)
        {
            if (pushed)
            {
                playerManagerService.UnqueueNetplayerPositionRequest();
            }
        }
    }

    /// <summary>
    /// OB-43: Spicy Meatball and Quin's Mask cache Size in OnInitOrAmountChanged (decompiled); run it as the owner.
    /// </summary>
    internal static class TeammateItemInit
    {
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        internal readonly struct Scope(bool tracker, bool position)
        {
            public bool Tracker { get; } = tracker;
            public bool Position { get; } = position;
        }

        internal static Scope Enter(Assets.Scripts.Inventory__Items__Pickups.Items.ItemBase item)
        {
            if (!RemoteItemCopies.IsTeammateCopy(item))
            {
                return default;
            }

            var owner = EventListenerOwnership.OwnerOf(item);
            if (!owner.HasValue)
            {
                return default;
            }

            var setTracker = !trackerService.GetCurrentPlayerId().HasValue;
            if (setTracker)
            {
                trackerService.SetCurrentPlayerId(owner.Value);
            }

            return new Scope(setTracker, PositionRequest.Enter(owner.Value));
        }

        internal static void Exit(Scope scope)
        {
            PositionRequest.Exit(scope.Position);
            if (scope.Tracker)
            {
                trackerService.UnsetCurrentPlayerId();
            }
        }
    }

    [HarmonyPatch(typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations.ItemSpicyMeatball))]
    internal static class ItemSpicyMeatballInitPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnInitOrAmountChanged")]
        public static void Prefix(Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations.ItemSpicyMeatball __instance, out TeammateItemInit.Scope __state)
            => __state = TeammateItemInit.Enter(__instance);

        [HarmonyFinalizer, HarmonyPatch("OnInitOrAmountChanged")]
        public static void Finalizer(TeammateItemInit.Scope __state) => TeammateItemInit.Exit(__state);
    }

    [HarmonyPatch(typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations.ItemQuinsMask))]
    internal static class ItemQuinsMaskInitPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnInitOrAmountChanged")]
        public static void Prefix(Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations.ItemQuinsMask __instance, out TeammateItemInit.Scope __state)
            => __state = TeammateItemInit.Enter(__instance);

        [HarmonyFinalizer, HarmonyPatch("OnInitOrAmountChanged")]
        public static void Finalizer(TeammateItemInit.Scope __state) => TeammateItemInit.Exit(__state);
    }

    [HarmonyPatch(typeof(PlayerStats))]
    internal static class PlayerStatsTeammatePatches
    {
        // Hot path: cheapest checks first. No netplay guard needed: CurrentTeammate is null outside a host session.
        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerStats.GetStat))]
        public static void GetStat_Postfix(EStat stat, ref float __result)
        {
            if (TeammateCombatStats.Bypass)
            {
                return;
            }

            var owner = TeammateCombatStats.CurrentTeammate();
            if (!owner.HasValue || !TeammateCombatStats.CombatStats.Contains((int)stat))
            {
                return;
            }

            var theirs = TeammateCombatStats.RemoteStat(owner.Value, (int)stat);
            if (theirs.HasValue)
            {
                __result = theirs.Value;
                TeammateCombatStats.NoteSubstitution(owner.Value);
            }
        }
    }

    [HarmonyPatch(typeof(WeaponUtility))]
    internal static class WeaponUtilityTeammatePatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("GetNewDamage")]
        public static void GetNewDamage_Postfix(StatComponents itemModifierStatComponents, ref float __result)
        {
            if (itemModifierStatComponents == null || !itemModifierStatComponents.hasModifications)
            {
                return; // That path reads GetStat, which the postfix above already corrected.
            }

            var owner = TeammateCombatStats.CurrentTeammate();
            if (!owner.HasValue)
            {
                return;
            }

            var theirs = TeammateCombatStats.RemoteStat(owner.Value, (int)EStat.DamageMultiplier);
            if (!theirs.HasValue)
            {
                return;
            }

            TeammateCombatStats.Bypass = true;
            float ours;
            try
            {
                ours = PlayerStats.GetStat(EStat.DamageMultiplier);
            }
            finally
            {
                TeammateCombatStats.Bypass = false;
            }

            if (ours > 0f)
            {
                __result *= theirs.Value / ours;
            }
        }

        // Weapon-argument scopes: these accessors can run before any tracker scope exists.

        [HarmonyPrefix]
        [HarmonyPatch(nameof(WeaponUtility.GetDamage), new[] { typeof(WeaponBase) })]
        public static void GetDamage_Prefix(WeaponBase weaponBase, out TeammateCombatStats.WeaponScope __state)
            => __state = TeammateCombatStats.EnterWeapon(weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(WeaponUtility.GetDamage), new[] { typeof(WeaponBase) })]
        public static void GetDamage_Finalizer(TeammateCombatStats.WeaponScope __state)
            => TeammateCombatStats.ExitWeapon(__state);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(WeaponUtility.GetCritChance))]
        public static void GetCritChance_Prefix(WeaponBase weaponBase, out TeammateCombatStats.WeaponScope __state)
            => __state = TeammateCombatStats.EnterWeapon(weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(WeaponUtility.GetCritChance))]
        public static void GetCritChance_Finalizer(TeammateCombatStats.WeaponScope __state)
            => TeammateCombatStats.ExitWeapon(__state);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(WeaponUtility.GetCritDamageMultiplier))]
        public static void GetCritDamageMultiplier_Prefix(WeaponBase weaponBase, out TeammateCombatStats.WeaponScope __state)
            => __state = TeammateCombatStats.EnterWeapon(weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(WeaponUtility.GetCritDamageMultiplier))]
        public static void GetCritDamageMultiplier_Finalizer(TeammateCombatStats.WeaponScope __state)
            => TeammateCombatStats.ExitWeapon(__state);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(WeaponUtility.GetKnockback))]
        public static void GetKnockback_Prefix(WeaponBase weaponBase, out TeammateCombatStats.WeaponScope __state)
            => __state = TeammateCombatStats.EnterWeapon(weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(WeaponUtility.GetKnockback))]
        public static void GetKnockback_Finalizer(TeammateCombatStats.WeaponScope __state)
            => TeammateCombatStats.ExitWeapon(__state);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(WeaponUtility.GetDamageProjectile))]
        public static void GetDamageProjectile_Prefix(ProjectileBase projectile, out TeammateCombatStats.WeaponScope __state)
            => __state = TeammateCombatStats.EnterWeapon(projectile?.weaponBase);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(WeaponUtility.GetDamageProjectile))]
        public static void GetDamageProjectile_Finalizer(TeammateCombatStats.WeaponScope __state)
            => TeammateCombatStats.ExitWeapon(__state);
    }
}

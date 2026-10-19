using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using Assets.Scripts.Menu.Shop;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace MegabonkTogether.Patches.Items
{
    /// <summary>
    /// OB-38. While the host builds or applies a teammate's hit, the hit reads that teammate's
    /// combat stats instead of the host's.
    ///
    /// <para><b>The defect</b> (decompiled, build 21750826). Every stat read on a hit is the static
    /// <c>PlayerStats.GetStat(EStat)</c>, which takes no player and answers for the local one:
    /// <c>WeaponUtility.GetDamage</c> and <c>GetNewDamage</c> read DamageMultiplier (12),
    /// <c>GetDamageContainer</c> / <c>GetCritChance</c> / <c>GetCritDamageMultiplier</c> read 18 and
    /// 19, <c>GetKnockback</c> 24, <c>Enemy</c> EliteDamageMultiplier (23), the burn and poison
    /// debuffs 12 and 56, and on-hit items 9, 10 and 24. The host builds every teammate's hits, so
    /// all of a teammate's damage, crits and knockback came from the host's build. Several on-hit
    /// items and the burn debuff also read <c>MyPlayer.baseDamage</c> as a raw field.</para>
    ///
    /// <para><b>The fix.</b> The owner's values already travel in <c>PlayerStatsChanged</c>
    /// (OB-1); the combat stats and baseDamage were added to its keyed dictionary, so no message
    /// changed. A postfix on <c>GetStat</c> substitutes the teammate's value for the stats in
    /// <see cref="CombatStats"/> only, and only while a teammate's hit is in scope:</para>
    /// <list type="bullet">
    /// <item>a weapon accessor called with a teammate's weapon (the owner comes from the argument,
    /// so it holds where no tracker scope exists yet), or</item>
    /// <item>the tracker attributing the current hit to a teammate (projectile hits, auras, the
    /// teammate's on-hit items, their debuff ticks).</item>
    /// </list>
    /// <para>Other stats are never substituted: drop chances, gold, XP and the host's own movement
    /// keep reading the host. <c>GetNewDamage</c> with item modifiers reads the local player's
    /// stat components directly rather than through <c>GetStat</c>; that path is rescaled by the
    /// ratio of the two DamageMultipliers, which is exact for a multiplicative modifier and close
    /// for an additive one.</para>
    ///
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
        ];

        // A weapon accessor's owner, from its argument. Active with a null owner means "the
        // host's own weapon", which must win over a teammate tracker scope around it.
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

        /// <summary>
        /// Swaps <c>MyPlayer.baseDamage</c> to the teammate's for the duration of a call that reads
        /// it as a raw field. Returns the value to restore, or null when nothing was swapped.
        /// </summary>
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

    [HarmonyPatch(typeof(PlayerStats))]
    internal static class PlayerStatsTeammatePatches
    {
        // Hot path: the cheapest checks (the bypass flag, then whether any owner is in scope at
        // all) come first. Outside a teammate's hit this costs a field read and a null check.
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

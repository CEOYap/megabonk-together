using Assets.Scripts.Inventory.Stats;
using Assets.Scripts.Menu.Shop;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Weapons
{
    /// <summary>
    /// OB-1. Redraws a remote player's attack with the owner's globals instead of the observer's.
    ///
    /// <para><b>The defect.</b> Both accessors below have the shape
    /// <c>weaponStats[stat] + PlayerStats.GetStat(stat)</c> — decompiled at VA <c>0x1804346F0</c>
    /// and <c>0x1804347A0</c> — and <c>PlayerStats.GetStat</c> is static: it takes no player and
    /// reads whoever is asking. So a remote attack was built from their weapon and <i>your</i>
    /// globals. Shield orbit count, aura size, projectile count and projectile size were all this
    /// one bug.</para>
    ///
    /// <para><b>The correction is exact, not a re-implementation.</b> The result already contains
    /// the observer's contribution as a plain sum, so subtracting <c>PlayerStats.GetStat</c> and
    /// adding the owner's value yields precisely what the owner's machine computed — without
    /// needing the per-weapon half, which is not separately reachable. That matters: reproducing
    /// the formula would silently drift the moment the game changes it.</para>
    ///
    /// <para><b>Patched here rather than at each visual.</b> These two methods are where every
    /// affected visual converges, so one correction covers the ones that were reported and the ones
    /// that were only predicted. The opposite mistake — patching a funnel that carries more than
    /// intended — is what OB-16 cost, so both patches bail immediately unless the weapon belongs to
    /// a <i>remote</i> player.</para>
    ///
    /// <para><b>UNVERIFIED.</b> Not run in-game. Confirms as a teammate's aura and shield looking
    /// the same on both screens after one of you takes a Projectiles or Size upgrade — before that
    /// upgrade the two agree anyway, which is what made this look intermittent.</para>
    /// </summary>
    [HarmonyPatch(typeof(WeaponUtility))]
    internal static class WeaponUtilityPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IPlayerStatsSyncService playerStatsSyncService = Plugin.Services.GetService<IPlayerStatsSyncService>();

        /// <summary>EWeapon.Shotgun. GetAttackQuantity returns a hardcoded 2 for it, before any stat is read.</summary>
        private const int ShotgunEWeapon = 29;

        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponUtility.GetAttackQuantity))]
        public static void GetAttackQuantity_Postfix(WeaponBase weaponBase, ref int __result)
        {
            // Shotgun never reaches the stat sum, so there is no observer contribution to remove.
            if (weaponBase?.weaponData != null && (int)weaponBase.weaponData.eWeapon == ShotgunEWeapon)
            {
                return;
            }

            var owner = ResolveRemoteOwner(weaponBase);
            if (owner == null)
            {
                return;
            }

            var ownerStat = playerStatsSyncService.GetRemoteStat(owner.Value, (int)EStat.Projectiles);
            if (ownerStat == null)
            {
                return;
            }

            var corrected = __result - (int)PlayerStats.GetStat(EStat.Projectiles) + (int)ownerStat.Value;

            // The game clamps this to a floor; a peer whose stat is lower must still get at least
            // one projectile rather than none.
            __result = corrected < 1 ? 1 : corrected;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(WeaponUtility.GetAttackSizeMultiplier))]
        public static void GetAttackSizeMultiplier_Postfix(WeaponBase weaponBase, ref float __result)
        {
            var owner = ResolveRemoteOwner(weaponBase);
            if (owner == null)
            {
                return;
            }

            var ownerStat = playerStatsSyncService.GetRemoteStat(owner.Value, (int)EStat.SizeMultiplier);
            if (ownerStat == null)
            {
                return;
            }

            var corrected = __result - PlayerStats.GetStat(EStat.SizeMultiplier) + ownerStat.Value;

            // A non-positive multiplier would collapse the attack to nothing, which is worse than
            // the wrong size this is fixing.
            if (corrected > 0f)
            {
                __result = corrected;
            }
        }

        /// <summary>
        /// The connection id of the remote player owning this weapon, or null when it is the local
        /// player's — in which case the game's own answer is already right and nothing is touched.
        ///
        /// <para>This runs on a per-attack path, so the session check comes first and is the
        /// cheapest thing here.</para>
        /// </summary>
        private static uint? ResolveRemoteOwner(WeaponBase weaponBase)
        {
            if (weaponBase == null || !synchronizationService.HasNetplaySessionStarted())
            {
                return null;
            }

            var netPlayer = playerManagerService.GetNetPlayerByWeapon(weaponBase);
            return netPlayer == null ? null : netPlayer.ConnectionId;
        }
    }
}

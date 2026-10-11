using Assets.Scripts.Inventory__Items__Pickups.Weapons;
﻿using Assets.Scripts.Game.Combat.ConstantAttacks;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace MegabonkTogether.Patches.ConstantAttacks
{
    [HarmonyPatch(typeof(AegisAttack))]
    internal static class AegisAttackPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        /// <summary>
        /// Prevent update on remote player
        /// </summary>

        [HarmonyPrefix]
        [HarmonyPatch(nameof(AegisAttack.FixedUpdate))]
        public static bool FixedUpdate_Prefix(AegisAttack __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var ownerId = DynamicData.For(__instance).Get<uint?>("ownerId");
            if (!ownerId.HasValue)
            {
                return true;
            }

            var playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
            var ownerPlayer = playerManagerService.GetPlayer(ownerId.Value);
            var localPlayer = playerManagerService.GetLocalPlayer();
            var isLocal = ownerPlayer.ConnectionId == localPlayer.ConnectionId;

            if (!isLocal)
            {
                ShowRemoteShieldCount(__instance);
            }

            return isLocal;
        }

        /// <summary>
        /// OB-1, Aegis. Keeps a remote player's shield count on screen in step with their stats.
        ///
        /// <para>This prefix blocks FixedUpdate for a remote avatar's Aegis, and FixedUpdate is the
        /// only thing that grows the count: it calls AmplifyShield (VA 0x1804A43D0, decompiled),
        /// which raises currentAmount up to WeaponUtility.GetAttackQuantity(weaponBase) and calls
        /// aegisRenderer.SetAmount. Blocked, the remote shield stayed at its initial value forever
        /// -- reported 2026-10-11 as one shield on the host's screen while the owner saw more.</para>
        ///
        /// <para>FixedUpdate stays blocked: AmplifyShield also plays the regen sound and raises the
        /// static AegisAttack.A_Regen, both of which would act on the LOCAL player. Instead the
        /// count is set to the maximum directly. GetAttackQuantity is already corrected to the
        /// owner's own Projectiles stat by the OB-1 WeaponUtility postfix. Only written when it
        /// changes, so this costs one quantity read per physics tick per remote Aegis.</para>
        ///
        /// <para>Shows the owner's maximum, not shields they have just spent: shield use is not
        /// replicated, so a remote shield that blocks a hit still reads full here.</para>
        /// </summary>
        private static void ShowRemoteShieldCount(AegisAttack aegis)
        {
            var weaponBase = aegis.weaponBase;
            var renderer = aegis.aegisRenderer;
            if (weaponBase == null || renderer == null)
            {
                return;
            }

            var max = WeaponUtility.GetAttackQuantity(weaponBase);
            if (max < 1 || aegis.currentAmount == max)
            {
                return;
            }

            aegis.currentAmount = max;
            renderer.SetAmount(max);
        }
    }
}

using Assets.Scripts.Inventory__Items__Pickups.Weapons;
﻿using Assets.Scripts.Game.Combat.ConstantAttacks;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches.ConstantAttacks
{
    [HarmonyPatch(typeof(AegisAttack))]
    internal static class AegisAttackPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        /// <summary>Blocks FixedUpdate for remote players' Aegis.</summary>

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
        /// OB-1: sets a remote Aegis count to its maximum directly. FixedUpdate stays blocked because
        /// AmplifyShield (VA 0x1804A43D0, decompiled) plays sound and raises the static A_Regen on the local player.
        /// Shows the maximum; shield use is not replicated.
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

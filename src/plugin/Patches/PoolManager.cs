using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Attacks;
using Assets.Scripts.Objects.Pooling;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches
{
    [HarmonyPatch(typeof(PoolManager))]
    internal class PoolManagerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Stamps ownerId on pooled WeaponAttacks for remote players' attacks.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(PoolManager.GetAttack))]
        public static void GetAttack_Postfix(ref WeaponAttack __result, WeaponBase weaponBase)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var weaponBaseData = DynamicData.For(weaponBase);
            var ownerId = weaponBaseData.Get<uint?>("ownerId");

            if (ownerId.HasValue)
            {
                DynamicData.For(__result).Set("ownerId", ownerId.Value);
            }
        }
    }
}
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Projectiles
{
    /// <summary>
    /// OB-59. Decompiled: <c>StepMovement</c> (0x1804E65D0) calls <c>HitTarget</c> (0x1804E6320) on
    /// reaching <c>lastTarget</c>. <c>HitTarget</c> damages it, draws <c>ProjectileHit</c>, counts a bounce
    /// and either calls <c>ProjectileDone</c> or picks the next target. A client refuses that
    /// <c>ProjectileDone</c> for a received copy, so the copy kept hitting targets of its own choosing,
    /// drawing a hit each time. The host's hits reach the client through <c>EnemyDamagedBatch</c>.
    /// </summary>
    [HarmonyPatch(typeof(ProjectileBluetooth))]
    internal static class ProjectileBluetoothPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Prefix-false on clients for received copies: the host decides what they hit.</summary>
        [HarmonyPrefix]
        [HarmonyPatch("HitTarget")]
        private static bool HitTarget_Prefix(ProjectileBluetooth __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (synchronizationService.IsServerMode() ?? false)
            {
                Helpers.ExplosionDiagnostics.HostBluetoothHit();
                return true;
            }

            if (!ProjectileBasePatches.IsReceived(__instance))
            {
                return true;
            }

            Helpers.ExplosionDiagnostics.BlockedBluetoothHit();
            return false;
        }
    }
}

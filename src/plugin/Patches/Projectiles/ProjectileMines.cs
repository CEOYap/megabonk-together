using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches.Projectiles
{
    /// <summary>
    /// OB-58. Mine explosions are the host's: it sends <see cref="MineExploded"/> from
    /// <c>Explode</c>, and a client draws exactly those. Decompiled: <c>MyFixedUpdate</c> (0x1804EEF10)
    /// calls <c>Explode</c> (0x1804EE9A0) when an enemy is inside the mine's radius; <c>Explode</c>
    /// draws from <c>explosionPool</c> (0x1D8) scaled by <c>projectileRadius</c>, damages, and calls
    /// <c>ProjectileDone</c>. Most mines instead time out through <c>ProjectileBase.CheckTimeout</c>
    /// (0x18042BB30) with no explosion.
    /// <para>Hands-off runs, 2026-10-24 (host / client): drawing on every removal gave 42 / 710; the
    /// client's own enemy check gave 119 / 73, and 197 / 151 with TryInit's radius copied. The client
    /// sees enemies late and the host kills some first, so only the host can say when one exploded.</para>
    /// </summary>
    [HarmonyPatch(typeof(ProjectileMines))]
    internal static class ProjectileMinesPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly INetTransport transport = Plugin.Services.GetService<INetTransport>();

        /// <summary>
        /// The scale-in a client would otherwise skip: it never runs <c>TryInit</c> (0x1804EF150) on a
        /// received mine. With Ghidra's names one slot out, checked against dump.cs offsets: spawnedAtTime
        /// (0x80) = now, localScale = 0, scaleMultiplier (0x84) = clamp(1 + 0.3 x (attack size - 1), 1, 3),
        /// checkRadius (0x78) = 2 x scaleMultiplier; constants from .rdata (0x18262EC7C = 1,
        /// 0x18262EDB8 = 0.3, 0x18262ECA8 = 3). Not copied: its rb.MovePosition to MyPlayer, which would
        /// put the mine under this machine's player. Visual only now that the host sends explosions.
        /// </summary>
        internal static void InitReceived(ProjectileMines mine, WeaponBase weapon)
        {
            var size = weapon != null ? WeaponUtility.GetAttackSizeMultiplier(weapon) : 1f;
            var scale = Mathf.Clamp(1f + 0.3f * (size - 1f), 1f, 3f);

            mine.spawnedAtTime = Assets.Scripts.Utility.MyTime.time;
            mine.scaleMultiplier = scale;
            mine.checkRadius = 2f * scale;
            mine.nextCheckTime = 0f;
            mine.transform.localScale = Vector3.zero;
        }

        /// <summary>Prefix-false on clients for received mines: their explosions come from the host.</summary>
        [HarmonyPrefix]
        [HarmonyPatch("MyFixedUpdate")]
        private static bool MyFixedUpdate_Prefix(ProjectileMines __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted() || (synchronizationService.IsServerMode() ?? false))
            {
                return true;
            }

            return !ProjectileBasePatches.IsReceived(__instance);
        }

        /// <summary>Host: share the explosion <c>Explode</c> just drew.</summary>
        [HarmonyPostfix]
        [HarmonyPatch("Explode")]
        private static void Explode_Postfix(ProjectileMines __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted() || synchronizationService.IsServerMode() != true)
            {
                return;
            }

            Helpers.ExplosionDiagnostics.HostMineExploded();
            transport.SendToAllClients(
                new MineExploded
                {
                    Position = Helpers.Quantizer.Quantize(__instance.transform.position),
                    Scale = __instance.projectileRadius,
                },
                NetDelivery.ReliableUnordered);
        }

        /// <summary>Client: draw the host's mine explosion as <c>Explode</c> does. Main thread.</summary>
        internal static void OnReceivedMineExploded(MineExploded exploded)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var explosion = Assets.Scripts.Objects.Pooling.PoolManager.Instance?.explosionPool?.Get();
            if (explosion == null)
            {
                return;
            }

            explosion.transform.position = Helpers.Quantizer.Dequantize(exploded.Position);
            explosion.transform.localScale = Vector3.one * exploded.Scale;
            explosion.SetActive(true);
            Helpers.ExplosionDiagnostics.DrewMine();
        }
    }
}

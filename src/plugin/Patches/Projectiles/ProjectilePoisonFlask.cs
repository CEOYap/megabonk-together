using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using HarmonyLib;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches.Projectiles
{
    /// <summary>
    /// OB-59. Poison Flask explosions are the host's: it sends <see cref="FlaskExploded"/> from
    /// <c>ExplodeFlask</c>, and a client plays exactly those. Decompiled: the overrides <c>HitEnemy</c>
    /// (0x1804EFDC0) and <c>HitOther</c> (0x1804EFDE0) only call <c>ExplodeFlask</c> (0x1804EF5D0), which
    /// detaches <c>effect</c> (0x80) to the flask's position, toggles it, sets its local scale to
    /// <c>explosionSizeDefault</c> (0x88) x attack size, plays it, damages and poisons enemies in
    /// <c>explosionRadius</c> x attack size, calls <c>ProjectileHit</c> per enemy, then <c>ProjectileDone</c>.
    /// A client refuses that <c>ProjectileDone</c> for a received copy, so the copy used to explode again on
    /// each later collision. <c>explosionSizeDefault</c> is filled from the effect's scale in <c>TryInit</c>
    /// (0x1804F0240), which a client never runs on a received copy, so its own explosions had zero scale.
    /// </summary>
    [HarmonyPatch(typeof(ProjectilePoisonFlask))]
    internal static class ProjectilePoisonFlaskPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IProjectileManagerService projectileManagerService = Plugin.Services.GetService<IProjectileManagerService>();
        private static readonly INetTransport transport = Plugin.Services.GetService<INetTransport>();

        /// <summary>
        /// A played effect is detached from its copy, which the interpolator destroys soon after, so it
        /// is destroyed on its own once it has played. UNVERIFIED: the particle length was not read; 10 s
        /// is a margin.
        /// </summary>
        private const float DetachedEffectLifetime = 10f;

        private static bool IsReceivedOnClient(ProjectileBase projectile)
            => synchronizationService.HasNetplaySessionStarted()
               && !(synchronizationService.IsServerMode() ?? false)
               && ProjectileBasePatches.IsReceived(projectile);

        /// <summary>Prefix-false on clients for received flasks: their explosions come from the host.</summary>
        [HarmonyPrefix]
        [HarmonyPatch("HitEnemy")]
        private static bool HitEnemy_Prefix(ProjectilePoisonFlask __instance, ref bool __result)
        {
            if (!IsReceivedOnClient(__instance))
            {
                return true;
            }

            Helpers.ExplosionDiagnostics.BlockedFlaskHit();
            __result = true;
            return false;
        }

        /// <summary>Prefix-false on clients for received flasks, as <see cref="HitEnemy_Prefix"/>.</summary>
        [HarmonyPrefix]
        [HarmonyPatch("HitOther")]
        private static bool HitOther_Prefix(ProjectilePoisonFlask __instance)
        {
            if (!IsReceivedOnClient(__instance))
            {
                return true;
            }

            Helpers.ExplosionDiagnostics.BlockedFlaskHit();
            return false;
        }

        /// <summary>Host: the flask's id, read before <c>ExplodeFlask</c>'s <c>ProjectileDone</c> drops it.</summary>
        [HarmonyPrefix]
        [HarmonyPatch("ExplodeFlask")]
        private static void ExplodeFlask_Prefix(ProjectilePoisonFlask __instance, out uint? __state)
        {
            __state = null;
            if (!synchronizationService.HasNetplaySessionStarted() || synchronizationService.IsServerMode() != true)
            {
                return;
            }

            var entry = projectileManagerService.GetProjectileByReference(__instance);
            if (entry.Value != null)
            {
                __state = entry.Key;
            }
        }

        /// <summary>Host: share the explosion <c>ExplodeFlask</c> just played.</summary>
        [HarmonyPostfix]
        [HarmonyPatch("ExplodeFlask")]
        private static void ExplodeFlask_Postfix(ProjectilePoisonFlask __instance, uint? __state)
        {
            if (!__state.HasValue)
            {
                return;
            }

            var effect = __instance.effect;
            if (effect == null)
            {
                return;
            }

            var scale = effect.transform.localScale;
            Helpers.ExplosionDiagnostics.HostFlaskExploded();
            transport.SendToAllClients(
                new FlaskExploded
                {
                    ProjectileId = __state.Value,
                    Position = Helpers.Quantizer.Quantize(effect.transform.position),
                    ScaleX = scale.x,
                    ScaleY = scale.y,
                    ScaleZ = scale.z,
                },
                NetDelivery.ReliableUnordered);
        }

        /// <summary>
        /// Client: play the host's explosion on the received copy's own effect, as <c>ExplodeFlask</c>
        /// does, then hide the copy, which on the host is finished. Main thread.
        /// </summary>
        internal static void OnReceivedFlaskExploded(FlaskExploded exploded)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var copy = projectileManagerService.GetReceivedProjectile(exploded.ProjectileId);
            var flask = copy != null ? copy.GetComponent<ProjectilePoisonFlask>() : null;
            var effect = flask != null ? flask.effect : null;
            if (effect == null)
            {
                Helpers.ExplosionDiagnostics.MissedFlask();
                return;
            }

            var effectObject = effect.gameObject;
            effect.transform.SetParent(null);
            effect.transform.position = Helpers.Quantizer.Dequantize(exploded.Position);
            effectObject.SetActive(false);
            effectObject.SetActive(true);
            effect.transform.localScale = new Vector3(exploded.ScaleX, exploded.ScaleY, exploded.ScaleZ);
            effect.Play();
            Object.Destroy(effectObject, DetachedEffectLifetime);

            flask.effect = null;
            copy.SetActive(false);
            Helpers.ExplosionDiagnostics.DrewFlask();
        }
    }
}

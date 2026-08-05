using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace MegabonkTogether.Patches.Projectiles
{
    [HarmonyPatch(typeof(Rocket))]
    internal static class RocketPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Use the correct player (local / remote) transform, and stop a client simulating a
        /// rocket the host owns.
        ///
        /// <para><b>Why this needs its own suppression:</b> <c>Rocket</c> is a bare
        /// <c>MonoBehaviour</c> — it does <b>not</b> derive from <c>ProjectileBase</c> (settled with
        /// <c>scripts/re/interop_members.py Rocket</c>). So every client-side guard in
        /// <see cref="ProjectileBasePatches"/> — <c>HitEnemy_Prefix</c> returning <c>isServer</c>,
        /// <c>ProjectileDone_Postfix</c> returning false for a networked projectile — patches a type
        /// the rocket never inherits, and has never applied to one.</para>
        ///
        /// <para>Decompiled <c>Rocket$$FixedUpdate</c> (<c>megabonk-re/decompiled/</c>) is:
        /// <c>if (!MyTime.paused) { StepMovement(); if (activeInHierarchy &amp;&amp; expiration &lt;= MyTime.time) ProjectileDone(); }</c>.
        /// On a client that means the game flies the rocket under local physics while
        /// <c>ProjectileInterpolator</c> teleports the same transform to host snapshots at 20 Hz,
        /// and detonates it on whichever local clock runs out first — the mid-flight explosions
        /// reported over the internet.</para>
        ///
        /// <para><b>UNVERIFIED:</b> the timeout field is read at <c>weaponBase+4</c> in Ghidra's
        /// output, whose applied struct names are known to sit a slot out. It is compared against
        /// <c>MyTime</c>, so it is an expiry check; which named field it is has not been confirmed
        /// against <c>dump.cs</c>, and nothing here depends on the answer.</para>
        ///
        /// <para><b>Why <c>ownerId</c> and not <c>weaponBase</c>:</b> the first version of this
        /// patch keyed on <c>GetNetPlayerByWeapon(__instance.weaponBase)</c> and was a no-op on the
        /// exact case it was written for. The client's spawn path sets <c>weaponBase</c> on the
        /// <b>ProjectileRocket</b> parent (<c>SynchronizationService</c>, <c>case EWeapon.Rockets</c>)
        /// and never on the <c>Rocket</c> child this patch runs against, so the lookup returned null
        /// and nothing was suppressed. The marker that path *does* set on the child is
        /// <c>DynamicData.For(rocketProjectileInstance.rocket).Set("ownerId", …)</c>, which is
        /// present on a received rocket and absent on one this peer fired itself.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Rocket.FixedUpdate))]
        public static bool MyFixedUpdate_Prefix(Rocket __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;

            if (!isHost)
            {
                // A received rocket carries an ownerId; the host owns its path and its lifetime,
                // and ProjectileInterpolator drives the transform. Anything without one is this
                // peer's own rocket and must keep simulating.
                return !DynamicData.For(__instance).Get<uint?>("ownerId").HasValue;
            }

            var netPlayer = playerManagerService.GetNetPlayerByWeapon(__instance.weaponBase);
            if (netPlayer == null)
            {
                return true;
            }

            // Host only, matching the postfix's pop exactly — the pair used to be unbalanced,
            // pushing on every peer and popping only on a host.
            playerManagerService.AddGetNetplayerPositionRequest(netPlayer.ConnectionId);
            __state = true;

            return true;
        }

        /// <summary>
        /// Suppress a client's own despawn of a host-owned rocket.
        ///
        /// <para>Decompiled <c>Rocket$$ProjectileDone</c> takes a GameObject from
        /// <c>PoolManager</c>, positions it at <c>transform.position + transform.forward</c> and
        /// activates it — that is the explosion — then deactivates the rocket and releases it back
        /// to the pool. This is the same suppression
        /// <see cref="ProjectileBasePatches.ProjectileDone_Postfix"/> already applies to every other
        /// projectile type on a client; the rocket only ever missed it because of the type
        /// hierarchy.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Rocket.ProjectileDone))]
        public static bool ProjectileDone_Prefix(Rocket __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;
            if (isHost)
            {
                return true;
            }

            // Same ownerId marker as the FixedUpdate prefix above, for the same reason.
            return !DynamicData.For(__instance).Get<uint?>("ownerId").HasValue;
        }

        /// <summary>
        /// Restore original transform after prefix
        /// </summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(Rocket.FixedUpdate))]
        public static void MyFixedUpdate_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }

    [HarmonyPatch(typeof(ProjectileRocket))]
    internal static class ProjectileRocketPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Use the correct player (local / remote) transform
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileRocket.TryInit))]
        public static bool TryInit_Prefix(ProjectileRocket __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;
            if (!isHost)
            {
                return true;
            }

            var netPlayer = Plugin.Services.GetService<IPlayerManagerService>().GetNetPlayerByWeapon(__instance.weaponBase);
            if (netPlayer == null)
            {
                return true;
            }

            playerManagerService.AddGetNetplayerPositionRequest(netPlayer.ConnectionId);

            __state = true;

            return true;
        }

        /// <summary>
        /// Restore original transform after prefix
        /// </summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileRocket.TryInit))]
        public static void TryInit_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
}

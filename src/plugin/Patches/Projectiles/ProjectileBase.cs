using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using Assets.Scripts.Objects.Particles___Effects.ParticleOpacity;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace MegabonkTogether.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileBase))]
    internal class ProjectileBasePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IProjectileManagerService projectileManagerService = Plugin.Services.GetService<IProjectileManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        // PERF: per-projectile caches for the opacity path below. Keyed by instance id rather than
        // by the component, so a destroyed projectile cannot keep an Il2Cpp wrapper alive.
        private static readonly System.Collections.Generic.Dictionary<int, ParticleOpacity> opacityComponents = new();
        private static readonly System.Collections.Generic.Dictionary<int, bool> hiddenState = new();

        /// <summary>
        /// Make sure to spawn projectiles at the net player's position
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileBase.TryInit))]
        public static void TryInit_Prefix(ProjectileBase __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var weapon = __instance.weaponBase;
            var netPlayer = playerManagerService.GetNetPlayerByWeapon(weapon);

            if (netPlayer != null)
            {
                var id = netPlayer.ConnectionId;

                __instance.transform.position = new UnityEngine.Vector3(
                    netPlayer.Model.transform.position.x,
                    netPlayer.Model.transform.position.y + Plugin.PLAYER_FEET_OFFSET_Y,
                    netPlayer.Model.transform.position.z
                );

                playerManagerService.AddProjectileToSpawn(id);
            }
            else
            {
                Plugin.Log.LogWarning("Weapon not found ?");
            }
        }


        /// <summary>
        /// Ignore HitEnemy for projectiles on clients (Simulated by server). Also track which player is hitting the enemy for stats tracking (money flying, item procs, kills)
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileBase.HitEnemy))]
        public static bool HitEnemy_Prefix(ProjectileBase __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;

            if (isServer)
            {
                var owner = playerManagerService.GetNetPlayerByWeapon(__instance.weaponBase);
                if (owner != null)
                {
                    trackerService.SetCurrentPlayerId(owner.ConnectionId);
                }
            }

            return isServer;
        }


        /// <summary>
        /// Remove the tracking
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ProjectileBase.HitEnemy))]
        public static void HitEnemy_Postfix(ProjectileBase __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            trackerService.UnsetCurrentPlayerId();
        }


        ///// <summary>
        ///// Ignore HitEnemy for projectiles not owned by local player (Prevent some null exceptions logs)
        ///// </summary>
        ///// <param name="__instance"></param>
        ///// <returns></returns>
        //[HarmonyPrefix]
        //[HarmonyPatch(nameof(ProjectileBase.HitEnemy))]
        //public static bool HitEnemy_Prefix(ProjectileBase __instance)
        //{
        //    if (!synchronizationService.HasNetplaySessionStarted())
        //    {
        //        return true;
        //    }

        //    var projectileEntry = projectileManagerService.GetProjectileByReference(__instance);

        //    if (projectileEntry.Value == null)
        //    {
        //        return false;
        //    }

        //    return true;
        //}

        /// <summary>
        /// Synchronize projectile destruction server-side only
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileBase.ProjectileDone))]
        public static bool ProjectileDone_Postfix(ProjectileBase __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            var netplayId = DynamicData.For(__instance).Get<uint?>("netplayId");
            if (netplayId.HasValue && !isServer)
            {
                return false;
            }

            synchronizationService.OnProjectileDone(__instance);
            ForgetProjectile(__instance.GetInstanceID());

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileBase.Update))]
        public static void Update_Prefix(ProjectileBase __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (playerManagerService.GetNetPlayerByWeapon(__instance.weaponBase) == null)
            {
                return; // Don't hide local player projectiles
            }

            DistanceToPlayer distance = Plugin.GetDistanceToPlayer(__instance.transform.position);

            var shouldHide = distance == DistanceToPlayer.Far;
            UpdateProjectileOpacity(__instance, shouldHide);
        }


        /// <summary>
        /// PERF: two costs removed, both paid per remote projectile per frame.
        ///
        /// <para><b>The component lookup.</b> <c>GetComponentInChildren</c> is a native hierarchy
        /// walk, and it ran every frame for a component that cannot change. Cached per projectile,
        /// keyed by instance id.</para>
        ///
        /// <para><b>The refresh itself.</b> <c>Refresh(true)</c> ran on <b>both</b> branches every
        /// frame — so a visible projectile spent the whole run re-applying an opacity it already
        /// had. It now fires only when the hide state actually changes, which is the same
        /// transition tracking <see cref="Helpers.DistanceThrottler"/> already does for renderers.
        /// The two writes to <c>SaveManager…particle_opacity</c> go with it.</para>
        /// </summary>
        private static void UpdateProjectileOpacity(ProjectileBase projectile, bool hide)
        {
            var instanceId = projectile.GetInstanceID();

            if (hiddenState.TryGetValue(instanceId, out var wasHidden) && wasHidden == hide)
            {
                return;
            }

            if (!opacityComponents.TryGetValue(instanceId, out var particleOpacity))
            {
                particleOpacity = projectile.GetComponentInChildren<ParticleOpacity>();
                opacityComponents[instanceId] = particleOpacity;
            }

            if (particleOpacity == null)
            {
                // Recorded so the hierarchy walk is not repeated every frame for a projectile
                // that has no ParticleOpacity at all.
                hiddenState[instanceId] = hide;
                return;
            }

            if (hide)
            {
                var current = SaveManager.Instance.config.cfVisualsSettings.particle_opacity;
                SaveManager.Instance.config.cfVisualsSettings.particle_opacity = 0f;
                particleOpacity.Refresh(true);
                SaveManager.Instance.config.cfVisualsSettings.particle_opacity = current;
            }
            else
            {
                particleOpacity.Refresh(true);
            }

            hiddenState[instanceId] = hide;
        }

        /// <summary>Drops a finished projectile's cached entries.</summary>
        internal static void ForgetProjectile(int instanceId)
        {
            opacityComponents.Remove(instanceId);
            hiddenState.Remove(instanceId);
        }

        /// <summary>Clears both caches so they cannot carry Il2Cpp wrappers across a session.</summary>
        internal static void ClearOpacityCache()
        {
            opacityComponents.Clear();
            hiddenState.Clear();
        }
    }
}

using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using Assets.Scripts.Objects.Particles___Effects.ParticleOpacity;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches.Projectiles
{
    [HarmonyPatch(typeof(ProjectileBase))]
    internal class ProjectileBasePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IProjectileManagerService projectileManagerService = Plugin.Services.GetService<IProjectileManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        // PERF: keyed by instance id so a destroyed projectile cannot keep an Il2Cpp wrapper alive.
        private static readonly System.Collections.Generic.Dictionary<int, ParticleOpacity> opacityComponents = new();
        private static readonly System.Collections.Generic.Dictionary<int, bool> hiddenState = new();

        /// <summary>Spawn projectiles at the net player's position.</summary>
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


        /// <summary>Prefix-false on clients (host simulates hits). Host sets the tracker owner for credit.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ProjectileBase.HitEnemy))]
        public static bool HitEnemy_Prefix(ProjectileBase __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;

            if (isServer)
            {
                // OB-41: inventory store, not avatars, which are cleared at the portal mid-flight.
                var weapon = __instance.weaponBase;
                var ownerId = playerManagerService.GetWeaponOwnerId(weapon);

                // OB-14: a null lookup credits the host; the pairing counters cannot see that.
                TrackerAttributionDiagnostics.RecordWeaponOwnerLookup(ownerId.HasValue);

                if (ownerId.HasValue)
                {
                    trackerService.SetCurrentPlayerId(ownerId.Value);
                    __state = true;
                }
                else
                {
                    var localWeapons = GameManager.Instance?.player?.inventory?.weaponInventory?.weapons;
                    if (weapon != null && localWeapons != null && localWeapons.ContainsValue(weapon))
                    {
                        TrackerAttributionDiagnostics.RecordOwnWeaponHit();
                    }
                    else if (TrackerAttributionDiagnostics.TakeUnknownSample())
                    {
                        TrackerAttributionDiagnostics.LogUnknownWeaponHit(
                            __instance.gameObject?.name ?? "projectile", weapon == null, weapon?.weaponData?.eWeapon.ToString());
                    }
                }
            }

            return isServer;
        }


        /// <summary>
        /// P1-5: finalizer, not postfix, so a throw in HitEnemy cannot strand the owner; unsets only
        /// what the prefix set.
        /// </summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(ProjectileBase.HitEnemy))]
        public static void HitEnemy_Finalizer(bool __state)
        {
            if (!__state)
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

        /// <summary>Clients skip destroying synced projectiles; the host decides.</summary>
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


        /// <summary>PERF: caches the component and refreshes only when the hide state changes.</summary>
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

        internal static void ForgetProjectile(int instanceId)
        {
            opacityComponents.Remove(instanceId);
            hiddenState.Remove(instanceId);
        }

        /// <summary>So caches cannot carry Il2Cpp wrappers across sessions.</summary>
        internal static void ClearOpacityCache()
        {
            opacityComponents.Clear();
            hiddenState.Clear();
        }
    }
}

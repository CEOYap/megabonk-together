using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using OmniBonk.Helpers;
using OmniBonk.Scripts.Enemies;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Patches.Enemies
{
    [HarmonyPatch(typeof(Enemy))]
    internal static class EnemyPatch
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IEnemyManagerService enemyManagerService = Plugin.Services.GetService<IEnemyManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        // OB-57. Plus the passives whose hit only the owner's game can make (it reacts to the
        // owner's own evade or damage taken), so a client's must reach the host. Their sources are
        // constants in the passive (decompiled ctors), not EItem names.
        public static readonly HashSet<string> AllowedDamageSource = new(Enum.GetNames(typeof(EItem))) { "Shadowstep", "Flex" };

        public static readonly DistanceThrottler EnemiesDistanceThrottler = new();

        /// <summary>Host: target a random player and attach a TargetSwitcher.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.InitEnemy))]
        public static void init_PostFix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            EnemiesDistanceThrottler.Cleanup(__instance.GetInstanceID());
            var renderer = __instance.GetComponentInChildren<Renderer>();
            if (renderer != null && !renderer.enabled)
            {
                renderer.enabled = true;
            }

            var isServer = synchronizationService.IsServerMode();
            if (isServer.HasValue && isServer.Value)
            {
                // FIX P0-4: null only during teardown; leave targetId unset. Not logged (per spawn).
                var host = playerManagerService.GetLocalPlayer();

                if (playerManagerService.TryGetGetNetplayerPosition(out uint id)) //TODO: this could be simplifed but too lazy
                {
                    if (host != null && host.ConnectionId == id)
                    {
                        DynamicData.For(__instance).Set("targetId", host.ConnectionId);
                    }
                    else
                    {
                        var randomPlayer = playerManagerService.GetNetPlayerByNetplayId(id);

                        if (randomPlayer != null)
                        {
                            __instance.target = randomPlayer.Rigidbody;
                            DynamicData.For(__instance).Set("targetId", randomPlayer.ConnectionId);
                        }
                        else if (host != null)
                        {
                            // FIX P0-4: peer disconnected; fall back to the host.
                            DynamicData.For(__instance).Set("targetId", host.ConnectionId);
                        }
                    }
                }
                else if (host != null)
                {
                    DynamicData.For(__instance).Set("targetId", host.ConnectionId);
                }

                // PERF 1A: enemies are pooled; reuse the switcher instead of stacking another.
                var switcher = __instance.gameObject.GetComponent<TargetSwitcher>();
                if (switcher == null)
                {
                    switcher = __instance.gameObject.AddComponent<TargetSwitcher>();
                }
                if (__instance.enemyData.enemyName == Actors.Enemies.EEnemy.GhostGrave1
                    || __instance.enemyData.enemyName == Actors.Enemies.EEnemy.GhostGrave2
                    || __instance.enemyData.enemyName == Actors.Enemies.EEnemy.GhostGrave3
                    || __instance.enemyData.enemyName == Actors.Enemies.EEnemy.GhostGrave4)
                {
                    var targetId = switcher.StartSwitching(__instance, true);

                    DynamicData.For(__instance).Set("targetId", targetId);
                }
                else
                {
                    switcher.StartSwitching(__instance);
                }

                enemyManagerService.InitializeSwitcher(switcher, __instance.enemyFlag, __instance.enemyData.enemyName);
            }
            else
            {
                __instance.basePowerupDropChance = 0f;
                __instance.minStayAtDistance = 0f;
            }
        }

        /// <summary>
        /// Manually register a local kill to properly track item procs, money flying and stats
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.EnemyDied), typeof(DamageContainer))]
        public static void EnemyDied_Prefix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            Items.EventListenerOwnership.NoteKiller(__instance);

            var currentTracker = trackerService.GetCurrentPlayerId();
            if (currentTracker.HasValue && currentTracker.Value != playerManagerService.GetLocalPlayer().ConnectionId) { return; }

            trackerService.RegisterTrack();
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.EnemyDied), typeof(DamageContainer))]
        public static void EnemyDied_Postfix(Enemy __instance, DamageContainer dc)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return;
            }

            synchronizationService.OnEnemyDied(__instance, dc, trackerService.GetCurrentPlayerId());
        }

        /// <summary>
        /// Manually register a local kill to properly track item procs, money flying and stats
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.EnemyDied), [])]
        public static void EnemyDiedWithoutDc_Prefix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            Items.EventListenerOwnership.NoteKiller(__instance);

            var currentTracker = trackerService.GetCurrentPlayerId();
            if (currentTracker.HasValue && currentTracker.Value != playerManagerService.GetLocalPlayer().ConnectionId) { return; }

            trackerService.RegisterTrack();
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.EnemyDied), new System.Type[0])]
        public static void EnemyDiedWithoutDc_Postfix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (!Plugin.CAN_SEND_MESSAGES)
            {
                return;
            }

            synchronizationService.OnEnemyDied(__instance, ownerId: trackerService.GetCurrentPlayerId());
        }

        /// <summary>Damage is host-authoritative.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.Damage))]
        public static bool Damage_Prefix(Enemy __instance, DamageContainer damageContainer, out bool __state)
        {
            // __state = suppressed; the postfix still runs after a false prefix and must not broadcast.
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            // OB-13: Damage (VA 0x1804AC7B0) is the funnel for all game callers and our replicated hits.
            if (EnemyDamageWhilePausedPatches.IsSuppressedForPause())
            {
                __state = true;
                EnemyDamageWhilePausedPatches.RecordSuppressed();
                return false;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (isServer)
            {
                return true;
            }

            if (!Plugin.Instance.CAN_DAMAGE_ENEMIES)
            {
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.Damage))]
        public static void Damage_Postfix(Enemy __instance, DamageContainer damageContainer, bool __state)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            if (__state)
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;

            // OB-34: a replayed host hit must not be forwarded back (it ping-ponged between peers).
            if (!isServer && Plugin.Instance.CAN_DAMAGE_ENEMIES)
            {
                return;
            }

            // OB-63: nor one replayed under SuppressOutbound. A client's replay of a host Lightning Orb
            // strike (tag 41) reaches Damage with source "LightningOrb", an item name, so it was sent
            // back and the host applied it twice.
            if (!isServer && !Plugin.CAN_SEND_MESSAGES)
            {
                if (AllowedDamageSource.Contains(damageContainer.damageSource))
                {
                    Helpers.ReplayedHitDiagnostics.Suppressed(damageContainer.damageSource);
                }

                return;
            }

            if (isServer || AllowedDamageSource.Contains(damageContainer.damageSource))
            {
                synchronizationService.OnEnemyDamaged(__instance, damageContainer);
            }
        }

        /// <summary>
        /// Prevent clients from updating enemy movement
        /// </summary> 
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.TeleportToPlayer))]
        public static bool TeleportToPlayer_Prefix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Prevent clients from updating enemy movement
        /// </summary> 
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.StartTeleporting))]
        public static bool StartTeleporting_Prefix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Prevent clients from updating enemy movement
        /// </summary> 
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.TryTeleport))]
        public static bool TryTeleport_Prefix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
            }

            return true;
        }

        /// <summary>Heal is host-only.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.Heal))]
        public static bool Heal_Prefix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        ///  Prevent enemies from running away from the player in netplay
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.IsRunningFromPlayer))]
        public static bool IsRunningFromPlayer_Prefix(ref bool __result)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            __result = false;

            return false;
        }

        /// <summary>
        /// Prevent enemies from being stationary in netplay
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.IsStationary))]
        public static bool IsStationary_Prefix(ref bool __result)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }
            __result = false;
            return false;
        }

        /// <summary>
        /// Distance throttling for non-boss enemies: host only hides far renderers, clients fully throttle.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.MyUpdate))]
        public static bool MyUpdate_Prefix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (__instance.IsBoss() || __instance.IsStageBoss() || __instance.IsFinalBoss())
            {
                return true;
            }

            var instanceId = __instance.GetInstanceID();
            var isServer = synchronizationService.IsServerMode() ?? false;
            return EnemiesDistanceThrottler.ShouldUpdate(__instance.gameObject, instanceId, isServer);
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.MyFixedUpdate))]
        public static bool MyFixedUpdate_Prefix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (__instance.IsBoss() || __instance.IsStageBoss() || __instance.IsFinalBoss())
            {
                return true;
            }

            var instanceId = __instance.GetInstanceID();
            var isServer = synchronizationService.IsServerMode() ?? false;
            return EnemiesDistanceThrottler.ShouldUpdate(__instance.gameObject, instanceId, isServer);
        }

        // No netplay guard: cleanup is harmless in singleplayer.
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.EnemyDied), typeof(DamageContainer))]
        public static void EnemyDied_Cleanup(Enemy __instance)
        {
            var instanceId = __instance.GetInstanceID();
            EnemiesDistanceThrottler.Cleanup(instanceId);
        }

        private static int pooledWhileRegistered;

        /// <summary>
        /// OB-51: <c>ReleaseToPool</c> (VA 0x1804AFC50, decompiled) is where every enemy leaves the field.
        /// Client: unregister its id so a reused object cannot alias. Host: read-only, since
        /// <c>OnEnemyDied</c> must still find the entry to broadcast.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.ReleaseToPool))]
        public static void ReleaseToPool_Postfix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted() || __instance == null)
            {
                return;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;
            uint? id;
            if (isHost)
            {
                if (__instance.hp <= 0f)
                {
                    return;
                }

                var registered = enemyManagerService.GetEnemyByReference(__instance);
                id = registered.Value != null ? registered.Key : null;
            }
            else
            {
                id = enemyManagerService.RemoveEnemyByInstance(__instance);
                if (__instance.hp <= 0f)
                {
                    return;
                }
            }

            if (!id.HasValue)
            {
                return;
            }

            pooledWhileRegistered++;
            if (pooledWhileRegistered == 1 || pooledWhileRegistered % 25 == 0)
            {
                Plugin.Log.LogInfo(
                    $"[desync] OB-51: enemy {id.Value} ({__instance.enemyData?.enemyName}, hp {__instance.hp:0}) " +
                    $"went back to the pool alive while registered ({pooledWhileRegistered} so far, host {isHost}" +
                    (isHost ? ", still registered)." : ", unregistered here)."));
            }
        }

        /// <summary>
        /// OB-51: only the host despawns. <c>TryDespawn</c> (VA 0x1804B0640, decompiled) pools without
        /// <c>EnemyDied</c>; returns false on clients, which remove enemies via host messages.
        /// <b>UNVERIFIED</b> that this explains every client-only despawn observed (575 on 2026-10-20).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("TryDespawn")]
        public static bool TryDespawn_Prefix()
            => !synchronizationService.HasNetplaySessionStarted() || (synchronizationService.IsServerMode() ?? false);

        /// <summary>
        /// OB-9: replicate HP rescaled after spawn (the SpawnedEnemy message carried unscaled max HP).
        /// Patching the scaler covers all five callers (xref). Host only.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.SetSwarmMultiplierHp))]
        public static void SetSwarmMultiplierHp_Postfix(Enemy __instance)
        {
            if (__instance == null)
            {
                return;
            }

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return;
            }

            synchronizationService.OnEnemyHpRescaled(__instance);
        }
    }
}

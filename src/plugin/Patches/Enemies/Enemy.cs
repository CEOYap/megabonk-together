using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using MegabonkTogether.Helpers;
using MegabonkTogether.Scripts.Enemies;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Patches.Enemies
{
    [HarmonyPatch(typeof(Enemy))]
    internal static class EnemyPatch
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly IEnemyManagerService enemyManagerService = Plugin.Services.GetService<IEnemyManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        public static readonly HashSet<string> AllowedDamageSource = new(Enum.GetNames(typeof(EItem)));

        public static readonly DistanceThrottler EnemiesDistanceThrottler = new();

        /// <summary>
        /// Randomly target a player (Not necessarly the local player)
        /// Also adds TargetSwitcher component to the enemy for target switching
        /// </summary>
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
                // FIX P0-4: GetLocalPlayer() is nullable and was dereferenced unguarded on every
                // branch below. Hoisted to a single lookup so all three uses share one guard.
                // A null host means the local Player record is gone, which only happens as the
                // session is tearing down — leave targetId unset rather than throwing out of
                // InitEnemy and aborting the rest of enemy initialisation. Deliberately not
                // logged: InitEnemy runs per spawn, so a warning here would flood during a swarm.
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
                            // FIX P0-4: that peer's netplayer is gone — they disconnected. Fall
                            // back to the host so targetId is never left unset; TargetSwitcher
                            // moves it onto a real peer on its next switch.
                            DynamicData.For(__instance).Set("targetId", host.ConnectionId);
                        }
                    }
                }
                else if (host != null)
                {
                    DynamicData.For(__instance).Set("targetId", host.ConnectionId);
                }

                // PERF 1A: reuse an existing switcher rather than adding a second one. This method
                // is InitEnemy's postfix, and enemies are pooled — the Cleanup + renderer re-enable
                // at the top of this method only make sense for a recycled GameObject — so an
                // unconditional AddComponent stacked a new TargetSwitcher on the same object every
                // time it came back out of the pool. Each one ticked independently.
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

            var currentTracker = trackerService.GetCurrentPlayerId();
            if (currentTracker.HasValue && currentTracker.Value != playerManagerService.GetLocalPlayer().ConnectionId) { return; }

            trackerService.RegisterTrack();
        }

        /// <summary>
        /// Synchronize enemy death
        /// </summary>

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

            var currentTracker = trackerService.GetCurrentPlayerId();
            if (currentTracker.HasValue && currentTracker.Value != playerManagerService.GetLocalPlayer().ConnectionId) { return; }

            trackerService.RegisterTrack();
        }

        /// <summary>
        /// Same as above 
        /// </summary>
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

        /// <summary>
        /// Damage applied server-side only
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Enemy.Damage))]
        public static bool Damage_Prefix(Enemy __instance, DamageContainer damageContainer, out bool __state)
        {
            // __state = "this damage was suppressed". The postfix below still runs when this
            // prefix returns false -- Harmony does not skip postfixes -- and it broadcasts the hit,
            // so without this it would announce damage that never happened.
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            // OB-13, at the real funnel. The first fix gated DamageFromPlayerWeapon,
            // DamageFromPlayerOther and DamageExternal, and decompiling them shows all three do
            // nothing but call this private method -- but they are not the only way in. Our own
            // OnReceivedEnemyDamaged calls enemy.Damage(...) DIRECTLY (Il2CppInterop exposes
            // private members as public), so every hit replicated from the other peer walked past
            // all three gates and landed while the game was paused. An xref on Damage
            // (VA 0x1804AC7B0) gives exactly four callers: those three and Kill. This covers all
            // of them and ours.
            //
            // That route matters most under OB-21's phase slip, where the other peer was usually
            // NOT actually paused at the barrier and kept fighting through this peer's window.
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

        /// <summary>
        /// Synchronize enemy damage to all clients, synchronize item damage for client
        /// </summary>

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.Damage))]
        public static void Damage_Postfix(Enemy __instance, DamageContainer damageContainer, bool __state)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            // Suppressed by the OB-13 pause gate in the prefix: no damage was applied, so there is
            // nothing to replicate.
            if (__state)
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;

            // OB-34. A client applies damage only while replaying a hit the host sent it
            // (OnReceivedEnemyDamaged opens CAN_DAMAGE_ENEMIES for exactly that call). Such a hit is
            // never this client's to forward. Without this, an item hit the host forwarded was
            // forwarded straight back as new, and with two clients it ping-ponged through the host
            // until the enemy died, applying again on every lap: 2026-10-19, three players, 16-24k
            // EnemyDamaged/s from EACH client and 33k/s from the host (2.6 MB/s), a client so far
            // behind it never received the final boss's death.
            if (!isServer && Plugin.Instance.CAN_DAMAGE_ENEMIES)
            {
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

        /// <summary>
        /// Heal applied server-side only
        /// </summary>
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
        /// Distance-based throttling for non boss enemies
        /// only disables renderer at far distance for server and full throttling for clients
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

        /// <summary>
        /// Distance-based throttling for non boss enemies
        /// only disables renderer at far distance for server and full throttling for clients
        /// </summary>
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

        /// <summary>
        /// Cleanup throttler tracking when enemy dies
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.EnemyDied), typeof(DamageContainer))]
        public static void EnemyDied_Cleanup(Enemy __instance)
        {
            var instanceId = __instance.GetInstanceID();
            EnemiesDistanceThrottler.Cleanup(instanceId);
        }

        private static int pooledWhileRegistered;

        /// <summary>
        /// OB-51 diagnostic. <c>ReleaseToPool</c> (VA 0x1804AFC50, decompiled) is where every enemy
        /// leaves the field. A timed despawn (<c>TryDespawn</c>, 0x1804B0640, which starts the
        /// <c>Despawn</c> coroutine, which ends here) never calls <c>EnemyDied</c>, so nothing
        /// unregisters it: the registry keeps an entry for a pooled object on that peer. Says so,
        /// rarely, so a run shows whether that happens before it is replicated. Read-only.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.ReleaseToPool))]
        public static void ReleaseToPool_Postfix(Enemy __instance)
        {
            if (!synchronizationService.HasNetplaySessionStarted() || __instance == null || __instance.hp <= 0f)
            {
                return;
            }

            var registered = enemyManagerService.GetEnemyByReference(__instance);
            if (registered.Value == null)
            {
                return;
            }

            pooledWhileRegistered++;
            if (pooledWhileRegistered == 1 || pooledWhileRegistered % 25 == 0)
            {
                Plugin.Log.LogInfo(
                    $"[desync] OB-51: enemy {registered.Key} ({__instance.enemyData?.enemyName}, hp {__instance.hp:0}) " +
                    $"went back to the pool alive and still registered ({pooledWhileRegistered} so far, " +
                    $"host {synchronizationService.IsServerMode() ?? false}).");
            }
        }

        /// <summary>
        /// OB-9. Replicate an enemy's HP after it has been rescaled post-spawn.
        ///
        /// <para><b>The ordering that caused the bug.</b> Every scaler runs in the *caller* of
        /// <c>EnemyManager.SpawnEnemy</c>, after it has returned — and
        /// <c>EnemyManagerPatches.SpawnEnemy_Postfix</c> fires in between. So the
        /// <c>SpawnedEnemy</c> message went out carrying the enemy's *unscaled* HP. The client sets
        /// <c>hp</c>, <c>controlHp</c> and <c>maxHp</c> from that single value, and the 40 Hz
        /// <c>EnemyModel</c> delta carries current HP and nothing else — so current HP converged and
        /// the maximum stayed at the unscaled number for the rest of the run. That is exactly the
        /// report: current agrees between peers, maximum does not.</para>
        ///
        /// <para><b>Why this method and not each spawner.</b> An xref gives
        /// <c>SetSwarmMultiplierHp</c> five callers — <c>EnemyManager.SpawnBoss</c>,
        /// <c>SwarmSummoner.SpendCredits</c>, and the <c>SpawnEnemy</c> of
        /// <c>InteractableCharacterFight</c>, <c>InteractableDesertGrave</c> and
        /// <c>InteractableSkeletonKingStatue</c>. They share no other hook, and the three
        /// interactables are the "interactables that spawn bosses" in the report. Patching the one
        /// scaler covers all five and any future caller.</para>
        ///
        /// <para><b>The multiplier is <c>GameManager.bossCurses</c>-driven</b> (<c>SpawnBoss</c>
        /// applies it only when <c>bossCurses &gt; 0</c>), which is why an early-run boss can look
        /// correct and a late-run one cannot.</para>
        ///
        /// <para>Host only: the client never runs the scaler, because its spawn was suppressed. The
        /// enemy already carries a netplay id here — <c>SpawnEnemy_Postfix</c> registered it before
        /// the caller got the reference back.</para>
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

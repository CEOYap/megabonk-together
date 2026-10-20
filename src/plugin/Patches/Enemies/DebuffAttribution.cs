using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Game.Combat.EnemyDebuffs;
using HarmonyLib;
using MegabonkTogether.Patches.Items;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace MegabonkTogether.Patches.Enemies
{
    /// <summary>
    /// OB-40. A burn, poison or other debuff a teammate applied ticks as theirs on the host.
    ///
    /// <para><b>The defect</b> (decompiled, build 21750826). <c>Enemy.AddDebuff</c> (VA 0x1804AB190)
    /// buffers the debuff into <c>debuffsToAdd</c>, and every type ticks through the base
    /// <c>EnemyDebuff.Tick</c> (0x1804A9E20; the subclasses override only <c>MyTick</c>).
    /// <c>EnemyDebuff</c> keeps only its enemy and ticks left, not who applied it. So every tick
    /// ran outside any attribution scope: on the host it counted as the host's, with the host's
    /// DamageMultiplier, PoisonDamageMultiplier and baseDamage (DebuffFire, DebuffPoison), the
    /// host's kill credit for a damage-over-time kill, and the host's damage panel.</para>
    ///
    /// <para><b>The fix.</b> On the host, <c>AddDebuff</c> records the attacker the tracker holds at
    /// that moment, per enemy and debuff type; <c>Tick</c> reopens that scope when nothing else
    /// holds the tracker. The latest application wins: if the host or an unattributed source
    /// re-applies the debuff, the record is dropped, so a stale owner never outlives the next
    /// application. Several players stacking one debuff are credited to whoever applied it last.
    /// Cleared on a level change.</para>
    ///
    /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
    /// </summary>
    internal static class DebuffAttribution
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();

        private static readonly Dictionary<(int, int), uint> owners = new();

        internal readonly struct TickScope(bool began, float? savedBaseDamage)
        {
            public bool Began { get; } = began;
            public float? SavedBaseDamage { get; } = savedBaseDamage;
        }

        private static bool IsHostSession()
            => synchronizationService.HasNetplaySessionStarted() && (synchronizationService.IsServerMode() ?? false);

        internal static void OnAdded(Enemy enemy, EDebuff debuff)
        {
            if (enemy == null || !IsHostSession())
            {
                return;
            }

            var key = (enemy.GetInstanceID(), (int)debuff);
            var attacker = trackerService.GetCurrentPlayerId();
            if (attacker.HasValue && !playerManagerService.IsLocalConnectionId(attacker.Value))
            {
                owners[key] = attacker.Value;
            }
            else
            {
                owners.Remove(key);
            }
        }

        internal static TickScope BeginTick(EnemyDebuff debuff)
        {
            if (debuff == null || owners.Count == 0 || !IsHostSession() || trackerService.GetCurrentPlayerId().HasValue)
            {
                return default;
            }

            var enemy = debuff.enemy;
            if (enemy == null || !owners.TryGetValue((enemy.GetInstanceID(), (int)debuff.GetDebuffType()), out var owner))
            {
                return default;
            }

            trackerService.SetCurrentPlayerId(owner);

            // The burn debuff reads MyPlayer.baseDamage as a raw field (DebuffFire.GetDamage).
            return new TickScope(true, TeammateCombatStats.EnterBaseDamage(owner));
        }

        internal static void EndTick(TickScope scope)
        {
            if (!scope.Began)
            {
                return;
            }

            TeammateCombatStats.ExitBaseDamage(scope.SavedBaseDamage);
            trackerService.UnsetCurrentPlayerId();
        }

        internal static void Clear() => owners.Clear();
    }

    [HarmonyPatch(typeof(Enemy))]
    internal static class EnemyAddDebuffAttributionPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Enemy.AddDebuff))]
        public static void AddDebuff_Postfix(Enemy __instance, EDebuff eDebuff)
            => DebuffAttribution.OnAdded(__instance, eDebuff);
    }

    [HarmonyPatch(typeof(EnemyDebuff))]
    internal static class EnemyDebuffTickAttributionPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemyDebuff.Tick))]
        public static void Tick_Prefix(EnemyDebuff __instance, out DebuffAttribution.TickScope __state)
            => __state = DebuffAttribution.BeginTick(__instance);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(EnemyDebuff.Tick))]
        public static void Tick_Finalizer(DebuffAttribution.TickScope __state)
            => DebuffAttribution.EndTick(__state);
    }

    /// <summary>
    /// OB-50. Bloodmark (Bloody Cleaver) does its damage outside both the hit and the debuff tick.
    /// <c>DebuffBloodmark.AddStacks</c> (VA 0x1804A65F0, decompiled) computes
    /// <c>DamageMultiplier x baseDamageMultiplier x MyPlayer.baseDamage x stacks</c> and executes the
    /// enemy once that reaches its HP; <c>MyTick</c> is an empty stub. The other execute is in
    /// <c>OnRemove</c> (0x1804A69A0): <c>OnEnemyDamaged</c> (0x1804A68B0) queues the mark for removal
    /// (<c>debuffsToRemove</c>, 0x130) when a hit leaves the enemy at or under the mark's damage, and
    /// <c>Enemy.DebuffTick</c> (0x1804ACC60) later removes it; unless the enemy is already dying,
    /// <c>OnRemove</c> then deals the mark's damage through <c>DamageFromPlayerOther</c>. So the execute ran
    /// unattributed on the host, with the host's stats, and went on the host's panel: 2026-10-20, a
    /// "Bloodmark 213" row on the host for a mark from Player2's Bloody Cleaver. Scoped to whoever
    /// applied the mark, exactly as <c>EnemyDebuff.Tick</c> is. <b>UNVERIFIED.</b>
    /// </summary>
    [HarmonyPatch(typeof(Assets.Scripts.Game.Combat.EnemyDebuffs.Implementations.DebuffBloodmark))]
    internal static class DebuffBloodmarkAttributionPatches
    {
        // OB-52. The removal execute. It runs from Enemy.DebuffTick, with no tracker scope, so it
        // went on the host's panel as the host's damage (2026-10-20: Bloodmark 2,395, then 1,550,
        // on a host holding no Bloodmark item). The first fix assumed OnEnemyDamaged re-added the
        // mark; decompiled with dump.cs offsets, it queues the removal instead.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Assets.Scripts.Game.Combat.EnemyDebuffs.Implementations.DebuffBloodmark.OnRemove))]
        public static void OnRemove_Prefix(Assets.Scripts.Game.Combat.EnemyDebuffs.Implementations.DebuffBloodmark __instance, out DebuffAttribution.TickScope __state)
            => __state = DebuffAttribution.BeginTick(__instance);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(Assets.Scripts.Game.Combat.EnemyDebuffs.Implementations.DebuffBloodmark.OnRemove))]
        public static void OnRemove_Finalizer(DebuffAttribution.TickScope __state)
            => DebuffAttribution.EndTick(__state);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Assets.Scripts.Game.Combat.EnemyDebuffs.Implementations.DebuffBloodmark.AddStacks))]
        public static void AddStacks_Prefix(Assets.Scripts.Game.Combat.EnemyDebuffs.Implementations.DebuffBloodmark __instance, out DebuffAttribution.TickScope __state)
            => __state = DebuffAttribution.BeginTick(__instance);

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(Assets.Scripts.Game.Combat.EnemyDebuffs.Implementations.DebuffBloodmark.AddStacks))]
        public static void AddStacks_Finalizer(DebuffAttribution.TickScope __state)
            => DebuffAttribution.EndTick(__state);
    }
}

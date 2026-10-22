using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Game.Combat.EnemyDebuffs;
using HarmonyLib;
using OmniBonk.Patches.Items;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;

namespace OmniBonk.Patches.Enemies
{
    /// <summary>
    /// OB-40: a teammate's debuff ticks as theirs on the host. Decompiled: <c>EnemyDebuff</c> does
    /// not record who applied it, and all types tick via base <c>EnemyDebuff.Tick</c> (VA 0x1804A9E20).
    /// <para>The host records the tracker's attacker per (enemy, debuff type) at <c>AddDebuff</c> and
    /// reopens that scope in <c>Tick</c>. Latest application wins. Cleared on level change.</para>
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
    /// OB-50: Bloodmark executes outside the hit and the tick, in <c>AddStacks</c> (VA 0x1804A65F0)
    /// and <c>OnRemove</c> (VA 0x1804A69A0); <c>MyTick</c> is empty (decompiled). Both are scoped to
    /// whoever applied the mark. <b>UNVERIFIED.</b>
    /// </summary>
    [HarmonyPatch(typeof(Assets.Scripts.Game.Combat.EnemyDebuffs.Implementations.DebuffBloodmark))]
    internal static class DebuffBloodmarkAttributionPatches
    {
        // OB-52: the removal execute runs from Enemy.DebuffTick with no tracker scope.
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

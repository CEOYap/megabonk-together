using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches
{
    /// <summary>
    /// OB-14. Keeps a teammate's damage out of this player's damage panel. RunStats.OnEnemyDamaged
    /// (VA 0x180411930, decompiled) records every hit applied on this machine, teammates' included.
    /// <para>Owner comes from the tracker; unattributed hits count as ours. Backstop:
    /// <see cref="IsTeammatesSource"/>.</para>
    /// </summary>
    [HarmonyPatch(typeof(Assets.Scripts.Saves___Serialization.Progression.Stats.RunStats))]
    internal static class RunStatsPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        [HarmonyPrefix]
        [HarmonyPatch("OnEnemyDamaged")]
        public static bool OnEnemyDamaged_Prefix(Enemy enemy, DamageContainer dc)
        {
            // Initialized, not Started: see IsTeammatesSource. No ownership guard needed: the panel is local.
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            // The original throws on a null source; skipping loses nothing.
            if (string.IsNullOrEmpty(dc?.damageSource))
            {
                return false;
            }

            uint? attacker = null;

            // A replayed hit carries its attacker in every state, including after the run stops.
            var replaying = Plugin.Instance.CAN_DAMAGE_ENEMIES;
            if (synchronizationService.HasNetplaySessionStarted() || replaying)
            {
                attacker = trackerService.GetCurrentPlayerId();

                // Prefix-false is safe: the original only adds to the panel's table.
                if (attacker.HasValue && !playerManagerService.IsLocalConnectionId(attacker.Value))
                {
                    return false;
                }
            }

            var source = dc?.damageSource;
            if (IsTeammatesSource(source))
            {
                return false;
            }

            ReportForeignSource(source, attacker);
            return true;
        }

        /// <summary>
        /// OB-14. True when <paramref name="source"/> is a weapon/item we lack and a teammate holds: a
        /// row the panel would show at LVL 0. Mirrors DamageSource.GetLevel (VA 0x1804011E0,
        /// decompiled). Needs no tracker, so it works outside Started. A teammate must hold it so a
        /// game effect sharing the name is not dropped.
        /// </summary>
        private static bool IsTeammatesSource(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return false;
            }

            if (!parsedSources.TryGetValue(source, out var parsed))
            {
                parsed = Parse(source);
                parsedSources[source] = parsed;
            }

            if (parsed.Kind == SourceKind.Other)
            {
                return false;
            }

            // OB-47: no local inventory means we cannot hold it; let the teammate check decide.
            var local = GameManager.Instance?.player?.inventory;
            if (local != null && Holds(local, parsed))
            {
                return false;
            }

            // Inventory store, not avatars: avatars are cleared at the portal while projectiles still fly.
            foreach (var inventory in playerManagerService.GetRemoteInventories())
            {
                if (inventory == null || !Holds(inventory, parsed))
                {
                    continue;
                }

                if (droppedSources.Add(source))
                {
                    Plugin.Log.LogInfo(
                        $"OB-14: kept '{source}' off our damage panel -- a teammate's {parsed.Kind.ToString().ToLowerInvariant()} " +
                        $"we do not hold (session started {synchronizationService.HasNetplaySessionStarted()}, " +
                        $"replaying {Plugin.Instance.CAN_DAMAGE_ENEMIES}, host {synchronizationService.IsServerMode() ?? false}).");
                }

                return true;
            }

            return false;
        }

        private static bool Holds(PlayerInventory inventory, ParsedSource parsed)
        {
            if (parsed.Kind == SourceKind.Weapon)
            {
                var weapons = inventory.weaponInventory?.weapons;
                return weapons != null && weapons.ContainsKey(parsed.Weapon);
            }

            var items = inventory.itemInventory?.items;
            return items != null && items.ContainsKey(parsed.Item);
        }

        private enum SourceKind { Other, Weapon, Item }

        private readonly record struct ParsedSource(SourceKind Kind, EWeapon Weapon, EItem Item);

        /// <summary>Same order and case rule as DamageSource.GetLevel: weapon first, then item.</summary>
        private static ParsedSource Parse(string source)
        {
            if (System.Enum.TryParse(source, true, out EWeapon weapon))
            {
                return new ParsedSource(SourceKind.Weapon, weapon, default);
            }

            if (System.Enum.TryParse(source, true, out EItem item))
            {
                return new ParsedSource(SourceKind.Item, default, item);
            }

            return new ParsedSource(SourceKind.Other, default, default);
        }

        /// <summary>Source string to its parse. Damage sources are a small, fixed set of names.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, ParsedSource> parsedSources = [];

        /// <summary>Sources already logged as kept off the panel, once each per session.</summary>
        private static readonly System.Collections.Generic.HashSet<string> droppedSources = [];

        private static readonly System.Collections.Generic.HashSet<string> checkedSources = [];

        /// <summary>
        /// OB-14 diagnostic: logs once per source when we record damage from a source we do not own.
        /// Status effects are reported too; that is expected.
        /// </summary>
        private static void ReportForeignSource(string source, uint? attacker)
        {
            if (string.IsNullOrEmpty(source) || checkedSources.Contains(source))
            {
                return;
            }

            // Before the once-per-source mark (OB-47).
            var inventory = GameManager.Instance?.player?.inventory;
            if (inventory == null)
            {
                return;
            }

            checkedSources.Add(source);

            if (inventory.weaponInventory?.weapons != null)
            {
                foreach (var weapon in inventory.weaponInventory.weapons.Keys)
                {
                    if (weapon.ToString() == source)
                    {
                        return;
                    }
                }
            }

            if (inventory.itemInventory?.items != null)
            {
                foreach (var item in inventory.itemInventory.items.Keys)
                {
                    if (item.ToString() == source)
                    {
                        return;
                    }
                }
            }

            var how = attacker.HasValue ? $"attributed to us ({attacker.Value})" : "unattributed";
            Plugin.Log.LogInfo(
                $"OB-14: recording '{source}' as our damage, but we own no weapon or item by that name -- " +
                $"{how}, replaying {Plugin.Instance.CAN_DAMAGE_ENEMIES}, host {synchronizationService.IsServerMode() ?? false}. " +
                "A teammate's source here is the leak; a status effect is expected.");
        }

        /// <summary>Clears the once-per-source memory so each session reports afresh.</summary>
        internal static void ResetDiagnostics()
        {
            checkedSources.Clear();
            droppedSources.Clear();
        }
    }
}

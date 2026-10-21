using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// OB-14. Keeps a teammate's damage out of this player's end-of-run damage panel.
    ///
    /// <para><b>The defect.</b> RunStats.OnEnemyDamaged (VA 0x180411930, decompiled) is a static
    /// handler on the global Enemy.A_Damage event. It looks the hit's damageSource up in a
    /// per-source table and adds the damage -- that table is the panel. It runs for every hit
    /// applied on this machine, and in netplay that includes the teammate's: hits replayed from
    /// EnemyDamaged, and on the host the projectiles and auras it simulates for remote players. So
    /// each player's panel listed the other's weapons and items at nearly their full totals, at
    /// LVL 0 because the viewer does not own them. Confirmed from both end screens of the
    /// 2026-10-11 run: Wireless Dagger at LVL 0 / 644.5K on the host against LVL 10 / 602.4K on the
    /// client that owns it; Lightning Staff at LVL 0 / 1.1M on the client against LVL 8 / 1.2M on
    /// the host that owns it.</para>
    ///
    /// <para><b>How the owner is known.</b> Every path that applies a teammate's hit already tells
    /// the tracker whose hit it is, for kill credit: OnReceivedEnemyDamaged sets the attacker,
    /// ProjectileBase.HitEnemy sets the weapon's owner on the host, and CombatAura now does the same.
    /// This only reads it. A hit with no attribution is treated as ours, which is what it was
    /// before, so a gap in attribution can only under-correct, never hide our own damage.</para>
    ///
    /// <para><b>Backstop.</b> The tracker is only scoped while the session is Started, so a second
    /// test that needs no tracker drops a weapon or item source we lack and a teammate holds, in
    /// every in-session state. See IsTeammatesSource.</para>
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
            // Initialized, not Started: see IsTeammatesSource. Outside netplay there are no spawned
            // teammates, so the ownership filter cannot drop anything there anyway.
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            // The original throws on a null source (Dictionary.TryGetValue with a null key, seen at
            // a run's end on 2026-10-21), so there is nothing to record and nothing lost.
            if (string.IsNullOrEmpty(dc?.damageSource))
            {
                return false;
            }

            uint? attacker = null;

            // A replayed hit (OnReceivedEnemyDamaged) carries its attacker in the tracker in every
            // state, so it is attributed even after the run has stopped being Started. At a run's
            // end the teammates' inventories are already cleared, which left a teammate's Cactus
            // hit with nobody to match and it landed as a LVL 0 row (2026-10-21, Player3).
            var replaying = Plugin.Instance.CAN_DAMAGE_ENEMIES;
            if (synchronizationService.HasNetplaySessionStarted() || replaying)
            {
                attacker = trackerService.GetCurrentPlayerId();

                // Record only our own damage. Prefix-false is deliberate: the original does nothing
                // but add to the panel's per-source table.
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
        /// OB-14, the LVL 0 rows themselves. True when <paramref name="source"/> names a weapon or
        /// item the local player does not hold and a teammate does: a row the panel could only ever
        /// show at LVL 0, for damage that cannot be ours.
        ///
        /// <para><b>Why this is exactly "LVL 0".</b> The panel's level is DamageSource.GetLevel
        /// (VA 0x1804011E0, decompiled): Enum.TryParse the source as EWeapon, ignoring case, and
        /// return the local WeaponInventory.GetWeaponLevel; failing that, as EItem and return
        /// ItemInventory.GetAmount; anything else shows 1. Both getters (VA 0x180432B20 and
        /// 0x1804427E0, decompiled) return 0 exactly when the key is not in the dictionary. This is
        /// the same test in the same order, so it drops nothing the panel would show at a real
        /// level.</para>
        ///
        /// <para><b>Outside Started.</b> The tracker filter above only holds while the session is
        /// Started, because every patch that scopes the tracker is gated on that. The panel kept
        /// recording through the other in-session states (Ready, LoadingNextLevel, Endgame), and the
        /// old prefix let every hit through there, a teammate's included, without reaching the
        /// diagnostic below. That is why the 2026-10-19 run's LVL 0 rows (host: Soul Harvester 945,
        /// Black Hole 412, Aura 109; a client: Flamewalker 5,772, Soul Harvester 680) produced no
        /// "OB-14: recording" line on any peer. This test needs no tracker, so it holds in every
        /// state, and while Started it also catches a teammate hit the tracker left unattributed
        /// (UNRESOLVED-WEAPON).</para>
        ///
        /// <para><b>Why a teammate must hold it.</b> Not holding the source is not enough on its
        /// own: a game effect could carry the name of a weapon or item we lack, and dropping it
        /// would hide real damage. Requiring a teammate who holds it means the row was theirs. A
        /// source we both hold is left to the tracker; outside Started such a hit can still inflate
        /// our row, but at our real level, not as a LVL 0 row.</para>
        ///
        /// <para>Cost: reached only by hits about to be recorded. A source we hold costs one native
        /// ContainsKey; the teammate scan runs only for a source we do not hold.</para>
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

            // OB-47: a missing local inventory (during a level load, or with the player object gone)
            // used to end the test here and let a teammate's row through as ours: the host's
            // 2026-10-20 panel kept a LVL 0 Mirror (997) with no "kept" or "recording" line. Without
            // a local inventory we cannot hold the source, so only the teammate check decides.
            var local = GameManager.Instance?.player?.inventory;
            if (local != null && Holds(local, parsed))
            {
                return false;
            }

            // The inventory store, not the spawned avatars: ResetForNextLevel clears the avatars at
            // the portal, and a teammate's projectiles still in flight then landed with nobody to
            // match. 2026-10-19: the host's panel kept a LVL 0 Wireless Dagger (24) from exactly
            // that, logged right after "Cleared all player cards".
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
        /// OB-14 diagnostic. The first time a source is about to be recorded as ours, checks that we
        /// own a weapon or item of that name, and says which path recorded it if we do not.
        ///
        /// <para><b>Why.</b> On 2026-10-18 the client's panel listed the host's Aura (2,331) and
        /// Chunkers (946) at LVL 0. Every path read offline attributes those hits to the host --
        /// the host stamps <c>AttackerId</c> from the tracker, and the client replays with it -- so
        /// whatever records them is a path not yet found. "Unattributed" means a hit applied here
        /// with no tracker scope at all; "attributed to us" means something stamped our id on it.
        /// <c>replaying</c> is true inside <c>OnReceivedEnemyDamaged</c>.</para>
        ///
        /// <para>Once per source per session, so it costs a set lookup per hit. Status effects
        /// (Burn, Poison, ...) are not weapons or items and will be reported once each; that is
        /// expected, not a leak.</para>
        /// </summary>
        private static void ReportForeignSource(string source, uint? attacker)
        {
            if (string.IsNullOrEmpty(source) || checkedSources.Contains(source))
            {
                return;
            }

            // Checked before the once-per-source mark, so a hit that arrives while the local
            // inventory is missing does not use up the one log line for that source (OB-47).
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

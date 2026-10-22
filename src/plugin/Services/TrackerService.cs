using Assets.Scripts.Inventory__Items__Pickups.Items;
using System;
using System.Threading;

namespace OmniBonk.Services
{
    /// <summary>
    /// P1-5: instrumentation for whether nested damage/death handling clobbers the single
    /// <see cref="TrackerService.currentPlayerId"/> credit field. Counts, not per-event logs.
    /// Uses <see cref="DateTime.UtcNow"/>: may be called off the main thread.
    /// </summary>
    internal static class TrackerAttributionDiagnostics
    {
        private const double REPORT_INTERVAL_SECONDS = 5d;

        private static long lastReportTicks;
        private static int firstThreadId;

        /// <summary>Set() while a DIFFERENT id was already live — the outer attribution is lost.</summary>
        private static int overwriteWhileSet;

        /// <summary>Set() while the SAME id was live — harmless, but proves nesting occurs.</summary>
        private static int redundantSet;

        /// <summary>
        /// Unset() with nothing set. Harmless; common on clients because Harmony runs the postfix
        /// even when the prefix returns false.
        /// </summary>
        private static int unsetWhileClear;

        /// <summary>
        /// The one that misattributes: an Unset() clearing an attribution it did not set (depth &lt; 0).
        /// EnemyDied_Prefix then reads null as "mine".
        /// </summary>
        private static int unbalancedUnset;

        private static int setDepth;

        private static int crossThreadCalls;

        /// <summary>OB-14: hits whose weapon resolved to no peer. Invisible to the pairing counters above.</summary>
        private static int unresolvedWeaponHits;

        /// <summary>OB-14: for the ratio.</summary>
        private static int resolvedWeaponHits;

        /// <summary>
        /// OB-14: host, once per hit. Read the ratio: the host's own weapons legitimately resolve to no net player.
        /// </summary>
        internal static void RecordWeaponOwnerLookup(bool resolved)
        {
            if (resolved)
            {
                Interlocked.Increment(ref resolvedWeaponHits);
            }
            else
            {
                Interlocked.Increment(ref unresolvedWeaponHits);
            }
        }

        /// <summary>OB-41: unresolved hits from the host's own weapons (expected).</summary>
        private static int ownWeaponHits;

        private static int loggedUnknownSamples;

        /// <summary>OB-41.</summary>
        internal static void RecordOwnWeaponHit() => Interlocked.Increment(ref ownWeaponHits);

        /// <summary>True for the first few unknown hits per session.</summary>
        internal static bool TakeUnknownSample() => Interlocked.Increment(ref loggedUnknownSamples) <= 5;

        internal static void LogUnknownWeaponHit(string projectileType, bool weaponNull, string weaponName)
        {
            Plugin.Log.LogInfo(
                $"[attribution] OB-41: a {projectileType} hit has no owner -- weaponBase " +
                (weaponNull ? "null" : $"'{weaponName}', held by no player") +
                ". It counts as the host's (stats, items, kill).");
        }

        private static string lastOverwrite = "none";

        internal static void RecordSet(uint? previous, uint incoming)
        {
            NoteThread();

            if (previous.HasValue)
            {
                if (previous.Value != incoming)
                {
                    Interlocked.Increment(ref overwriteWhileSet);
                    lastOverwrite = $"{previous.Value} -> {incoming}";
                }
                else
                {
                    Interlocked.Increment(ref redundantSet);
                }
            }

            Interlocked.Increment(ref setDepth);

            MaybeReport();
        }

        internal static void RecordUnset(bool wasSet)
        {
            NoteThread();

            if (!wasSet)
            {
                Interlocked.Increment(ref unsetWhileClear);
            }

            if (Interlocked.Decrement(ref setDepth) < 0)
            {
                Interlocked.Exchange(ref setDepth, 0);

                if (wasSet)
                {
                    Interlocked.Increment(ref unbalancedUnset);
                }
            }

            MaybeReport();
        }

        private static void NoteThread()
        {
            var id = Thread.CurrentThread.ManagedThreadId;
            var first = Interlocked.CompareExchange(ref firstThreadId, id, 0);

            if (first != 0 && first != id)
            {
                Interlocked.Increment(ref crossThreadCalls);
            }
        }

        private static void MaybeReport()
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref lastReportTicks);

            if (new TimeSpan(nowTicks - last).TotalSeconds < REPORT_INTERVAL_SECONDS)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref lastReportTicks, nowTicks, last) != last)
            {
                return;
            }

            var overwrites = Interlocked.Exchange(ref overwriteWhileSet, 0);
            var redundant = Interlocked.Exchange(ref redundantSet, 0);
            var strayUnsets = Interlocked.Exchange(ref unsetWhileClear, 0);
            var unbalanced = Interlocked.Exchange(ref unbalancedUnset, 0);
            var crossThread = Interlocked.Exchange(ref crossThreadCalls, 0);
            var unresolved = Interlocked.Exchange(ref unresolvedWeaponHits, 0);
            var resolved = Interlocked.Exchange(ref resolvedWeaponHits, 0);
            var own = Interlocked.Exchange(ref ownWeaponHits, 0);
            var unknown = unresolved - own;

            if ((overwrites | redundant | strayUnsets | unbalanced | crossThread) == 0
                && unknown <= 0)
            {
                return; // nothing to say; do not spam a healthy session
            }

            Plugin.Log.LogWarning(
                "Kill-attribution anomalies in the last ~5s - " +
                $"UNBALANCED-UNSET: {unbalanced}, " +
                $"overwrite-while-set: {overwrites} (last {lastOverwrite}), " +
                $"unset-while-clear: {strayUnsets}, " +
                $"redundant-set: {redundant}, " +
                $"cross-thread: {crossThread}, " +
                $"UNRESOLVED-WEAPON: {unknown} of {unresolved + resolved} hit(s) ({own} more were the host's own). " +
                "Only UNBALANCED-UNSET and overwrite-while-set misattribute credit. " +
                "UNRESOLVED-WEAPON is OB-14/OB-41: a hit whose weapon nobody holds, which runs as " +
                "the host's. The host's own weapons are counted separately now (OB-41), so any " +
                "UNRESOLVED-WEAPON is a teammate hit that lost its owner. " +
                "See P1-5 in docs/netplay/01-critical-fixes.md and OB-14 in 08-observed-bugs.md.");
        }

        internal static void Reset()
        {
            Interlocked.Exchange(ref lastReportTicks, 0);
            Interlocked.Exchange(ref firstThreadId, 0);
            Interlocked.Exchange(ref unresolvedWeaponHits, 0);
            Interlocked.Exchange(ref resolvedWeaponHits, 0);
            Interlocked.Exchange(ref ownWeaponHits, 0);
            Interlocked.Exchange(ref loggedUnknownSamples, 0);
            Interlocked.Exchange(ref overwriteWhileSet, 0);
            Interlocked.Exchange(ref redundantSet, 0);
            Interlocked.Exchange(ref unsetWhileClear, 0);
            Interlocked.Exchange(ref unbalancedUnset, 0);
            Interlocked.Exchange(ref setDepth, 0);
            Interlocked.Exchange(ref crossThreadCalls, 0);
            lastOverwrite = "none";
        }
    }

    public class Tracks
    {
        public uint moneyFlying { get; set; } = 0;
        public uint kills { get; set; } = 0;
    }

    public interface ITrackerService
    {

        public void SetCurrentPlayerId(uint playerId);
        public uint? GetCurrentPlayerId();
        public void UnsetCurrentPlayerId();
        public void RegisterTrack();

        public Tracks GetPlayerTrack();
    }

    internal class TrackerService : ITrackerService
    {
        private uint? currentPlayerId;
        private Tracks playerTrack = new();

        public void SetCurrentPlayerId(uint playerId)
        {
            TrackerAttributionDiagnostics.RecordSet(currentPlayerId, playerId);
            currentPlayerId = playerId;
        }

        public uint? GetCurrentPlayerId()
        {
            return currentPlayerId;
        }

        public void UnsetCurrentPlayerId()
        {
            TrackerAttributionDiagnostics.RecordUnset(currentPlayerId.HasValue);
            currentPlayerId = null;
        }

        public void RegisterTrack()
        {
            uint moneyFlying = 1;
            uint kill = 1;

            playerTrack.moneyFlying += moneyFlying;
            playerTrack.kills += kill;
        }

        public Tracks GetPlayerTrack()
        {
            return playerTrack;
        }
    }
}

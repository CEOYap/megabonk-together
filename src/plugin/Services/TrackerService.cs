using Assets.Scripts.Inventory__Items__Pickups.Items;
using System;
using System.Threading;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// P1-5 follow-up. <see cref="TrackerService.currentPlayerId"/> is a single process-wide field
    /// that decides who gets kill / money-flying / item-proc credit, and
    /// <see cref="TrackerService.UnsetCurrentPlayerId"/> clears it unconditionally. The suspicion
    /// is that nested or interleaved damage/death handling clobbers it, so a kill lands on the
    /// wrong player — which would fit the "intermittent, worse under load" reports that P1-5
    /// originally blamed on <c>DamageContainer</c> (that turned out to be write-only dead code).
    ///
    /// This is instrumentation, not a fix. It is designed to falsify the hypothesis as readily as
    /// confirm it: if every counter stays at zero across a real session, the suspicion is wrong
    /// and the P1-5 note should be struck.
    ///
    /// Counts rather than logs per event, following <c>TransformFallbackDiagnostics</c> — the rate
    /// is the diagnosis. A handful per session means a rare interleave; hundreds means the pairing
    /// is structurally broken.
    ///
    /// Deliberately uses <see cref="DateTime.UtcNow"/> rather than <c>Time.unscaledTime</c>:
    /// <c>UdpClientService.Poll()</c> runs on a background task during connection setup as well as
    /// from <c>NetworkHandler.Update()</c>, and a Unity API off the main thread throws. A
    /// diagnostic must not be able to crash the thing it is measuring.
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
        /// Unset() with nothing set. Expected to be the common one: `ProjectileBase.HitEnemy_Postfix`
        /// unsets unconditionally while its prefix only sets when host AND the weapon maps to a
        /// remote netplayer, so the pairing is asymmetric by construction.
        /// </summary>
        private static int unsetWhileClear;

        /// <summary>RegisterTrack() with no attribution set — credit falls to the local player by default.</summary>
        private static int trackWithNoOwner;

        private static int crossThreadCalls;

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

            MaybeReport();
        }

        internal static void RecordUnset(bool wasSet)
        {
            NoteThread();

            if (!wasSet)
            {
                Interlocked.Increment(ref unsetWhileClear);
            }

            MaybeReport();
        }

        internal static void RecordTrack(bool hasOwner)
        {
            NoteThread();

            if (!hasOwner)
            {
                Interlocked.Increment(ref trackWithNoOwner);
            }

            MaybeReport();
        }

        /// <summary>
        /// Records whether these calls ever arrive on more than one thread. If they do, the field is
        /// racing regardless of nesting and that is a different bug with the same symptom.
        /// </summary>
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

            // Only the thread that wins this swap reports, so concurrent hits cannot double-log.
            if (Interlocked.CompareExchange(ref lastReportTicks, nowTicks, last) != last)
            {
                return;
            }

            var overwrites = Interlocked.Exchange(ref overwriteWhileSet, 0);
            var redundant = Interlocked.Exchange(ref redundantSet, 0);
            var strayUnsets = Interlocked.Exchange(ref unsetWhileClear, 0);
            var ownerless = Interlocked.Exchange(ref trackWithNoOwner, 0);
            var crossThread = Interlocked.Exchange(ref crossThreadCalls, 0);

            if ((overwrites | redundant | strayUnsets | ownerless | crossThread) == 0)
            {
                return; // nothing to say; do not spam a healthy session
            }

            Plugin.Log.LogWarning(
                "Kill-attribution anomalies in the last ~5s — " +
                $"overwrite-while-set: {overwrites} (last {lastOverwrite}), " +
                $"redundant-set: {redundant}, " +
                $"unset-while-clear: {strayUnsets}, " +
                $"track-with-no-owner: {ownerless}, " +
                $"cross-thread: {crossThread}. " +
                "overwrite-while-set and track-with-no-owner are the ones that misattribute credit; " +
                "see P1-5 in docs/netplay/01-critical-fixes.md.");
        }

        /// <summary>Clears counters and the throttle so each session starts from zero.</summary>
        internal static void Reset()
        {
            Interlocked.Exchange(ref lastReportTicks, 0);
            Interlocked.Exchange(ref firstThreadId, 0);
            Interlocked.Exchange(ref overwriteWhileSet, 0);
            Interlocked.Exchange(ref redundantSet, 0);
            Interlocked.Exchange(ref unsetWhileClear, 0);
            Interlocked.Exchange(ref trackWithNoOwner, 0);
            Interlocked.Exchange(ref crossThreadCalls, 0);
            lastOverwrite = "none";
        }
    }

    public class Tracks
    {
        public uint moneyFlying { get; set; } = 0;
        public uint itemProcs { get; set; } = 0;
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
            TrackerAttributionDiagnostics.RecordTrack(currentPlayerId.HasValue);

            uint moneyFlying = 1;
            uint kill = 1;
            uint itemProcs = 0;

            var inventory = GameManager.Instance.player.inventory;
            if (inventory.itemInventory.items.Keys.System_Collections_Generic_ICollection_TKey__Contains(EItem.SoulHarvester))
            {
                itemProcs += 1;
            }

            if (inventory.itemInventory.items.Keys.System_Collections_Generic_ICollection_TKey__Contains(EItem.SluttyCannon))
            {
                itemProcs += 1;
            }

            if (inventory.itemInventory.items.Keys.System_Collections_Generic_ICollection_TKey__Contains(EItem.MoldyCheese))
            {
                itemProcs += 1;
            }

            playerTrack.moneyFlying += moneyFlying;
            playerTrack.itemProcs += itemProcs;
            playerTrack.kills += kill;
        }

        public Tracks GetPlayerTrack()
        {
            return playerTrack;
        }
    }
}

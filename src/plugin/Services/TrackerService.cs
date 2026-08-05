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
        /// Unset() with nothing set. CONFIRMED in a 3-player session: 0 on the host, up to 1042 per
        /// 5s on clients. `ProjectileBase.HitEnemy_Postfix` unsets unconditionally while its prefix
        /// only sets when host AND the weapon maps to a remote netplayer — and Harmony runs the
        /// postfix even when the prefix returns false, which it does for clients. Harmless in
        /// itself: nothing was pending to lose.
        /// </summary>
        private static int unsetWhileClear;

        /// <summary>
        /// <b>The one that actually misattributes.</b> An Unset() that clears a live attribution
        /// it did not set — i.e. depth would go negative. That takes someone else's credit away
        /// mid-flight, and because EnemyDied_Prefix reads a null owner as "mine", the credit is
        /// then awarded to whoever is running the code rather than merely lost.
        ///
        /// <para>The first version of this class did not count this at all. It only counted
        /// <see cref="unsetWhileClear"/> — the harmless direction — so the 3-player run could rule
        /// out a race and an overwrite but could not rule this out. Depth tracking fixes that.</para>
        /// </summary>
        private static int unbalancedUnset;

        /// <summary>
        /// Nesting depth: incremented on Set, decremented on Unset. A decrement that would take it
        /// below zero is an <see cref="unbalancedUnset"/>.
        /// </summary>
        private static int setDepth;

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

            // An unset that takes depth negative is one that never had a matching set. If an
            // attribution was live at the time, that unset just destroyed it.
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
            var unbalanced = Interlocked.Exchange(ref unbalancedUnset, 0);
            var crossThread = Interlocked.Exchange(ref crossThreadCalls, 0);

            if ((overwrites | redundant | strayUnsets | unbalanced | crossThread) == 0)
            {
                return; // nothing to say; do not spam a healthy session
            }

            Plugin.Log.LogWarning(
                "Kill-attribution anomalies in the last ~5s — " +
                $"UNBALANCED-UNSET: {unbalanced}, " +
                $"overwrite-while-set: {overwrites} (last {lastOverwrite}), " +
                $"unset-while-clear: {strayUnsets}, " +
                $"redundant-set: {redundant}, " +
                $"cross-thread: {crossThread}. " +
                "Only UNBALANCED-UNSET and overwrite-while-set misattribute credit. " +
                "See P1-5 in docs/netplay/01-critical-fixes.md.");
        }

        /// <summary>Clears counters and the throttle so each session starts from zero.</summary>
        internal static void Reset()
        {
            Interlocked.Exchange(ref lastReportTicks, 0);
            Interlocked.Exchange(ref firstThreadId, 0);
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
            // NOTE: the first version of this class counted RegisterTrack-with-no-owner here as an
            // anomaly. It is not one. On the host, killing an enemy with your own weapon leaves
            // currentPlayerId null by design — HitEnemy_Prefix only sets an owner when the weapon
            // maps to a *remote* netplayer — so a null owner is the normal "I killed it" path, and
            // EnemyDied_Prefix reading null as "mine" is correct there. The 3-player run showed
            // 2-11 per 5s on the host and 0 on clients, which is just the host's own kill rate.
            // Counter removed rather than relabelled: it measured nothing actionable.

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

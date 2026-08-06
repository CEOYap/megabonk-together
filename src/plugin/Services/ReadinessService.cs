using System;
using System.Collections.Generic;
using System.Linq;

namespace MegabonkTogether.Services
{
    public interface IReadinessService
    {
        /// <summary>The round this peer is participating in. Zero means "no round open".</summary>
        uint SessionId { get; }

        /// <summary>1-based. Zero means "no round open" and is never a valid round.</summary>
        uint RoundId { get; }

        /// <summary>True once this peer holds a host-assigned stamp it can report against.</summary>
        bool HasStamp { get; }

        /// <summary>Host. Opens the next round over the given participants. Returns the new stamp.</summary>
        (uint SessionId, uint RoundId) OpenRound(IEnumerable<uint> participants);

        /// <summary>Client. Adopts a host-assigned stamp. Ignores one older than the current round.</summary>
        bool AdoptStamp(uint sessionId, uint roundId);

        /// <summary>Host. Records a report. Returns false when the stamp does not name the open round.</summary>
        bool TryMarkReady(uint connectionId, uint sessionId, uint roundId);

        /// <summary>Host. True when every remaining participant has reported.</summary>
        bool AreAllParticipantsReady();

        /// <summary>Host. Participants that have not reported yet.</summary>
        IReadOnlyCollection<uint> MissingParticipants();

        /// <summary>Host. True when this connection has reported for the open round.</summary>
        bool IsReady(uint connectionId);

        /// <summary>Host. Drops a departed peer from the round and re-checks completion.</summary>
        void RemoveParticipant(uint connectionId);

        /// <summary>Ends the open round without ending the run.</summary>
        void CloseRound();

        /// <summary>Drops session and round state at teardown.</summary>
        void ResetSession();
    }

    /// <summary>
    /// The lobby / level-transition readiness barrier: who is expected in this round, who has
    /// reported, and which round "this round" is.
    ///
    /// <para><b>Why this exists as a service rather than a flag on <c>Player</c>.</b> Readiness was
    /// a mutable <c>IsReady</c> bool on the replicated <c>Player</c> record, which is the root of
    /// two of the four lobby-ready defects: <c>ResetForNextLevel</c> clears it for every player
    /// including remote ones (defect B), and the host's full player record overwrites it wholesale
    /// (defect C — narrowed by the 60/5 Hz stream split in <c>a79ea0c</c>, which is recorded as
    /// having fixed it, but the 5 Hz record still overwrites). Anything that replicates a field
    /// can clobber it; the authoritative set has to live somewhere nothing replicates over.
    /// <c>Player.IsReady</c> stays on the wire as a display mirror, re-asserted from here, so a
    /// clobber self-heals instead of losing the answer.</para>
    ///
    /// <para><b>Derived from a shipping third-party implementation's spawn-readiness barrier,
    /// deliberately not a port of it.</b> That design is the right shape — host-assigned <c>(sessionId, roundId)</c>, an
    /// explicitly captured participant set, retry on both sides — and the parts of it that answer
    /// our defects are taken. Five things are done differently, each because the original has a
    /// sharp edge:</para>
    ///
    /// <list type="number">
    /// <item><b>One stamp, not two.</b> The reference tracks two parallel stamps — identical on
    /// the host, divergent on the client — which forces two near-identical predicates and
    /// three pending slots. One authoritative stamp plus one pending slot covers the same cases
    /// with far less to keep consistent.</item>
    ///
    /// <item><b>An empty participant set is an error, not a silent wait.</b> The reference's
    /// all-ready predicate returns false when the set is empty, so a round opened over
    /// nobody can never complete — a hang shape. Here it completes and logs, because "nobody to
    /// wait for" is trivially satisfied and the interesting event is that it happened at all.</item>
    ///
    /// <item><b>Retry is targeted, and the timeout names names.</b> The reference re-broadcasts the
    /// round start to every client once a second for up to sixty seconds and, on timeout, logs a
    /// bare boolean. <see cref="MissingParticipants"/> lets the host re-ask only the peers
    /// that owe a report, and lets the giving-up log say which connection ids never answered —
    /// the difference between an actionable line and a boolean.</item>
    ///
    /// <item><b>No all-players-ready message.</b> The reference needs one, plus a targeted re-send
    /// of it to answer a retrying client. Ours does not: the host already replicates readiness at 5 Hz
    /// and force-sends a full record the moment any <c>IsReady</c> changes
    /// (<c>UdpClientService.HasReadinessChanged</c>), so accepting a report <i>is</i> the
    /// acknowledgement and the client's retry stops when it observes itself ready. One fewer
    /// message type and one fewer piece of state that can disagree.</item>
    ///
    /// <item><b>Rounds are 1-based.</b> The reference agrees, and it matters: zero has to mean "no round"
    /// unambiguously. The encounter barrier next door uses zero as both a sentinel and a legal
    /// value, and that ambiguity is separately recorded as a defect in
    /// <c>docs/netplay/01-critical-fixes.md</c>. Not repeating it here.</item>
    /// </list>
    ///
    /// <para><b>UNVERIFIED.</b> Reasoned from the code paths and from the reference implementation.
    /// Not run in-game.</para>
    /// </summary>
    internal class ReadinessService : IReadinessService
    {
        private readonly HashSet<uint> participants = [];
        private readonly HashSet<uint> reported = [];

        private uint nextRoundId;

        public uint SessionId { get; private set; }

        public uint RoundId { get; private set; }

        public bool HasStamp => SessionId != 0 && RoundId != 0;

        public (uint SessionId, uint RoundId) OpenRound(IEnumerable<uint> participantIds)
        {
            EnsureSession();

            RoundId = ++nextRoundId;

            participants.Clear();
            reported.Clear();

            foreach (var id in participantIds)
            {
                participants.Add(id);
            }

            Plugin.Log.LogInfo(
                $"[readiness] Round {RoundId} open over {participants.Count} participant(s): " +
                $"{string.Join(", ", participants)}");

            return (SessionId, RoundId);
        }

        public bool AdoptStamp(uint sessionId, uint roundId)
        {
            if (sessionId == 0 || roundId == 0)
            {
                Plugin.Log.LogWarning($"[readiness] Ignoring an invalid stamp (session {sessionId}, round {roundId}).");
                return false;
            }

            // A round from the session we are already in, at or behind the one we hold, is a
            // duplicate or a reorder. Accepting it would re-open a round this peer has finished and
            // send it back to reporting for a level transition that is over.
            if (sessionId == SessionId && roundId <= RoundId)
            {
                return false;
            }

            SessionId = sessionId;
            RoundId = roundId;
            reported.Clear();

            return true;
        }

        public bool TryMarkReady(uint connectionId, uint sessionId, uint roundId)
        {
            if (!HasStamp || sessionId != SessionId || roundId != RoundId)
            {
                return false;
            }

            // Not in the captured set: either a peer that joined after the round opened, or a
            // report from one that has since left. Neither can be counted toward a round it was
            // never part of, but the round is not harmed by it either.
            if (!participants.Contains(connectionId))
            {
                Plugin.Log.LogWarning(
                    $"[readiness] Report from {connectionId}, which is not a participant of round {RoundId}. Ignored.");
                return false;
            }

            if (reported.Add(connectionId))
            {
                Plugin.Log.LogInfo($"[readiness] {connectionId} ready ({reported.Count}/{participants.Count}) for round {RoundId}.");
            }

            return true;
        }

        public bool AreAllParticipantsReady()
        {
            if (!HasStamp)
            {
                return false;
            }

            // Deliberately true, where the reference returns false. A round with no participants is
            // satisfied by definition; treating it as permanently unsatisfied converts a bookkeeping
            // mistake into a hang, which is the failure mode this whole barrier exists to remove.
            if (participants.Count == 0)
            {
                Plugin.Log.LogWarning($"[readiness] Round {RoundId} has no participants; treating it as satisfied.");
                return true;
            }

            return participants.All(reported.Contains);
        }

        public IReadOnlyCollection<uint> MissingParticipants()
        {
            return participants.Where(p => !reported.Contains(p)).ToList();
        }

        public bool IsReady(uint connectionId)
        {
            return reported.Contains(connectionId);
        }

        public void RemoveParticipant(uint connectionId)
        {
            var wasParticipant = participants.Remove(connectionId);
            reported.Remove(connectionId);

            if (wasParticipant)
            {
                Plugin.Log.LogInfo(
                    $"[readiness] {connectionId} left during round {RoundId} " +
                    $"({reported.Count}/{participants.Count} remaining participants ready).");
            }
        }

        public void CloseRound()
        {
            RoundId = 0;
            participants.Clear();
            reported.Clear();
        }

        public void ResetSession()
        {
            SessionId = 0;
            RoundId = 0;
            nextRoundId = 0;
            participants.Clear();
            reported.Clear();
        }

        /// <summary>
        /// Host only. Mints a non-zero session id once per run, so a report left in flight across a
        /// teardown cannot be accepted by the next run — whose round counter also starts at 1.
        /// </summary>
        private void EnsureSession()
        {
            if (SessionId != 0)
            {
                return;
            }

            // A Guid rather than a tick count: two runs started within the same millisecond would
            // collide on the latter, and the whole point of the field is to distinguish them.
            var bytes = Guid.NewGuid().ToByteArray();
            var candidate = BitConverter.ToUInt32(bytes, 0);

            SessionId = candidate == 0 ? 1u : candidate;
            nextRoundId = 0;
        }
    }
}

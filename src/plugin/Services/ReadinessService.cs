using System;
using System.Collections.Generic;
using System.Linq;

namespace OmniBonk.Services
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

        /// <summary>Host. Drops a departed peer from the round and re-checks completion.</summary>
        void RemoveParticipant(uint connectionId);

        /// <summary>Ends the open round without ending the run.</summary>
        void CloseRound();

        /// <summary>Drops session and round state at teardown.</summary>
        void ResetSession();
    }

    /// <summary>
    /// Level-transition readiness barrier, host-stamped <c>(sessionId, roundId)</c>.
    /// <para>Not a flag on <c>Player</c>: replicated fields get clobbered. <c>Player.IsReady</c> is only
    /// a display mirror re-asserted from here. Accepting a report is the acknowledgement (no
    /// all-ready message). Rounds are 1-based so zero means "no round".</para>
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

            // Duplicate or reordered stamp; would re-open a finished round.
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

            // Admit late joiners: dropping them would silently start without them. Caller has
            // already validated the id against the roster.
            if (participants.Add(connectionId))
            {
                Plugin.Log.LogInfo(
                    $"[readiness] {connectionId} joined round {RoundId} after it opened; admitted as a participant.");
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

            // Empty is satisfied; otherwise a bookkeeping mistake becomes a hang.
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

        /// <summary>Host only. Per-run id so an in-flight report cannot match the next run's round 1.</summary>
        private void EnsureSession()
        {
            if (SessionId != 0)
            {
                return;
            }

            var bytes = Guid.NewGuid().ToByteArray();
            var candidate = BitConverter.ToUInt32(bytes, 0);

            SessionId = candidate == 0 ? 1u : candidate;
            nextRoundId = 0;
        }
    }
}

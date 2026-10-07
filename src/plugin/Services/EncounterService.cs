using System;
using System.Collections.Concurrent;
using System.Linq;
using UnityEngine;

namespace MegabonkTogether.Services
{
    public interface IEncounterService
    {
        public bool IsClosable();

        public void AddClosedEncounterForPlayer(uint playerId);

        public void ClearClosedEncounters();

        public void Close();
        public void Unclose();

        /// <summary>Starts the failsafe clock for this round. Idempotent.</summary>
        public void BeginWaiting();

        /// <summary>True between <see cref="BeginWaiting"/> and the round ending.</summary>
        public bool IsWaiting { get; }

        public float WaitedSeconds { get; }

        /// <summary>True once a blocked round has outlived <see cref="WaitFailsafeSeconds"/>.</summary>
        public bool HasWaitedTooLong();

        public float WaitFailsafeSeconds { get; }

        #region Round identity (SE-5)

        /// <summary>The barrier session this peer is participating in. 0 until it knows one.</summary>
        uint SessionId { get; }

        /// <summary>The round this peer believes is currently open.</summary>
        uint RoundId { get; }

        /// <summary>
        /// Host only. Mints a new non-zero session id if there is none. Clients learn theirs from
        /// the first stamped release they see, so they never call this.
        /// </summary>
        void EnsureSession();

        /// <summary>
        /// Drops session and round state at teardown, so the next run cannot inherit a round
        /// counter and accept a message left in flight across the boundary.
        /// </summary>
        void ResetSession();

        /// <summary>
        /// True when <paramref name="sessionId"/> / <paramref name="roundId"/> name the round this
        /// peer currently has open. A zero <paramref name="sessionId"/> is accepted as "sender has
        /// not learned the session yet" — see the remarks on the implementation.
        /// </summary>
        bool IsCurrentStamp(uint sessionId, uint roundId);

        /// <summary>
        /// Records a release as applied and opens the next round. Returns false when this stamp has
        /// already been applied, which is the whole point: a repeated release for a finished round
        /// must not close whatever window is open now.
        /// </summary>
        bool TryApplyRelease(uint sessionId, uint roundId);

        #endregion
    }

    /// <summary>
    /// The shared-experience "everyone has finished choosing" barrier.
    ///
    /// <para>Each peer that reaches a reward window blocks until every player has reported; the
    /// host counts the reports and broadcasts the release. The full protocol, and the holes in it,
    /// are written up in `docs/netplay/07-shared-experience-audit.md` — in short, neither the
    /// report nor the release carries any round identity, so both can be attributed to the wrong
    /// round and a peer can end up waiting on one that will never complete.</para>
    ///
    /// <para>Closing that properly needs a wire change. Until then the failsafe clock here is what
    /// stops a hole from being permanent: a peer blocked for <see cref="WaitFailsafeSeconds"/>
    /// releases the round itself. Upstream issue #88 asks for exactly this.</para>
    /// </summary>
    internal class EncounterService(IPlayerManagerService playerManagerService) : IEncounterService
    {
        private readonly ConcurrentDictionary<uint, byte> closedEncounterPerPlayer = new();
        private bool forceClose = false;

        // Unscaled on purpose: shared experience pauses the game while the barrier is up, so a
        // scaled clock would not advance and the failsafe would never fire.
        private float waitingSince = -1f;

        public float WaitFailsafeSeconds => 20f;

        public void AddClosedEncounterForPlayer(uint playerId)
        {
            closedEncounterPerPlayer.TryAdd(playerId, 0);
        }

        public void ClearClosedEncounters()
        {
            closedEncounterPerPlayer.Clear();
            forceClose = false;
            waitingSince = -1f;
        }

        public bool IsClosable()
        {
            var allPlayerCount = playerManagerService.GetAllPlayers().Count();
            return closedEncounterPerPlayer.Count >= allPlayerCount || forceClose;
        }

        public void Close()
        {
            forceClose = true;
        }

        public void Unclose()
        {
            forceClose = false;
        }

        public void BeginWaiting()
        {
            if (waitingSince < 0f)
            {
                waitingSince = Time.unscaledTime;
            }
        }

        public bool IsWaiting => waitingSince >= 0f;

        public float WaitedSeconds => waitingSince < 0f ? 0f : Time.unscaledTime - waitingSince;

        public bool HasWaitedTooLong()
        {
            return IsWaiting && WaitedSeconds >= WaitFailsafeSeconds;
        }

        #region Round identity (SE-5)

        /// <remarks>
        /// <para><b>What this closes.</b> Neither <c>EncounterClosed</c> nor <c>CloseEncounter</c>
        /// carried any round identity, so neither a report nor a release could be addressed to the
        /// round it belonged to. Two observed consequences:</para>
        ///
        /// <list type="bullet">
        /// <item><b>SE-5 / OB-4</b> — a release generated for an already-finished round arrives at a
        /// peer that has since opened its next encounter window, and closes it instantly. The player
        /// loses that pick. <see cref="TryApplyRelease"/> makes a release idempotent per round.</item>
        /// <item><b>SE-5, report half</b> — a late report for a released round counts toward the
        /// round that is open now, releasing it before everyone has chosen.
        /// <see cref="IsCurrentStamp"/> lets the host drop it.</item>
        /// </list>
        ///
        /// <para><b>Why the round id is host-assigned but client-predicted.</b> Our barrier is
        /// peer-initiated — each peer reports when its own reward window finishes — so there is no
        /// host message opening a round that a client could quote back. Instead every peer derives
        /// the same number the same way: round <c>N</c> is open until a release for <c>N</c> is
        /// applied, after which <c>N+1</c> is open. Both sides start at 0, so they agree without a
        /// handshake, and any peer that misses a release stops matching the host's stamp and has its
        /// reports rejected rather than misattributed. Recovery is the existing failsafe.</para>
        ///
        /// <para><b>Why a session id as well.</b> The round counter alone is ambiguous across runs:
        /// two consecutive runs both start at round 0, so a message in flight across a teardown
        /// would be accepted by the next run. The host mints a non-zero session id per run and every
        /// stamp carries it. A client's <i>first</i> report necessarily carries 0, because a client
        /// only ever learns the value from a release and by definition has not seen one yet — so 0
        /// is accepted as "not yet learned" and every later value must match exactly.</para>
        ///
        /// <para><b>Deliberately not a monotonic timestamp.</b> A counter is comparable without
        /// clock agreement between peers, which nothing in this codebase establishes.</para>
        ///
        /// <para><b>UNVERIFIED.</b> Reasoned from the code paths, not run in-game.</para>
        /// </remarks>
        public uint SessionId { get; private set; }

        public uint RoundId { get; private set; }

        /// <summary>The last release applied, so a repeat is recognisable. -1 when none yet.</summary>
        private long lastAppliedRoundId = -1;

        /// <summary>
        /// Lazy rather than called from a run-start hook on purpose: the host reaches
        /// <c>RewardFinished</c> before any barrier message can exist, so minting there is
        /// unconditionally early enough and does not depend on which of the several run-start paths
        /// the session actually took.
        /// </summary>
        public void EnsureSession()
        {
            if (SessionId != 0)
            {
                return;
            }

            // Non-zero, because 0 is the wire's "sender has not learned a session id" value. The low
            // bits of the tick count suffice: this only has to differ from the previous run in the
            // same process, not be unguessable.
            var candidate = (uint)Environment.TickCount;
            SessionId = candidate == 0 ? 1u : candidate;
            RoundId = 0;
            lastAppliedRoundId = -1;
        }

        public void ResetSession()
        {
            SessionId = 0;
            RoundId = 0;
            lastAppliedRoundId = -1;
        }

        public bool IsCurrentStamp(uint sessionId, uint roundId)
        {
            if (sessionId != 0 && SessionId != 0 && sessionId != SessionId)
            {
                return false;
            }

            return roundId == RoundId;
        }

        public bool TryApplyRelease(uint sessionId, uint roundId)
        {
            if (sessionId != 0 && SessionId != 0 && sessionId != SessionId)
            {
                return false;
            }

            // A client adopts the host's session from the first release it sees; this is the only
            // place the value crosses.
            if (SessionId == 0 && sessionId != 0)
            {
                SessionId = sessionId;
            }

            if (lastAppliedRoundId >= 0 && roundId <= lastAppliedRoundId)
            {
                return false;
            }

            lastAppliedRoundId = roundId;

            // Open the round after the one just released. Assigned rather than incremented so a peer
            // that missed a release resynchronises to the host on the next one it does see.
            RoundId = roundId + 1;

            return true;
        }

        #endregion
    }
}

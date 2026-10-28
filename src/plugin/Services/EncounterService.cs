using System;
using System.Collections.Concurrent;
using System.Linq;
using UnityEngine;

namespace OmniBonk.Services
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

        /// <summary>
        /// OB-67: a reward window only this peer has (a Shady Guy a teammate already bought from). It is
        /// not part of any round: no pause, no report. Set before the window opens, cleared when it closes.
        /// </summary>
        bool IsSoloShop { get; }

        void BeginSoloShop();

        void EndSoloShop();

        #region Round identity (SE-5)

        /// <summary>0 until known.</summary>
        uint SessionId { get; }

        uint RoundId { get; }

        /// <summary>Host only. Clients learn the session from the first release.</summary>
        void EnsureSession();

        /// <summary>At teardown, so in-flight messages cannot match the next run.</summary>
        void ResetSession();

        /// <summary>A zero <paramref name="sessionId"/> means the sender has not learned it yet.</summary>
        bool IsCurrentStamp(uint sessionId, uint roundId);

        /// <summary>
        /// Returns false for an already-applied stamp, so a repeated release cannot close the
        /// window open now.
        /// </summary>
        bool TryApplyRelease(uint sessionId, uint roundId);

        #endregion
    }

    /// <summary>
    /// The shared-experience "everyone has finished choosing" barrier: the host counts reports and
    /// broadcasts the release. A peer blocked for <see cref="WaitFailsafeSeconds"/> releases itself.
    /// See docs/netplay/07-shared-experience-audit.md.
    /// </summary>
    internal class EncounterService(IPlayerManagerService playerManagerService) : IEncounterService
    {
        private readonly ConcurrentDictionary<uint, byte> closedEncounterPerPlayer = new();
        private bool forceClose = false;

        // Unscaled: the game is paused while the barrier is up.
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

        public bool IsSoloShop { get; private set; }

        public void BeginSoloShop() => IsSoloShop = true;

        public void EndSoloShop() => IsSoloShop = false;

        public bool HasWaitedTooLong()
        {
            return IsWaiting && WaitedSeconds >= WaitFailsafeSeconds;
        }

        #region Round identity (SE-5)

        /// <remarks>
        /// <para>SE-5 / OB-4: stamps stop a stale release closing the next window and a late report
        /// counting toward the wrong round.</para>
        /// <para>Every peer derives the round id the same way (N open until release N applies), so no
        /// handshake is needed. The session id disambiguates runs; a client's first report carries 0
        /// ("not yet learned").</para>
        /// <para><b>UNVERIFIED.</b> Reasoned from the code paths, not run in-game.</para>
        /// </remarks>
        public uint SessionId { get; private set; }

        public uint RoundId { get; private set; }

        /// <summary>-1 when none yet.</summary>
        private long lastAppliedRoundId = -1;

        /// <summary>Lazy: minting at RewardFinished is early enough on every run-start path.</summary>
        public void EnsureSession()
        {
            if (SessionId != 0)
            {
                return;
            }

            // Non-zero: 0 means "not learned" on the wire.
            var candidate = (uint)Environment.TickCount;
            SessionId = candidate == 0 ? 1u : candidate;
            RoundId = 0;
            lastAppliedRoundId = -1;
        }

        public void ResetSession()
        {
            IsSoloShop = false;
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

            // A client adopts the host's session here.
            if (SessionId == 0 && sessionId != 0)
            {
                SessionId = sessionId;
            }

            if (lastAppliedRoundId >= 0 && roundId <= lastAppliedRoundId)
            {
                return false;
            }

            lastAppliedRoundId = roundId;

            // Assigned, not incremented, so a peer that missed a release resyncs.
            RoundId = roundId + 1;

            return true;
        }

        #endregion
    }
}

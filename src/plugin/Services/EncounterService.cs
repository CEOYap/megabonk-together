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
    }
}

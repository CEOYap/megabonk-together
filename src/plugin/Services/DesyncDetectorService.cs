using Assets.Scripts.Managers;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Configuration;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Services
{
    public interface IDesyncDetectorService
    {
        /// <summary>Host: sends a digest every <c>DIGEST_INTERVAL_SECONDS</c>. Cheap when off.</summary>
        void Tick(float deltaTime);

        /// <summary>Client: diffs a received digest against local state and logs what drifted.</summary>
        void OnDigestReceived(StateDigest digest);

        void Reset();
    }

    /// <summary>
    /// The desync detector. The host periodically sends its view of the run; each client compares
    /// it with its own and logs the fields that disagree.
    ///
    /// <para><b>Why.</b> Two-player sessions have mostly been diagnosed by comparing screenshots.
    /// Every field in <see cref="StateDigest"/> is something that has drifted before — the
    /// interactable counters (OB-12), the barrier round (OB-21), the enemy and pickup registries
    /// (OB-9, OB-5). This turns the next drift into a log line.</para>
    ///
    /// <para><b>Reports, never corrects.</b> Nothing here writes back into the game. Correcting a
    /// mismatch would remove the evidence this exists to produce.</para>
    ///
    /// <para><b>Persistence filter.</b> State is in flight at any instant — an enemy spawned 50 ms
    /// ago, a barrier release not yet applied — so a single disagreement is often just latency. A
    /// field is reported only when it disagrees on two consecutive digests, 5 s apart, which is far
    /// longer than anything legitimately in flight. Counts additionally get a small tolerance.</para>
    ///
    /// <para><b>Silence is evidence.</b> A clean comparison logs one summary line per minute, so a
    /// log with no drift lines and a steady summary means "compared and agreed", not "never ran".</para>
    /// </summary>
    internal class DesyncDetectorService : IDesyncDetectorService
    {
        private const float DIGEST_INTERVAL_SECONDS = 5f;
        private const float IN_SYNC_SUMMARY_INTERVAL_SECONDS = 60f;

        // Counts move continuously while enemies spawn and die, so an exact comparison would
        // report every digest. Absolute floor for small numbers, proportional for large ones.
        private const int COUNT_TOLERANCE_ABSOLUTE = 3;
        private const float COUNT_TOLERANCE_RATIO = 0.10f;

        // INetTransport, NOT IUdpClientService. IUdpClientService is always the LiteNetLib session;
        // only INetTransport is switched to the Steam transport by UseSteamTransport (Plugin.cs).
        // Sending through IUdpClientService under Steam writes into a socket with no peers, and
        // nothing ever leaves the machine -- which is how this sent nothing for whole sessions.
        private readonly INetTransport transport;
        private readonly ISynchronizationService synchronizationService;
        private readonly IEnemyManagerService enemyManagerService;
        private readonly IPickupManagerService pickupManagerService;
        private readonly IEncounterService encounterService;

        private float accumulator = 0f;
        private uint sequence = 0;

        private HashSet<string> previousMismatches = new HashSet<string>();
        private HashSet<string> currentMismatches = new HashSet<string>();
        private readonly List<string> reportLines = new List<string>();
        private readonly Dictionary<string, int> localInteractables = new Dictionary<string, int>();

        private int digestsCompared = 0;
        private float lastInSyncSummary = -999f;
        private float lastSentReport = -999f;

        public DesyncDetectorService(
            INetTransport transport,
            ISynchronizationService synchronizationService,
            IEnemyManagerService enemyManagerService,
            IPickupManagerService pickupManagerService,
            IEncounterService encounterService)
        {
            this.transport = transport;
            this.synchronizationService = synchronizationService;
            this.enemyManagerService = enemyManagerService;
            this.pickupManagerService = pickupManagerService;
            this.encounterService = encounterService;
        }

        public void Tick(float deltaTime)
        {
            // Cheapest checks first: this runs every frame from NetworkHandler.
            if (!ModConfig.LogDesync.Value)
            {
                return;
            }

            accumulator += deltaTime;
            if (accumulator < DIGEST_INTERVAL_SECONDS)
            {
                return;
            }

            accumulator = 0f;

            if (!synchronizationService.HasNetplaySessionStarted()
                || !(synchronizationService.IsServerMode() ?? false))
            {
                return;
            }

            // The try is around the call, not inside BuildDigest. BuildDigest enumerates an
            // Il2CppSystem dictionary, and a member that does not resolve against this build raises
            // MissingMethodException when the containing method is JITted -- a try inside it could
            // not catch that. A broken diagnostic must never take the network tick down with it.
            StateDigest digest;
            try
            {
                digest = BuildDigest();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[desync] Could not build a digest: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            transport.SendToAllClients(digest, NetDelivery.ReliableOrdered);

            // The host side used to be silent on success, so on 2026-10-08 a detector that never
            // sent anything (it was wired to the wrong transport) and one that sent fine looked
            // identical in the host log. One line a minute closes that: no line means no digests.
            var now = Time.unscaledTime;
            if (now - lastSentReport >= IN_SYNC_SUMMARY_INTERVAL_SECONDS)
            {
                Plugin.Log.LogInfo($"[desync] host sent digest #{digest.Sequence} (sent every {DIGEST_INTERVAL_SECONDS:F0} s).");
                lastSentReport = now;
            }
        }

        private StateDigest BuildDigest()
        {
            var digest = new StateDigest
            {
                Sequence = ++sequence,
                TrackedEnemies = enemyManagerService.TrackedEnemyCount,
                TrackedPickups = pickupManagerService.TrackedPickupCount,
                BarrierSessionId = encounterService.SessionId,
                BarrierRoundId = encounterService.RoundId,
                PlayerLevel = ReadLocalLevel(),
            };

            ReadInteractables(digest.InteractablesUsed);
            return digest;
        }

        public void OnDigestReceived(StateDigest digest)
        {
            if (!ModConfig.LogDesync.Value || digest == null)
            {
                return;
            }

            try
            {
                Compare(digest);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[desync] Could not compare digest #{digest.Sequence}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Compare(StateDigest host)
        {
            currentMismatches.Clear();
            reportLines.Clear();

            var localEnemies = enemyManagerService.TrackedEnemyCount;
            var localPickups = pickupManagerService.TrackedPickupCount;
            var localLevel = ReadLocalLevel();

            CheckCount("enemies", host.TrackedEnemies, localEnemies);
            CheckCount("pickups", host.TrackedPickups, localPickups);
            CheckExact("level", host.PlayerLevel, localLevel);

            // A client learns the session id from its first release, so 0 means "not yet", not a
            // mismatch. The round is only comparable once both sides are in the same session.
            if (encounterService.SessionId != 0 && host.BarrierSessionId != 0)
            {
                CheckExact("barrier-session", (long)host.BarrierSessionId, encounterService.SessionId);
                CheckExact("barrier-round", (long)host.BarrierRoundId, encounterService.RoundId);
            }

            localInteractables.Clear();
            ReadInteractables(localInteractables);

            foreach (var row in host.InteractablesUsed)
            {
                localInteractables.TryGetValue(row.Key, out var local);
                CheckExact($"counter '{row.Key}'", row.Value, local);
            }

            foreach (var row in localInteractables)
            {
                if (!host.InteractablesUsed.ContainsKey(row.Key) && row.Value != 0)
                {
                    CheckExact($"counter '{row.Key}'", 0, row.Value);
                }
            }

            digestsCompared++;

            if (reportLines.Count > 0)
            {
                Plugin.Log.LogWarning(
                    $"[desync] digest #{host.Sequence}: {reportLines.Count} field(s) disagree with the host " +
                    $"on two consecutive digests -- {string.Join("; ", reportLines)}");
            }
            else
            {
                var now = Time.unscaledTime;
                if (now - lastInSyncSummary >= IN_SYNC_SUMMARY_INTERVAL_SECONDS)
                {
                    Plugin.Log.LogInfo(
                        $"[desync] {digestsCompared} digest(s) compared, in sync at #{host.Sequence} " +
                        $"(host/local: enemies {host.TrackedEnemies}/{localEnemies}, pickups " +
                        $"{host.TrackedPickups}/{localPickups}, round {host.BarrierRoundId}/{encounterService.RoundId}, " +
                        $"level {host.PlayerLevel}/{localLevel}).");
                    lastInSyncSummary = now;
                }
            }

            // Swap rather than copy: the set that was current becomes the comparison baseline.
            (previousMismatches, currentMismatches) = (currentMismatches, previousMismatches);
        }

        private void CheckCount(string field, int host, int local)
        {
            var tolerance = Math.Max(COUNT_TOLERANCE_ABSOLUTE, (int)(Math.Abs(host) * COUNT_TOLERANCE_RATIO));
            if (Math.Abs(host - local) > tolerance)
            {
                Flag(field, $"{field} host {host} / local {local}");
            }
        }

        private void CheckExact(string field, long host, long local)
        {
            if (host != local)
            {
                Flag(field, $"{field} host {host} / local {local}");
            }
        }

        private void Flag(string field, string line)
        {
            currentMismatches.Add(field);

            // Reported only if it also disagreed last time: one-off disagreements are latency.
            if (previousMismatches.Contains(field))
            {
                reportLines.Add(line);
            }
        }

        private static int ReadLocalLevel()
        {
            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.player == null || gameManager.player.inventory == null)
            {
                return -1;
            }

            var playerXp = gameManager.player.inventory.playerXp;
            return playerXp == null ? -1 : playerXp.level;
        }

        /// <summary>
        /// Copies numUsed for every row of the game's interactable counter into a managed
        /// dictionary. Copied rather than held, because a native collection must not be kept
        /// across frames.
        /// </summary>
        private static void ReadInteractables(Dictionary<string, int> into)
        {
            var byName = InteractablesStatus.interactablesByName;
            if (byName == null)
            {
                return;
            }

            foreach (var entry in byName)
            {
                if (entry.Key == null || entry.Value == null)
                {
                    continue;
                }

                into[entry.Key] = entry.Value.numUsed;
            }
        }

        public void Reset()
        {
            accumulator = 0f;
            sequence = 0;
            previousMismatches.Clear();
            currentMismatches.Clear();
            digestsCompared = 0;
            lastInSyncSummary = -999f;
            lastSentReport = -999f;
        }
    }
}

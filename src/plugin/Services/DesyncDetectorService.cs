using Assets.Scripts.Managers;
using OmniBonk.Common.Messages;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Configuration;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Services
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
    /// Desync detector: the host sends a digest of its state; clients log fields that disagree on
    /// two consecutive digests (one-off differences are latency). Reports only, never corrects.
    /// </summary>
    internal class DesyncDetectorService : IDesyncDetectorService
    {
        private const float DIGEST_INTERVAL_SECONDS = 5f;
        private const float IN_SYNC_SUMMARY_INTERVAL_SECONDS = 60f;

        // Counts move continuously; absolute floor for small numbers, proportional for large.
        private const int COUNT_TOLERANCE_ABSOLUTE = 3;
        private const float COUNT_TOLERANCE_RATIO = 0.10f;

        // INetTransport, NOT IUdpClientService: only INetTransport follows UseSteamTransport.
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
            // Runs every frame: cheapest checks first.
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

            // Try outside BuildDigest: an unresolved interop member throws MissingMethodException at JIT time.
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

            // One line a minute, so a missing line means no digests were sent.
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

            // Session id 0 means "not learned yet", not a mismatch.
            if (encounterService.SessionId != 0 && host.BarrierSessionId != 0)
            {
                CheckExact("barrier-session", (long)host.BarrierSessionId, encounterService.SessionId);
                CheckExact("barrier-round", (long)host.BarrierRoundId, encounterService.RoundId);
            }

            localInteractables.Clear();
            ReadInteractables(localInteractables);

            foreach (var row in host.InteractablesUsed)
            {
                if (IsPerPlayerCounter(row.Key))
                {
                    continue;
                }

                localInteractables.TryGetValue(row.Key, out var local);
                CheckExact($"counter '{row.Key}'", row.Value, local);
            }

            foreach (var row in localInteractables)
            {
                if (!IsPerPlayerCounter(row.Key) && !host.InteractablesUsed.ContainsKey(row.Key) && row.Value != 0)
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

                if (localEnemies > host.TrackedEnemies)
                {
                    ReportStalestEnemies(localEnemies - host.TrackedEnemies);
                }
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

            (previousMismatches, currentMismatches) = (currentMismatches, previousMismatches);
        }

        private float lastStaleReport = -999f;

        // OB-51: ids whose EnemyDied arrived here. Bounded by clearing.
        private static readonly System.Collections.Generic.HashSet<uint> diedReceived = new();

        internal static void ClearEnemyDiedReceived() => diedReceived.Clear();

        internal static void NoteEnemyDiedReceived(uint id)
        {
            if (diedReceived.Count > 20000)
            {
                diedReceived.Clear();
            }

            diedReceived.Add(id);
        }

        /// <summary>OB-51. Names the longest-silent local enemies when we hold more than the host. At most once a minute.</summary>
        private void ReportStalestEnemies(int extra)
        {
            var now = Time.unscaledTime;
            if (now - lastStaleReport < 60f)
            {
                return;
            }

            lastStaleReport = now;

            var rows = new System.Collections.Generic.List<(uint Id, string Name, float Hp, float Age, bool Dead)>();
            foreach (var kv in enemyManagerService.GetAllRegisteredEnemies())
            {
                var enemy = kv.Value;
                if (enemy == null)
                {
                    rows.Add((kv.Key, "<destroyed>", 0f, float.MaxValue, true));
                    continue;
                }

                var interpolator = enemy.GetComponent<Scripts.Snapshot.EnemyInterpolator>();
                var last = interpolator == null ? -1f : interpolator.LastSnapshotAt;
                var age = last < 0f ? float.MaxValue : now - last;
                rows.Add((kv.Key, enemy.enemyData != null ? enemy.enemyData.enemyName.ToString() : "?", enemy.hp, age, enemy.IsDead()));
            }

            rows.Sort((a, b) => b.Age.CompareTo(a.Age));
            var shown = System.Math.Min(rows.Count, System.Math.Max(extra, 5));
            var parts = new System.Collections.Generic.List<string>();
            for (var i = 0; i < shown; i++)
            {
                var r = rows[i];
                var age = r.Age == float.MaxValue ? "never" : $"{r.Age:F0}s";
                parts.Add($"{r.Id} {r.Name} hp {r.Hp:F0}{(r.Dead ? " DEAD" : "")} silent {age}{(diedReceived.Contains(r.Id) ? " died-msg-RECEIVED" : " no-died-msg")}");
            }

            Plugin.Log.LogWarning(
                $"[desync] OB-51: {extra} more enemies here than on the host; the longest-silent entries: " +
                string.Join("; ", parts) + ".");
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

        /// <summary>OB-29. Counters that are per player by design (Shady Guy counts per own shop close).</summary>
        private static bool IsPerPlayerCounter(string key) => key == "Shady Guy";

        /// <summary>Copies interactable numUsed rows; a native collection must not be held across frames.</summary>
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

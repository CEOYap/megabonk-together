using Assets.Scripts.Inventory.Stats;
using Assets.Scripts.Menu.Shop;
using OmniBonk.Common.Messages;
using OmniBonk.Common.Messages.GameNetworkMessages;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace OmniBonk.Services
{
    public interface IPlayerStatsSyncService
    {
        void SendIfChanged();

        void ApplyRemote(PlayerStatsChanged message);

        /// <summary>Null means unknown: leave the visual alone, do not assume zero.</summary>
        float? GetRemoteStat(uint ownerId, int stat);

        /// <summary>OB-26: mean Difficulty of all players, for the host's shared-world readers.</summary>
        float GetTeamDifficulty(float localDifficulty);

        void Reset();
    }

    /// <summary>
    /// OB-1: replicates player stat globals so peers draw remote attacks with the owner's numbers
    /// (<c>PlayerStats.GetStat</c> is static and reads the observer's). Polled, so no change path is missed.
    /// </summary>
    internal class PlayerStatsSyncService : IPlayerStatsSyncService
    {
        /// <summary>
        /// Tracked <c>EStat</c> ints. "Measured" ones are decompiled readers (VA <c>0x1804346F0</c>,
        /// <c>0x1804347A0</c>, <c>0x180435930</c>). Keyed dictionary, so adding one needs no protocol change.
        /// </summary>
        internal static readonly int[] TrackedStats =
        [
            (int)EStat.Projectiles,               // 16 — measured
            (int)EStat.SizeMultiplier,            // 9  — measured
            (int)EStat.ProjectileBounces,         // 45 — measured (OB-18)
            (int)EStat.DurationMultiplier,        // 10
            (int)EStat.ProjectileSpeedMultiplier, // 11
            (int)EStat.AttackSpeed,               // 15
            (int)EStat.Difficulty,                // 38 — OB-26, read by the host for the team average

            // OB-38: damage inputs (decompiled). TeammateCombatStats substitutes them while the host
            // builds a teammate's hit.
            (int)EStat.DamageMultiplier,          // 12
            (int)EStat.EffectDurationMultiplier,  // 14
            (int)EStat.CritChance,                // 18
            (int)EStat.CritDamage,                // 19
            (int)EStat.FireDamage,                // 20
            (int)EStat.IceDamage,                 // 21
            (int)EStat.LightningDamage,           // 22
            (int)EStat.EliteDamageMultiplier,     // 23
            (int)EStat.KnockbackMultiplier,       // 24
            (int)EStat.BurnChance,                // 42
            (int)EStat.FreezeChance,              // 43
            (int)EStat.PoisonDamageMultiplier,    // 56
            (int)EStat.Thorns,                    // 3  -- OB-43, WeaponUtility.GetWeaponDamage
            BaseDamageKey,
        ];

        /// <summary>OB-38: <c>MyPlayer.baseDamage</c>, a raw field not an EStat, under a key no EStat uses.</summary>
        internal const int BaseDamageKey = 1000;

        // INetTransport, NOT IUdpClientService, which sends nowhere under the Steam transport.
        private readonly INetTransport transport;
        private readonly IPlayerManagerService playerManagerService;

        private readonly Dictionary<int, float> lastSent = [];
        private readonly ConcurrentDictionary<uint, Dictionary<int, float>> remoteStats = [];

        // OB-26: cached once per frame; readers run per enemy.
        private int teamDifficultyFrame = -1;
        private float teamDifficulty;
        private float lastLoggedTeamDifficulty = float.NaN;
        private int lastLoggedCount;

        public PlayerStatsSyncService(
            INetTransport transport,
            IPlayerManagerService playerManagerService)
        {
            this.transport = transport;
            this.playerManagerService = playerManagerService;
        }

        public void SendIfChanged()
        {
            var local = playerManagerService.GetLocalPlayer();
            if (local == null)
            {
                return;
            }

            Dictionary<int, float> changed = null;

            foreach (var stat in TrackedStats)
            {
                float value;
                if (stat == BaseDamageKey)
                {
                    var me = Assets.Scripts.Actors.Player.MyPlayer.Instance;
                    if (me == null)
                    {
                        continue;
                    }

                    value = me.baseDamage;
                }
                else
                {
                    value = PlayerStats.GetStat((EStat)stat);
                }

                if (lastSent.TryGetValue(stat, out var previous) && previous == value)
                {
                    continue;
                }

                lastSent[stat] = value;
                changed ??= [];
                changed[stat] = value;
            }

            if (changed == null)
            {
                return;
            }

            // Whole block, not deltas, so a missed message cannot leave mixed values.
            IGameNetworkMessage message = new PlayerStatsChanged
            {
                OwnerId = local.ConnectionId,
                Stats = new Dictionary<int, float>(lastSent)
            };

            // ReliableOrdered: nothing supersedes it, and it carries a dictionary.
            if (playerManagerService.IsLocalConnectionId(local.ConnectionId)
                && (Plugin.Instance?.NetworkHandler?.IsHost ?? false))
            {
                transport.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                transport.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        public void ApplyRemote(PlayerStatsChanged message)
        {
            if (message?.Stats == null)
            {
                return;
            }

            remoteStats.TryGetValue(message.OwnerId, out var previous);
            remoteStats[message.OwnerId] = new Dictionary<int, float>(message.Stats);

            // Constant attacks that cache stats (Chunkers, Aegis) only refresh in OnStatUpdate, which
            // nothing calls on a remote avatar.
            var changed = new List<int>();
            foreach (var stat in message.Stats)
            {
                // Not an EStat; OnStatUpdate below must never see it.
                if (stat.Key == BaseDamageKey)
                {
                    continue;
                }

                if (previous == null
                    || !previous.TryGetValue(stat.Key, out var old)
                    || old != stat.Value)
                {
                    changed.Add(stat.Key);
                }
            }

            if (changed.Count == 0)
            {
                return;
            }

            var netPlayer = playerManagerService.GetNetPlayerByNetplayId(message.OwnerId);
            netPlayer?.OnRemoteStatsChanged(changed);
        }

        public float? GetRemoteStat(uint ownerId, int stat)
        {
            if (remoteStats.TryGetValue(ownerId, out var stats)
                && stats.TryGetValue(stat, out var value))
            {
                return value;
            }

            return null;
        }

        /// <summary>
        /// Mean (HP already scales with player count). Unknown teammates are skipped, dead ones kept.
        /// Uses the sampled local value: this runs inside <c>PlayerStatsNew.GetStat</c>.
        /// </summary>
        public float GetTeamDifficulty(float localDifficulty)
        {
            var frame = UnityEngine.Time.frameCount;
            if (frame == teamDifficultyFrame)
            {
                return teamDifficulty;
            }

            teamDifficultyFrame = frame;

            var local = lastSent.TryGetValue((int)EStat.Difficulty, out var sampled) ? sampled : localDifficulty;
            var sum = local;
            var count = 1;

            foreach (var player in playerManagerService.GetAllPlayersExceptLocal())
            {
                var remote = GetRemoteStat(player.ConnectionId, (int)EStat.Difficulty);
                if (remote.HasValue)
                {
                    sum += remote.Value;
                    count++;
                }
            }

            teamDifficulty = sum / count;
            LogTeamDifficultyIfChanged(local, count);
            return teamDifficulty;
        }

        // On change only; compare numbers before building strings (runs per frame).
        private void LogTeamDifficultyIfChanged(float local, int count)
        {
            if (teamDifficulty == lastLoggedTeamDifficulty && count == lastLoggedCount)
            {
                return;
            }

            lastLoggedTeamDifficulty = teamDifficulty;
            lastLoggedCount = count;

            var parts = $"host {local:0.00}";
            foreach (var player in playerManagerService.GetAllPlayersExceptLocal())
            {
                var remote = GetRemoteStat(player.ConnectionId, (int)EStat.Difficulty);
                parts += remote.HasValue
                    ? $", {player.ConnectionId} {remote.Value:0.00}"
                    : $", {player.ConnectionId} unknown";
            }

            Plugin.Log.LogInfo($"OB-26: team Difficulty {teamDifficulty:0.00} ({parts}) over {count} players");
        }

        public void Reset()
        {
            lastSent.Clear();
            remoteStats.Clear();
            teamDifficultyFrame = -1;
            lastLoggedTeamDifficulty = float.NaN;
            lastLoggedCount = 0;
        }
    }
}

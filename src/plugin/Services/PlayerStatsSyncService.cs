using Assets.Scripts.Inventory.Stats;
using Assets.Scripts.Menu.Shop;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MegabonkTogether.Services
{
    public interface IPlayerStatsSyncService
    {
        /// <summary>Samples the local player's tracked stats and broadcasts them if any changed.</summary>
        void SendIfChanged();

        /// <summary>Stores a peer's stat block.</summary>
        void ApplyRemote(PlayerStatsChanged message);

        /// <summary>
        /// A peer's value for one stat, or null when this peer has not been told it yet.
        /// Null means "unknown" and callers must leave the visual alone rather than assume zero.
        /// </summary>
        float? GetRemoteStat(uint ownerId, int stat);

        void Reset();
    }

    /// <summary>
    /// OB-1. Replicates the globals that decide how a player's attacks are drawn, so other peers can
    /// draw them with the owner's numbers.
    ///
    /// <para><b>Why this exists at all.</b> Every <c>WeaponUtility</c> stat accessor is
    /// <c>weaponStats[stat] + PlayerStats.GetStat(stat)</c>, and <c>PlayerStats.GetStat</c> is static
    /// — it reads whoever is asking. A remote player's attack was therefore built from their weapon
    /// and the observer's globals. Shield orbit count, aura size, projectile count and projectile
    /// size were all that one defect; the entry originally described only the countable one.</para>
    ///
    /// <para><b>Why a stat block rather than a per-visual message.</b> Replicating "this player's
    /// shield has N orbs" fixes the case somebody happened to notice and leaves aura size wrong.
    /// The stats are the shared cause, there are few of them, and they move only on level-up and
    /// item pickup.</para>
    ///
    /// <para><b>Why polled rather than event-driven.</b> The game raises stat changes through
    /// several paths — level-up, item added, weapon added, tome added — and hooking each is more
    /// surface than a comparison of five floats on a slow tick. Polling also catches a path nobody
    /// enumerated, which is the failure mode this bug already has a history of.</para>
    /// </summary>
    internal class PlayerStatsSyncService : IPlayerStatsSyncService
    {
        /// <summary>
        /// The stats a remote attack's appearance is built from, as <c>EStat</c> ints.
        ///
        /// <para><b>Projectiles and SizeMultiplier are measured</b> — they are what
        /// <c>GetAttackQuantity</c> and <c>GetAttackSizeMultiplier</c> read, decompiled at
        /// VA <c>0x1804346F0</c> and <c>0x1804347A0</c>. The rest are predicted from the same shape
        /// and cost nothing to carry; a stat that turns out not to matter is a few bytes a run.</para>
        /// </summary>
        internal static readonly int[] TrackedStats =
        [
            (int)EStat.Projectiles,               // 16 — measured
            (int)EStat.SizeMultiplier,            // 9  — measured
            (int)EStat.DurationMultiplier,        // 10
            (int)EStat.ProjectileSpeedMultiplier, // 11
            (int)EStat.AttackSpeed,               // 15
        ];

        private readonly IUdpClientService udpClientService;
        private readonly IPlayerManagerService playerManagerService;

        private readonly Dictionary<int, float> lastSent = [];
        private readonly ConcurrentDictionary<uint, Dictionary<int, float>> remoteStats = [];

        public PlayerStatsSyncService(
            IUdpClientService udpClientService,
            IPlayerManagerService playerManagerService)
        {
            this.udpClientService = udpClientService;
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
                var value = PlayerStats.GetStat((EStat)stat);

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

            // The whole block is sent, not only the deltas: a peer that missed an earlier message
            // would otherwise hold a mix of old and new values, and the block is five floats.
            IGameNetworkMessage message = new PlayerStatsChanged
            {
                OwnerId = local.ConnectionId,
                Stats = new Dictionary<int, float>(lastSent)
            };

            // Reliable because nothing supersedes it on any schedule: globals can sit unchanged for
            // the rest of a run, so a dropped update leaves every observer drawing this player's
            // attacks wrongly until they next level up. Ordered because two rapid level-ups must not
            // apply out of sequence. It also carries a dictionary, which rules out an unreliable
            // channel regardless — LiteNetLib fragments reliable channels only.
            if (playerManagerService.IsLocalConnectionId(local.ConnectionId)
                && (Plugin.Instance?.NetworkHandler?.IsHost ?? false))
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        public void ApplyRemote(PlayerStatsChanged message)
        {
            if (message?.Stats == null)
            {
                return;
            }

            remoteStats[message.OwnerId] = new Dictionary<int, float>(message.Stats);
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

        public void Reset()
        {
            lastSent.Clear();
            remoteStats.Clear();
        }
    }
}

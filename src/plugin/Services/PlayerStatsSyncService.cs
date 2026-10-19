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

        /// <summary>
        /// OB-26. The mean Difficulty of every connected player, for the host's shared-world readers.
        /// <paramref name="localDifficulty"/> is the local player's own value, used until the 2 Hz
        /// sample has taken one.
        /// </summary>
        float GetTeamDifficulty(float localDifficulty);

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
        /// VA <c>0x1804346F0</c> and <c>0x1804347A0</c>, joined by <c>ProjectileBounces</c> at
        /// <c>0x180435930</c> for OB-18. The rest are predicted from the same shape and cost nothing
        /// to carry; a stat that turns out not to matter is a few bytes a run.</para>
        ///
        /// <para><b>This list is meant to grow.</b> The message carries a keyed dictionary rather
        /// than named fields precisely so a stat can be added without a protocol change — adding one
        /// here is the whole edit.</para>
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

            // OB-38. What a hit's damage is built from (decompiled: WeaponUtility.GetDamage,
            // GetNewDamage, GetCritChance, GetCritDamageMultiplier, GetKnockback, GetDamageContainer,
            // Enemy.Damage, the burn and poison debuffs, the on-hit items). The host builds every
            // teammate's hits, and read all of these from itself. TeammateCombatStats substitutes
            // them while a teammate's hit is being built or applied.
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

        /// <summary>
        /// OB-38. <c>MyPlayer.baseDamage</c> (defaultBaseDamage + character level, decompiled
        /// <c>MyPlayer.CalculateBaseDamage</c>), which several on-hit items and the burn debuff read
        /// as a raw field. Not an EStat: carried under a key no EStat uses, which the dictionary was
        /// made for. A peer on an older build stores it and never reads it.
        /// </summary>
        internal const int BaseDamageKey = 1000;

        // INetTransport, NOT IUdpClientService. IUdpClientService is always the LiteNetLib session;
        // only INetTransport is switched to the Steam transport by UseSteamTransport (Plugin.cs).
        // Sending through IUdpClientService under Steam writes into a socket with no peers, and
        // nothing ever leaves the machine -- which is how this sent nothing for whole sessions.
        private readonly INetTransport transport;
        private readonly IPlayerManagerService playerManagerService;

        private readonly Dictionary<int, float> lastSent = [];
        private readonly ConcurrentDictionary<uint, Dictionary<int, float>> remoteStats = [];

        // OB-26. The readers that want the team average run per enemy, some per frame, so the mean
        // is computed once a frame rather than once a read.
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

            // Constant attacks that CACHE a stat (Chunkers caches its count and size; Aegis its
            // cooldown) only refresh in OnStatUpdate. On the local player the game calls that from
            // PlayerStatsNew.A_StatUpdate whenever a stat moves; on a remote avatar nothing did, so
            // those stayed at the values they were built with. The values they recompute come from
            // WeaponUtility, which the OB-1 postfixes already correct to this owner -- so telling
            // them a stat changed is all that was missing.
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
        /// <para><b>Mean, not sum or maximum</b> — the project's decision, see
        /// <c>docs/netplay/16-session-handover.md</c>. The mod already scales enemy HP for player
        /// count, so a sum would count players twice; a maximum imposes one player's curse on all.</para>
        ///
        /// <para><b>A teammate whose stat block has not arrived is left out</b> rather than counted
        /// as zero, so a late block does not dip the run for a moment. <b>Dead players are kept</b>:
        /// their build still defines the run, and dropping them would make a death soften the
        /// world.</para>
        ///
        /// <para>The local value is the 2 Hz sample rather than a fresh read: this is called from
        /// inside <c>PlayerStatsNew.GetStat</c>, and Difficulty moves only on pickups and level-ups.</para>
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

        // Logged on change only, which is also the first use: the line proves the patch reaches
        // the readers at all (handover trap 1), and Difficulty moves a handful of times a run.
        // Compared as numbers before any string is built: this runs every frame a reader does.
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

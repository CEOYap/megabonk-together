using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Extensions;
using MegabonkTogether.Helpers;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// The per-tick outbound streams: players, enemies, projectiles and tumbleweeds, plus the
    /// chunking that keeps each tick on an unreliable datagram.
    ///
    /// <para><b>Why this is not in the transport.</b> It reads game state, builds messages and
    /// paces them — none of which depends on what carries the bytes. It lived in
    /// <c>UdpClientService</c> and called that class's own send methods directly, which meant
    /// pointing <see cref="INetTransport"/> at a second transport would have sent gameplay one way
    /// and the streams another. Phase 4 of <c>docs/steamworks/00-migration-plan.md</c>.</para>
    ///
    /// <para><b>Tick rates are not set here.</b> <c>NetworkHandler</c> owns the accumulators and
    /// calls these methods; this decides only what each tick contains. Raising a rate is a
    /// performance change made there — see <c>docs/netplay/04-performance-and-gc.md</c>.</para>
    /// </summary>
    public interface IStateBroadcastService
    {
        /// <summary>
        /// One lobby tick. On the host this is the split player broadcast; on a client it is the
        /// local player's own update to the host.
        /// </summary>
        void Update();

        /// <summary>Host only. One enemy delta tick, with boss orbs riding on the first chunk.</summary>
        void UpdateEnemies();

        /// <summary>Host only.</summary>
        void UpdateProjectiles();

        /// <summary>Host only, and only on the Desert map.</summary>
        void UpdateTumbleWeeds();

        /// <summary>Stops every stream for the rest of the run.</summary>
        void GameOver();

        /// <summary>Clears per-session stream state. Separate from the transport's own reset.</summary>
        void Reset();
    }

    internal class StateBroadcastService(
        IPlayerManagerService playerManagerService,
        IEnemyManagerService enemyManagerService,
        IProjectileManagerService projectileManagerService,
        IFinalBossOrbManagerService finalBossOrbManagerService,
        ISpawnedObjectManagerService spawnedObjectManagerService) : IStateBroadcastService
    {
        /// <summary>
        /// The chunk budget: a tick serializing above this is split rather than promoted to a
        /// reliable channel.
        ///
        /// <para><b>There is a second copy of this number in <c>UdpClientService</c></b>, and they
        /// are deliberately not shared. That one is a promotion threshold on a single message that
        /// cannot be split; this one is a splitting budget. They agree today because both derive
        /// from the same MTU, and either could move without the other being wrong.</para>
        /// </summary>
        private const int MAX_PACKET_SIZE_BYTES = 1000;

        // Slack left in each chunk of a split stream tick for the message envelope that every chunk
        // repeats — the MemoryPack union tag plus the empty-collection headers for the fields that
        // stream does not populate. See SendStreamUpdate.
        private const int CHUNK_ENVELOPE_HEADROOM_BYTES = 100;

        // The full Player record goes out every Nth 60 Hz tick — 5 Hz — plus immediately on any
        // IsReady change. See SendPlayerStreams for how this number was chosen.
        private const int FULL_PLAYER_RECORD_EVERY_N_TICKS = 12;

        private int playerRecordTick;
        private readonly Dictionary<uint, bool> lastSentReadiness = [];
        private bool isGameOver = false;

        /// <summary>
        /// Resolved on first use rather than injected, for the same reason
        /// <see cref="NetMessageRouter"/> does it: the transport that provides
        /// <see cref="INetTransport"/> is constructed alongside this, and taking it as a
        /// constructor parameter is a cycle the container cannot break. Resolved once and cached;
        /// never per tick.
        /// </summary>
        private INetTransport transport;

        private INetTransport Transport =>
            transport ??= Plugin.Services.GetService<INetTransport>();

        public void GameOver()
        {
            isGameOver = true;
        }

        public void Reset()
        {
            isGameOver = false;

            // Player-stream pacing is per session: a stale readiness snapshot here would suppress
            // the forced full record that a genuine change is supposed to trigger, if a reused
            // connection id happened to carry the same flag.
            playerRecordTick = 0;
            lastSentReadiness.Clear();
        }

        public void Update()
        {
            UpdateLocalPlayer();

            if (isGameOver)
            {
                return;
            }

            if (Transport.IsHost() == true)
            {
                if (playerManagerService.IsGameOver())
                {
                    isGameOver = true;
                    IGameNetworkMessage wsMessage = new GameOver();
                    Transport.SendToAllClients(wsMessage, NetDelivery.ReliableOrdered);
                    EventManager.OnGameOver(wsMessage as GameOver);

                    return;
                }

                SendPlayerStreams();
            }
            else
            {
                IGameNetworkMessage playerUpdate = playerManagerService.GetLocalPlayerUpdate();
                if (playerUpdate == null)
                {
                    Plugin.Log.LogWarning("Local player update is null, cannot send to host");
                    return;
                }
                Transport.SendToHost(playerUpdate, NetDelivery.Unreliable);
            }
        }

        public void UpdateEnemies()
        {
            if (isGameOver || !EnsureIsHost())
            {
                return;
            }

            SendEnemiesUpdate();
        }

        public void UpdateProjectiles()
        {
            if (isGameOver || !EnsureIsHost())
            {
                return;
            }

            SendProjectilesUpdate();
        }

        public void UpdateTumbleWeeds()
        {
            if (isGameOver || !EnsureIsHost())
            {
                return;
            }

            SendTumbleWeedsUpdate();
        }

        private bool EnsureIsHost()
        {
            var isHost = Transport.IsHost();

            if (isHost == null)
            {
                Plugin.Log.LogWarning("IsHost not set yet");
                return false;
            }

            if (!isHost.Value)
            {
                Plugin.Log.LogWarning("Only host can perform this action");
                return false;
            }

            return true;
        }

        private void UpdateLocalPlayer()
        {
            var player = playerManagerService.GetLocalPlayer();
            var localPlayer = playerManagerService.GetLocalPlayerUpdate();

            if (player == null)
            {
                Plugin.Log.LogWarning("Local player is null");
                return;
            }

            if (localPlayer == null)
            {
                Plugin.Log.LogWarning("Local player update is null");
                return;
            }

            player.Position = Quantizer.Quantize(localPlayer.Position.ToUnityVector3());
            player.MovementState = localPlayer.MovementState;
            player.AnimatorState = localPlayer.AnimatorState;
            player.Hp = localPlayer.Hp;
            player.MaxHp = localPlayer.MaxHp;
            player.Shield = localPlayer.Shield;
            player.MaxShield = localPlayer.MaxShield;
            player.Inventory = localPlayer.Inventory;
            player.Name = localPlayer.Name;

            playerManagerService.UpdatePlayer(player);
        }

        /// <summary>
        /// Splits the host's 60 Hz player broadcast into the half that changes every frame and the
        /// half that does not.
        ///
        /// <para>Two captures measured the old single stream at a flat ~20 KB/s at two players —
        /// 65-90% of all host egress — re-sending names, skins, character ids and inventories sixty
        /// times a second. Measured against the real serializer, splitting it saves 62% at two
        /// players early and 79% at six players late (95.39 -> 19.70 KB/s). The six-player full
        /// record is 1628 B, which is over <see cref="MAX_PACKET_SIZE_BYTES"/>: before the split
        /// that promoted to ReliableOrdered every single tick, and after it only the low-rate record
        /// does.</para>
        ///
        /// <para><b>Readiness never waits for the heartbeat.</b> Any change to a player's
        /// <c>IsReady</c> sends a full record immediately. This matters: the lobby-ready barrier has
        /// four open defects where a single lost or late readiness is a permanent hang, and slowing
        /// that field down to save bandwidth would be the wrong trade. The check is one bool compare
        /// per player per tick.</para>
        ///
        /// <para><b>Why 5 Hz for the heartbeat.</b> With readiness handled separately, everything
        /// left in the full record is cosmetic on a sub-second scale: inventory display, health-bar
        /// maxima, and identity (name, skin, character), which is fixed after join. 5 Hz bounds the
        /// worst case at 200 ms — under the threshold where a joining player would notice a default
        /// name — while halving the heartbeat's cost against 10 Hz, which is worth 8 KB/s at six
        /// players. This is the knob to turn if any of that ever proves to want lower latency.</para>
        /// </summary>
        private void SendPlayerStreams()
        {
            SendPlayersStateUpdate();

            playerRecordTick++;

            if (playerRecordTick < FULL_PLAYER_RECORD_EVERY_N_TICKS && !HasReadinessChanged())
            {
                return;
            }

            playerRecordTick = 0;
            SendLobbyUpdate();
        }

        /// <summary>
        /// True when any player's <c>IsReady</c> differs from the last full record we sent, which
        /// forces one out immediately. Deliberately only <c>IsReady</c>: it is the sole field in the
        /// slow half where arriving 100 ms late has a correctness consequence rather than a cosmetic
        /// one.
        /// </summary>
        private bool HasReadinessChanged()
        {
            var changed = false;

            foreach (var player in playerManagerService.GetAllPlayers())
            {
                if (!lastSentReadiness.TryGetValue(player.ConnectionId, out var wasReady) || wasReady != player.IsReady)
                {
                    lastSentReadiness[player.ConnectionId] = player.IsReady;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// The continuous half, every tick. Chunked like the other per-entity streams so a six-player
        /// lobby cannot push it over the MTU and back onto a reliable channel.
        /// </summary>
        private void SendPlayersStateUpdate()
        {
            var states = new List<PlayerState>();

            foreach (var player in playerManagerService.GetAllPlayers())
            {
                states.Add(new PlayerState
                {
                    ConnectionId = player.ConnectionId,
                    Position = player.Position,
                    AnimatorState = player.AnimatorState,
                    MovementState = player.MovementState,
                    Hp = player.Hp,
                    Shield = player.Shield,
                });
            }

            SendStreamUpdate(
                states,
                (chunk, _) => new PlayersStateUpdate { States = chunk },
                nameof(PlayersStateUpdate));
        }

        private void SendLobbyUpdate()
        {
            // Labelled by hand, not by GetType().Name: this send and SendEnemiesUpdate both use the
            // LobbyUpdates type, so the type name merged the player stream and the enemy stream into
            // one bucket. That bucket has been reported as 65-90% of host traffic and named as where
            // bandwidth work belongs — a conclusion the counter could not actually support, because
            // the two scale with completely different things (player count vs. enemies alive). A
            // level-159 capture peaked at 199.50 KB/s under the merged name with no way to say which.
            //
            // Chunked despite being the smallest of the four streams: a Player carries a name string
            // and a full inventory snapshot, so at six players late in a run this can cross the cap
            // even though it rarely does at two.
            SendStreamUpdate(
                [.. playerManagerService.GetAllPlayers()],
                (chunk, _) => new LobbyUpdates { Players = chunk },
                "LobbyUpdates(players)");
        }

        private void SendEnemiesUpdate()
        {
            List<EnemyModel> enemies = [.. enemyManagerService.GetAllEnemiesDeltaAndUpdate()];
            List<BossOrbModel> bossFinalOrb = [.. finalBossOrbManagerService.GetAllOrbs()];

            if (enemies.Count == 0 && bossFinalOrb.Count == 0)
            {
                return;
            }

            // The other half of the LobbyUpdates bucket — see the note in SendLobbyUpdate. Enemies
            // and boss orbs stay together here deliberately: they are one delta stream sharing one
            // send, so splitting them further would report a size neither of them has on the wire.
            //
            // This is the stream the chunking is really aimed at. It is a delta, so a big tick means
            // "many enemies moved" — and a moving enemy is re-sent next tick regardless, which is
            // exactly the state that does not need a reliable channel. The old size promotion paid
            // acks and retransmits to redeliver positions that were already stale on arrival.
            //
            // The orbs ride on chunk 0 only: there are a handful of them, they are not what makes a
            // tick oversized, and repeating them per chunk would be duplicated payload. Chunk 0
            // exists whether or not the tick splits, so the single-message path is unchanged.
            if (enemies.Count == 0)
            {
                SendStreamUpdate(
                    bossFinalOrb,
                    (chunk, _) => new LobbyUpdates { BossOrbs = chunk },
                    "LobbyUpdates(enemies)");
                return;
            }

            SendStreamUpdate(
                enemies,
                (chunk, chunkIndex) => new LobbyUpdates
                {
                    Enemies = chunk,
                    BossOrbs = chunkIndex == 0 ? bossFinalOrb : [],
                },
                "LobbyUpdates(enemies)");
        }

        private void SendProjectilesUpdate()
        {
            // The worst offender by size in the level-159 capture: 4761 B/send at 20 Hz, i.e. four
            // fragments of a reliable-ordered message per tick during a swarm.
            SendStreamUpdate(
                [.. projectileManagerService.GetAllProjectilesDeltaAndUpdate()],
                (chunk, _) => new ProjectilesUpdate { Projectiles = chunk },
                nameof(ProjectilesUpdate));
        }

        private void SendTumbleWeedsUpdate()
        {
            SendStreamUpdate(
                [.. spawnedObjectManagerService.GetAllTumbleWeedsDeltaAndUpdate()],
                // TumbleWeeds is an array on the wire, so the chunk is copied. Leaving the field a
                // List would be a wire change; this stream is Desert-only at 20 Hz and rarely
                // splits, so the copy is the cheaper of the two.
                (chunk, _) => new TumbleWeedsUpdate { TumbleWeeds = [.. chunk] },
                nameof(TumbleWeedsUpdate));
        }

        /// <summary>
        /// Sends one tick of a per-entity stream, splitting an oversized tick into several sub-MTU
        /// <see cref="NetDelivery.Unreliable"/> datagrams instead of promoting the whole tick to
        /// <see cref="NetDelivery.ReliableOrdered"/>.
        ///
        /// <para><b>What this replaces.</b> Each of these senders used to do
        /// <c>if (serialized.Length >= MAX_PACKET_SIZE_BYTES) deliveryMethod = ReliableOrdered</c>.
        /// That is not wrong — LiteNetLib fragments reliable channels only, so an oversized
        /// unreliable send silently fails and promoting is the only way to get it there in one
        /// message. It is just the expensive answer to the wrong question. The tick does not need to
        /// arrive in one message; it needs to arrive.</para>
        ///
        /// <para><b>Why it matters at scale.</b> A level-159 capture put the merged enemy/player
        /// bucket at 2100 B/send at 97.3/s and <c>ProjectilesUpdate</c> at 4761 B/send at 20 Hz —
        /// so the hot streams were spending most of their time on a multi-fragment reliable channel,
        /// paying acks and retransmits, and head-of-line blocking every later entity update behind
        /// any one stalled fragment. Splitting by a byte budget keeps every datagram on the
        /// unreliable path, where a loss costs one entity one tick and the next tick corrects it.</para>
        ///
        /// <para><b>No wire change.</b> <c>OnLobbyUpdate</c> and the other receive handlers iterate
        /// per item and nothing depends on a list being complete, so N messages deserialize exactly
        /// as one did. Same types, same union tags, same handlers — a peer on the old build cannot
        /// tell the difference.</para>
        ///
        /// <para><b>The fallback still promotes.</b> The chunk count is derived from the whole
        /// tick's average bytes-per-item, so an unusually large single item (a player with a big
        /// inventory) can still overflow its chunk. That chunk promotes exactly as before, which
        /// makes the worst case no worse than today rather than a silent dropped send.</para>
        ///
        /// <para>Chunk 0 is passed to <paramref name="buildChunk"/> as index 0 whether or not the
        /// tick was split, so a caller can attach ride-along state (the enemy stream's boss orbs) to
        /// the first message only and have it behave identically in both paths.</para>
        ///
        /// <para><b>Reading the counters after this lands:</b> a split tick records once per chunk,
        /// so sends/s rises and B/send falls while KB/s stays put. That is the change working, not a
        /// regression.</para>
        /// </summary>
        private void SendStreamUpdate<TItem>(
            List<TItem> items,
            Func<List<TItem>, int, IGameNetworkMessage> buildChunk,
            string label)
        {
            if (items.Count == 0)
            {
                return;
            }

            // Serialized through IGameNetworkMessage explicitly, never the concrete type: the union
            // tag comes from the interface, and serializing as the concrete message would omit it
            // and put an undecodable payload on the wire.
            byte[] serialized = MemoryPackSerializer.Serialize<IGameNetworkMessage>(buildChunk(items, 0));

            // The common case, and the one that must stay cheap: the whole tick fits, so it goes out
            // exactly as it did before — one serialize, one send, no extra allocation.
            if (serialized.Length < MAX_PACKET_SIZE_BYTES)
            {
                Transport.SendToAllClients(serialized, NetDelivery.Unreliable, label);
                return;
            }

            // Budget below the cap rather than at it: every chunk repeats the message envelope (the
            // union tag and the empty-collection headers for the fields this stream does not set),
            // so sizing purely on the whole tick's bytes-per-item would land the last chunk just
            // over. Undershooting costs one extra datagram; overshooting costs a promotion.
            var bytesPerItem = Math.Max(1, serialized.Length / items.Count);
            var itemsPerChunk = Math.Max(1, (MAX_PACKET_SIZE_BYTES - CHUNK_ENVELOPE_HEADROOM_BYTES) / bytesPerItem);

            for (int start = 0, chunkIndex = 0; start < items.Count; start += itemsPerChunk, chunkIndex++)
            {
                var count = Math.Min(itemsPerChunk, items.Count - start);
                var chunk = items.GetRange(start, count);

                byte[] chunkBytes = MemoryPackSerializer.Serialize<IGameNetworkMessage>(buildChunk(chunk, chunkIndex));

                // Only reachable when the average underestimated this chunk, or when one item is
                // bigger than the whole budget. Reliable is the sole way such a message arrives.
                var deliveryMethod = chunkBytes.Length >= MAX_PACKET_SIZE_BYTES
                    ? NetDelivery.ReliableOrdered
                    : NetDelivery.Unreliable;

                Transport.SendToAllClients(chunkBytes, deliveryMethod, label);
            }
        }
    }
}

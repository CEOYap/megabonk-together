using OmniBonk.Common.Messages;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Common.Models;
using OmniBonk.Extensions;
using OmniBonk.Helpers;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace OmniBonk.Services
{
    /// <summary>
    /// The per-tick outbound streams, chunked to stay on unreliable datagrams. Transport-agnostic;
    /// tick rates live in <c>NetworkHandler</c>.
    /// </summary>
    public interface IStateBroadcastService
    {
        /// <summary>Host: player broadcast. Client: own update to the host.</summary>
        void Update();

        /// <summary>Host only. Boss orbs ride on the first chunk.</summary>
        void UpdateEnemies();

        /// <summary>Host only.</summary>
        void UpdateProjectiles();

        /// <summary>Host only, Desert map only.</summary>
        void UpdateTumbleWeeds();

        void GameOver();

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
        /// Chunk budget. Deliberately separate from <c>UdpClientService</c>'s copy, which is a
        /// promotion threshold for unsplittable messages.
        /// </summary>
        private const int MAX_PACKET_SIZE_BYTES = 1000;

        // Per-chunk slack for the repeated envelope (union tag, empty collection headers).
        private const int CHUNK_ENVELOPE_HEADROOM_BYTES = 100;

        // 5 Hz at 60 Hz ticks, plus immediately on any IsReady change.
        private const int FULL_PLAYER_RECORD_EVERY_N_TICKS = 12;

        private int playerRecordTick;
        private readonly Dictionary<uint, bool> lastSentReadiness = [];
        private bool isGameOver = false;

        /// <summary>Resolved lazily and cached: injecting it is a DI cycle.</summary>
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

            // A stale readiness snapshot could suppress a forced full record for a reused id.
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
        /// Continuous state every tick; the full record at 5 Hz (cosmetic fields only), saving
        /// 62-79% of host egress. Readiness changes force a full record at once: a late readiness
        /// can hang the lobby barrier.
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

        /// <summary>Only IsReady: the one slow field where lateness affects correctness.</summary>
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
            // Labelled by hand: enemies also use LobbyUpdates, and the counters must tell them apart.
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

            // Orbs ride on chunk 0 only, to avoid duplicating them per chunk.
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
            SendStreamUpdate(
                [.. projectileManagerService.GetAllProjectilesDeltaAndUpdate()],
                (chunk, _) => new ProjectilesUpdate { Projectiles = chunk },
                nameof(ProjectilesUpdate));
        }

        private void SendTumbleWeedsUpdate()
        {
            SendStreamUpdate(
                [.. spawnedObjectManagerService.GetAllTumbleWeedsDeltaAndUpdate()],
                // Array on the wire; changing the field type would break the wire format.
                (chunk, _) => new TumbleWeedsUpdate { TumbleWeeds = [.. chunk] },
                nameof(TumbleWeedsUpdate));
        }

        /// <summary>
        /// Sends one stream tick, splitting an oversized tick into sub-MTU Unreliable datagrams
        /// rather than promoting it to ReliableOrdered (LiteNetLib fragments reliable only). No wire
        /// change: receivers handle items individually. A chunk that still overflows promotes.
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

            // Serialize as IGameNetworkMessage: the concrete type omits the union tag.
            byte[] serialized = MemoryPackSerializer.Serialize<IGameNetworkMessage>(buildChunk(items, 0));

            if (serialized.Length < MAX_PACKET_SIZE_BYTES)
            {
                Transport.SendToAllClients(serialized, NetDelivery.Unreliable, label);
                return;
            }

            // Budget below the cap: every chunk repeats the envelope.
            var bytesPerItem = Math.Max(1, serialized.Length / items.Count);
            var itemsPerChunk = Math.Max(1, (MAX_PACKET_SIZE_BYTES - CHUNK_ENVELOPE_HEADROOM_BYTES) / bytesPerItem);

            for (int start = 0, chunkIndex = 0; start < items.Count; start += itemsPerChunk, chunkIndex++)
            {
                var count = Math.Min(itemsPerChunk, items.Count - start);
                var chunk = items.GetRange(start, count);

                byte[] chunkBytes = MemoryPackSerializer.Serialize<IGameNetworkMessage>(buildChunk(chunk, chunkIndex));

                // Oversized chunk: only reliable fragments.
                var deliveryMethod = chunkBytes.Length >= MAX_PACKET_SIZE_BYTES
                    ? NetDelivery.ReliableOrdered
                    : NetDelivery.Unreliable;

                Transport.SendToAllClients(chunkBytes, deliveryMethod, label);
            }
        }
    }
}

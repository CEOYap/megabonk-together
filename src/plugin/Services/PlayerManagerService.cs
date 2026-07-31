using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive.Implementations;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using BepInEx.Logging;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Extensions;
using MegabonkTogether.Helpers;
using MegabonkTogether.Scripts.NetPlayer;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MegabonkTogether.Services
{
    public interface IPlayerManagerService
    {
        public PlayerUpdate? GetLocalPlayerUpdate();
        public IEnumerable<Player> GetAllNonHostPlayers();
        public IEnumerable<Player> GetAllPlayers();
        public IEnumerable<Player> GetAllPlayersAlive();
        public int GetAlivePlayerCount();
        public IReadOnlyList<Player> GetAllPlayersAliveNonAlloc();
        public IEnumerable<Player> GetAllPlayersExceptLocal();
        public IEnumerable<NetPlayer> GetAllSpawnedNetPlayers();
        public bool AddPlayer(uint connectionId, bool isHost, bool isSelf, string name = "Player");
        public Player GetPlayer(uint connectionId);
        public void UpdatePlayer(Player player);

        public Player? GetHost();
        public Player? GetLocalPlayer();

        public void SpawnPlayers();

        public NetPlayer GetRandomNetPlayer();
        public void AddProjectileToSpawn(uint connectionId);
        public void AddGetNetplayerPosition(uint connectionId);
        public void AddGetNetplayerPositionRequest(uint connectionId);
        public bool TryGetProjectileToSpawn(out uint connectionId);
        public bool TryGetGetNetplayerPosition(out uint connectionId);
        public uint? PeakNetplayerPosition();
        public uint? PeakNetplayerPositionRequest();

        public void UnqueueNetplayerPositionRequest();
        public NetPlayer GetNetPlayerByNetplayId(uint connectionId);
        public NetPlayer GetNetPlayerByWeapon(WeaponBase weapon);

        public void ResetForNextLevel();
        public void AddPlayerInventory(uint connectionId, PlayerInventory inventory);
        public PlayerInventory GetPlayerInventory(uint connectionId);

        public bool IsGameOver();
        public uint? GetRandomPlayerAliveConnectionId();
        public IEnumerable<(uint, Rigidbody)> GetConnectionIdsAndRigidBodies();
        public void Disconnect(uint connectionId);
        public void Reset();

        public void OnSelectedCharacterSet();
        public bool HasSelectedCharacter();
        public bool IsLocalConnectionId(uint ownerId);
        public bool IsRemoteConnectionId(uint value);
        void SetSeed(int seed);
        public int GetSeed();
        public bool IsRemotePlayerHealth(PlayerHealth instance);
        public bool IsRemoteTomeInventory(TomeInventory instance);
        public bool IsANetPlayerAbility(PassiveAbilityBullseye instance);
        public bool IsRemoteItem(ItemGhost instance);
        public void RemovePlayer(uint clientConnectionId);
    }

    public class PlayerManagerService : IPlayerManagerService
    {
        private readonly ConcurrentDictionary<uint, Player> players = new();

        /// <summary>
        /// FIX P1-4: reused by <see cref="GetAllPlayersAliveNonAlloc"/>. Sized for the 6-player cap.
        /// Main-thread only — see that method's remarks.
        /// </summary>
        private readonly List<Player> alivePlayersBuffer = new(8);
        private readonly ManualLogSource logger;
        private ConcurrentDictionary<uint, NetPlayer> spawnedPlayers = [];
        private uint localConnectionId = 0;
        private bool isLocalPlayerSet = false;
        private bool hasSelectedCharacter = false;
        private int seed = 0;
        private ConcurrentQueue<uint> projectileToSpawnQueue = new();
        private ConcurrentQueue<uint> getNetplayerPositionQueue = new();
        private ConcurrentQueue<uint> getNetplayerPositionRequestQueue = new();
        private ConcurrentDictionary<uint, PlayerInventory> playerInventories = new();

        public PlayerManagerService(ManualLogSource logger)
        {
            this.logger = logger;
        }

        public bool IsLocalConnectionId(uint ownerId)
        {
            return ownerId == localConnectionId;
        }

        public bool IsRemoteConnectionId(uint value)
        {
            return value != localConnectionId;
        }

        public bool IsRemotePlayerHealth(PlayerHealth instance)
        {
            var player = GameManager.Instance.player;
            if (player == null || player.inventory == null)
            {
                logger.LogWarning("Local player or inventory is null when checking remote PlayerHealth.");
                return false;
            }
            return instance != player.inventory.playerHealth;
        }

        public bool IsRemoteTomeInventory(TomeInventory instance)
        {
            var player = GameManager.Instance.player;
            if (player == null || player.inventory == null)
            {
                logger.LogWarning("Local player or inventory is null when checking remote TomeInventory.");
                return false;
            }
            return instance != player.inventory.tomeInventory;
        }

        public IEnumerable<Player> GetAllNonHostPlayers()
        {
            return players.Where(p => !p.Value.IsHost).Select(p => p.Value).ToList();
        }

        public IEnumerable<Player> GetAllPlayers()
        {
            return [.. players.Values];
        }

        public IEnumerable<Player> GetAllPlayersAlive()
        {
            return [.. players.Where(p => p.Value.Hp > 0).Select(p => p.Value)];
        }

        /// <summary>
        /// FIX P1-4: allocation-free alive count. <see cref="GetAllPlayersAlive"/> materialises a
        /// Player[] plus two LINQ iterators on every call, and most callers only wanted
        /// <c>.Count()</c> of it.
        /// </summary>
        public int GetAlivePlayerCount()
        {
            var count = 0;
            foreach (var kv in players)
            {
                if (kv.Value.Hp > 0)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// FIX P1-4: non-allocating alive list for hot callers.
        ///
        /// <para><b>The returned list is a shared buffer that the next call overwrites.</b> Read it
        /// and finish before calling anything that might call this again; copy it if you need to
        /// retain it past that point.</para>
        ///
        /// <para><b>Main thread only.</b> There is one buffer and no lock. Every current caller is
        /// a Unity <c>Update</c>. Do not call this from a network receive handler — use
        /// <see cref="GetAllPlayersAlive"/> there, which allocates but is safe.</para>
        /// </summary>
        public IReadOnlyList<Player> GetAllPlayersAliveNonAlloc()
        {
            alivePlayersBuffer.Clear();
            foreach (var kv in players)
            {
                if (kv.Value.Hp > 0)
                {
                    alivePlayersBuffer.Add(kv.Value);
                }
            }

            return alivePlayersBuffer;
        }

        public uint? GetRandomPlayerAliveConnectionId()
        {
            var alivePlayers = players.Where(p => p.Value.Hp > 0).ToList();

            if (alivePlayers.Count == 0)
            {
                logger.LogWarning("No alive players available to select.");
                return null;
            }
            var randomIndex = UnityEngine.Random.Range(0, alivePlayers.Count);
            var randomPlayer = alivePlayers.ElementAt(randomIndex).Value;
            return randomPlayer.ConnectionId;
        }

        public IEnumerable<(uint, Rigidbody)> GetConnectionIdsAndRigidBodies()
        {
            var result = new List<(uint, Rigidbody)>();
            foreach (var kvp in spawnedPlayers)
            {
                var netPlayer = kvp.Value;
                if (netPlayer != null)
                {
                    result.Add((netPlayer.ConnectionId, netPlayer.Rigidbody));
                }
            }

            var localPlayer = GetLocalPlayer();
            if (localPlayer != null)
            {
                var playerGameObject = GameManager.Instance.player.gameObject;
                var rigidbody = playerGameObject.GetComponent<Rigidbody>();
                if (rigidbody != null)
                {
                    result.Add((localPlayer.ConnectionId, rigidbody));
                }
            }

            return result;
        }

        public void OnSelectedCharacterSet()
        {
            hasSelectedCharacter = true;
        }
        public bool HasSelectedCharacter()
        {
            return hasSelectedCharacter;
        }
        public bool AddPlayer(uint connectionId, bool isHost, bool isSelf, string name = "Player")
        {
            var player = new Player
            {
                ConnectionId = connectionId,
                IsHost = isHost,
                Name = isSelf ? Configuration.ModConfig.PlayerName.Value : name
            };

            if (!players.TryAdd(connectionId, player))
            {
                return false;
            }

            logger.LogInfo($"Player added. Total players: {players.Count}");

            if (isSelf)
            {
                localConnectionId = connectionId;
                isLocalPlayerSet = true;
            }

            return true;
        }

        public void RemovePlayer(uint connectionId)
        {
            if (!players.Remove(connectionId, out var removed))
            {
                logger.LogWarning("Attempted to remove a player that does not exist.");
                return;
            }

            if (GameManager.Instance?.player == null)
            {
                return;
            }

            var netPlayer = GetNetPlayerByNetplayId(connectionId);

            if (netPlayer == null)
            {
                logger.LogWarning($"No NetPlayer found for ConnectionId: {connectionId} during disconnect.");
                return;
            }

            if (spawnedPlayers.TryRemove(connectionId, out var toDelete))
            {
                GameObject.Destroy(toDelete.gameObject);
            }

            CleanQueue(projectileToSpawnQueue, connectionId);
            CleanQueue(getNetplayerPositionQueue, connectionId);
            CleanQueue(getNetplayerPositionRequestQueue, connectionId);
        }

        private static void CleanQueue(ConcurrentQueue<uint> queue, uint connectionId)
        {
            var newQueue = queue.Where(id => id != connectionId).ToList();
            queue.Clear();
            foreach (var id in newQueue)
            {
                queue.Enqueue(id);
            }
        }

        public Player GetPlayer(uint connectionId)
        {
            if (players.TryGetValue(connectionId, out var player))
            {
                return player;
            }

            // This warning was unthrottled and fired 668 times in one 3-player session, all for the
            // same id, immediately after that peer disconnected — i.e. this sits on a per-frame path
            // and something keeps asking for a player who is gone. Each hit was a string concat plus
            // BepInEx disk I/O, which the bepinex skill calls out explicitly.
            //
            // Throttled per connection id so a genuinely new missing player is still reported at
            // once, while a stuck caller reports every 5s with a count instead of every frame. The
            // repetition is the signal, not each line.
            ReportMissingPlayer(connectionId);
            return null;
        }

        private const double MISSING_PLAYER_REPORT_INTERVAL_SECONDS = 5d;
        private readonly ConcurrentDictionary<uint, (DateTime LastReport, int Suppressed)> missingPlayerReports = new();

        private void ReportMissingPlayer(uint connectionId)
        {
            var now = DateTime.UtcNow;

            missingPlayerReports.TryGetValue(connectionId, out var previous);

            if (previous.LastReport != default
                && (now - previous.LastReport).TotalSeconds < MISSING_PLAYER_REPORT_INTERVAL_SECONDS)
            {
                missingPlayerReports[connectionId] = (previous.LastReport, previous.Suppressed + 1);
                return;
            }

            missingPlayerReports[connectionId] = (now, 0);

            // Carry the suppressed count into the line. The rate is the diagnosis — "+667 more"
            // says a per-frame caller is stuck on a departed player, which one line per 5s alone
            // would not convey.
            logger.LogWarning(previous.Suppressed > 0
                ? $"Player not found for ConnectionId: {connectionId} (+{previous.Suppressed} more in the last ~5s)"
                : $"Player not found for ConnectionId: {connectionId}");
        }

        public void UpdatePlayer(Player player)
        {
            if (!players.Remove(player.ConnectionId, out _))
            {
                logger.LogWarning("Attempted to update a player that does not exist.");
                return;
            }

            players.TryAdd(player.ConnectionId, player);
        }

        public Player GetHost()
        {
            return players.FirstOrDefault(p => p.Value.IsHost).Value;
        }

        public void SpawnPlayers()
        {
            if (spawnedPlayers.Count > 0)
            {
                logger.LogWarning("Players have already been spawned.");
                return;
            }

            var others = GetAllPlayersExceptLocal();

            foreach (var other in others)
            {
                var go = new GameObject($"NetPlayer-{other.ConnectionId}");
                var player = go.AddComponent<NetPlayer>();
                spawnedPlayers.TryAdd(other.ConnectionId, player);
                player.Initialize((ECharacter)other.Character, other.ConnectionId, other.Skin);
            }
        }

        public IEnumerable<NetPlayer> GetAllSpawnedNetPlayers()
        {
            return [.. spawnedPlayers.Values];
        }

        public NetPlayer GetRandomNetPlayer()
        {
            if (spawnedPlayers.Count == 0)
            {
                logger.LogWarning("No spawned players available to select.");
                return null;
            }
            var randomIndex = UnityEngine.Random.Range(0, spawnedPlayers.Count);
            return spawnedPlayers.ElementAt(randomIndex).Value;

        }

        public void AddProjectileToSpawn(uint connectionId)
        {
            projectileToSpawnQueue.Enqueue(connectionId);
        }

        public bool TryGetProjectileToSpawn(out uint connectionId)
        {
            return projectileToSpawnQueue.TryDequeue(out connectionId);
        }

        public NetPlayer GetNetPlayerByNetplayId(uint connectionId)
        {
            if (spawnedPlayers.TryGetValue(connectionId, out var netPlayer))
            {
                return netPlayer;
            }

            return null;
        }

        public void AddGetNetplayerPosition(uint connectionId)
        {
            getNetplayerPositionQueue.Enqueue(connectionId);
        }

        public uint? PeakNetplayerPosition()
        {
            if (getNetplayerPositionQueue.TryPeek(out var connectionId))
            {
                return connectionId;
            }

            return null;
        }

        public bool TryGetGetNetplayerPosition(out uint connectionId)
        {
            return getNetplayerPositionQueue.TryDequeue(out connectionId);
        }

        public PlayerUpdate? GetLocalPlayerUpdate()
        {
            var player = GameManager.Instance.player;
            var camera = GameManager.Instance.playerCamera;
            if (player == null || camera == null || player.inventory == null)
            {
                return null;
            }

            var position = new Vector3(
                player.transform.position.x,
                player.transform.position.y - Plugin.PLAYER_FEET_OFFSET_Y,
                player.transform.position.z
            );

            if (player.character == ECharacter.TonyMcZoom)
            {
                HoverAnimations hoverAnimations = player.GetComponent<HoverAnimations>();
                if (hoverAnimations != null)
                {
                    position = hoverAnimations.defaultPos;
                }
            }

            var animatorState = player.GetAnimatorState();
            var axisInput = new UnityEngine.Vector2(MyInputManager.GetPlayer().GetAxis("Move Horizontal"), MyInputManager.GetPlayer().GetAxis("Move Vertical"));

            return new PlayerUpdate
            {
                Position = position.ToNumericsVector3(),
                MovementState = new()
                {
                    AxisInput = Quantizer.Quantize(axisInput),
                    CameraForward = Quantizer.Quantize(camera.transform.forward),
                    CameraRight = Quantizer.Quantize(camera.transform.right)
                },
                AnimatorState = animatorState,
                ConnectionId = localConnectionId,
                Hp = (uint)Math.Max(0, player.inventory.playerHealth.hp),
                MaxHp = (uint)Math.Max(0, player.inventory.playerHealth.maxHp),
                //Xp = (uint)Math.Max(0, player.inventory.playerXp.xp),
                Shield = (uint)Math.Max(0, player.inventory.playerHealth.shield),
                MaxShield = (uint)Math.Max(0, player.inventory.playerHealth.maxShield),
                Inventory = player.inventory.ToInventoryInfos(),
                Name = Configuration.ModConfig.PlayerName.Value
            };
        }

        public Player? GetLocalPlayer()
        {
            if (!isLocalPlayerSet)
            {
                logger.LogWarning("Local connection ID is not set.");
                return null;
            }

            return GetPlayer(localConnectionId);
        }

        public IEnumerable<Player> GetAllPlayersExceptLocal()
        {
            return players
                .Where(p => p.Key != localConnectionId)
                .Select(p => p.Value)
                .ToList();
        }

        public NetPlayer GetNetPlayerByWeapon(WeaponBase weapon)
        {
            return spawnedPlayers.Values
                .FirstOrDefault(np => np.Inventory.weaponInventory.weapons.ContainsValue(weapon));
        }

        public void UnqueueNetplayerPositionRequest()
        {
            if (!getNetplayerPositionRequestQueue.TryDequeue(out _))
            {
                //logger.LogWarning("No netplayer position requests to unqueue.");
            }
        }

        public void AddGetNetplayerPositionRequest(uint connectionId)
        {
            getNetplayerPositionRequestQueue.Enqueue(connectionId);
        }


        public uint? PeakNetplayerPositionRequest()
        {
            if (getNetplayerPositionRequestQueue.TryPeek(out var connectionId))
            {
                return connectionId;
            }

            return null;
        }

        public void ResetForNextLevel()
        {
            players.Values.ToList().ForEach(p =>
            {
                p.IsReady = false;
            });
            spawnedPlayers.ToList().ForEach(p => p.Value.gameObject.SetActive(false));
            spawnedPlayers.Clear();
            projectileToSpawnQueue.Clear();
            getNetplayerPositionQueue.Clear();
            getNetplayerPositionRequestQueue.Clear();

        }

        //TODO: cleanup inventories at some point
        public void AddPlayerInventory(uint connectionId, PlayerInventory inventory)
        {
            if (!playerInventories.TryAdd(connectionId, inventory))
            {
                logger.LogWarning($"Failed to add PlayerInventory for ConnectionId: {connectionId}");
            }
        }

        public PlayerInventory GetPlayerInventory(uint connectionId)
        {
            if (!playerInventories.TryGetValue(connectionId, out var inventory))
            {
                logger.LogWarning($"PlayerInventory not found for ConnectionId: {connectionId}");
                return null;
            }

            return inventory;
        }

        public bool IsGameOver()
        {
            return players.Values.All(p => p.Hp == 0);
        }

        public void Disconnect(uint connectionId)
        {
            RemovePlayer(connectionId);

            var inventory = GetPlayerInventory(connectionId);
            if (inventory != null)
            {
                playerInventories.TryRemove(connectionId, out _);
            }

            Plugin.Instance.NetPlayersDisplayer.RemovePlayer(connectionId);
        }

        public void Reset()
        {
            players.Clear();

            // FIX P1-7: guard per item. The try used to wrap the whole ForEach, and List<T>.ForEach
            // abandons the iteration on the first throw — so one already-destroyed NetPlayer meant
            // every remaining one was never destroyed, and the Clear() below then dropped the only
            // references to them. They survived into the next session, untracked and undestroyed.
            foreach (var kv in spawnedPlayers.ToList())
            {
                try
                {
                    var netPlayer = kv.Value;
                    if (netPlayer == null) // Unity's overloaded == also catches a destroyed object
                    {
                        continue;
                    }

                    var go = netPlayer.gameObject;
                    if (go != null)
                    {
                        GameObject.Destroy(go);
                    }
                }
                catch (Exception ex)
                {
                    // The null check above is not sufficient on its own: a native object freed
                    // underneath the managed wrapper still throws on .gameObject through interop.
                    logger.LogWarning($"Could not destroy spawned player {kv.Key} during reset: {ex.Message}");
                }
            }

            spawnedPlayers.Clear();
            projectileToSpawnQueue.Clear();
            getNetplayerPositionQueue.Clear();
            getNetplayerPositionRequestQueue.Clear();
            // FIX P1-7: same shape as the loop above, and this one had no try at all — a throw
            // from Cleanup() escaped Reset() entirely, skipping the Clear() and every field reset
            // below it and leaving the service half-reset for the next session.
            foreach (var kv in playerInventories.ToList())
            {
                try
                {
                    kv.Value?.Cleanup();
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Could not clean up inventory for player {kv.Key} during reset: {ex.Message}");
                }
            }

            playerInventories.Clear();
            missingPlayerReports.Clear();
            localConnectionId = 0;
            isLocalPlayerSet = false;
            hasSelectedCharacter = false;
            seed = 0;
        }

        public void SetSeed(int seed)
        {
            this.seed = seed;
        }

        public int GetSeed()
        {
            return seed;
        }

        public bool IsANetPlayerAbility(PassiveAbilityBullseye instance)
        {
            return GetAllSpawnedNetPlayers().Any(netPlayer => netPlayer.Inventory.passiveAbility == instance);
        }

        public bool IsRemoteItem(ItemGhost instance)
        {
            return GetAllSpawnedNetPlayers().Any(netPlayer => netPlayer.Inventory.itemInventory.items.ContainsValue(instance));
        }
    }
}

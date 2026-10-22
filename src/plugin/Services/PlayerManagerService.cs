using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts._Data.Hats;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive.Implementations;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using BepInEx.Logging;
using OmniBonk.Common.Messages;
using OmniBonk.Common.Models;
using OmniBonk.Extensions;
using OmniBonk.Helpers;
using Microsoft.Extensions.DependencyInjection;
using OmniBonk.Scripts.NetPlayer;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OmniBonk.Services
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

        /// <summary>Gives a known peer an avatar if it lacks one. Idempotent, main thread only.</summary>
        public NetPlayer EnsureNetPlayerSpawned(uint connectionId);

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

        /// <summary>Teammates' inventories; unlike avatars, these survive a level change.</summary>
        public ICollection<PlayerInventory> GetRemoteInventories();

        /// <summary>
        /// The teammate holding <paramref name="weapon"/>, or null for the local player's or nobody's.
        /// Works during a level change.
        /// </summary>
        public uint? GetWeaponOwnerId(WeaponBase weapon);

        /// <summary>Like GetPlayerInventory but without the warning. For hit paths.</summary>
        public PlayerInventory FindPlayerInventory(uint connectionId);

        /// <summary>Matched by native pointer.</summary>
        public uint? GetItemInventoryOwnerId(System.IntPtr itemInventory);

        /// <summary>Matched by native pointer.</summary>
        public uint? GetPassiveAbilityOwnerId(System.IntPtr passiveAbility);

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
        public void ClearLobbyPlayers();
    }

    public class PlayerManagerService : IPlayerManagerService
    {
        private readonly ConcurrentDictionary<uint, Player> players = new();

        /// <summary>FIX P1-4: buffer for <see cref="GetAllPlayersAliveNonAlloc"/>. Main thread only.</summary>
        private readonly List<Player> alivePlayersBuffer = new(8);
        private readonly ManualLogSource logger;
        private ConcurrentDictionary<uint, NetPlayer> spawnedPlayers = [];
        private uint localConnectionId = 0;
        private bool isLocalPlayerSet = false;

        private bool warnedLocalPlayerUnset = false;
        private bool hasSelectedCharacter = false;
        private int seed = 0;
        private ConcurrentQueue<uint> projectileToSpawnQueue = new();
        private ConcurrentQueue<uint> getNetplayerPositionQueue = new();
        // FIX P1-11: frame-stamped so an unpopped request can be dropped.
        private ConcurrentQueue<(uint ConnectionId, int Frame)> getNetplayerPositionRequestQueue = new();
        private ConcurrentDictionary<uint, PlayerInventory> playerInventories = new();

        private const float STALE_REQUEST_REPORT_INTERVAL_SECONDS = 5f;
        private int stalePositionRequests = 0;
        private float lastStalePositionRequestReport = -999f;

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
            if (player == null || player.inventory == null || player.inventory.tomeInventory == null || instance == null)
            {
                logger.LogWarning("Local player or inventory is null when checking remote TomeInventory.");
                return false;
            }
            // By native pointer: one native object may have several managed wrappers.
            return instance.Pointer != player.inventory.tomeInventory.Pointer;
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

        /// <summary>FIX P1-4: allocation-free alive count.</summary>
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
        /// FIX P1-4: returns a shared buffer the next call overwrites. Main thread only (no lock);
        /// use <see cref="GetAllPlayersAlive"/> from network handlers.
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

        /// <summary>
        /// Drops players after a failed match attempt. Not <see cref="Reset"/>: this runs on the
        /// WebSocket receive thread, so it must do no Unity work. Keeps seed and character choice.
        /// </summary>
        public void ClearLobbyPlayers()
        {
            var cleared = players.Count;

            players.Clear();
            localConnectionId = 0;
            isLocalPlayerSet = false;

            if (cleared > 0)
            {
                logger.LogInfo($"Cleared {cleared} lobby player(s) left over from a failed match attempt.");
            }
        }

        public void RemovePlayer(uint connectionId)
        {
            if (!players.Remove(connectionId, out var removed))
            {
                logger.LogWarning("Attempted to remove a player that does not exist.");
                return;
            }

            // Before destroying the NetPlayer (and before the early returns): enemies targeting a
            // destroyed Rigidbody read a dangling transform every frame.
            try
            {
                var moved = Scripts.Enemies.TargetSwitcherManager.RetargetAllTargeting(connectionId);
                if (moved > 0)
                {
                    logger.LogInfo($"Moved {moved} enem{(moved == 1 ? "y" : "ies")} off departed player {connectionId}.");
                }
            }
            catch (Exception ex)
            {
                // P1-7: never abort the rest of the cleanup.
                logger.LogWarning($"Could not retarget enemies away from {connectionId}: {ex.Message}");
            }

            // In-flight pickups also follow the departed player's Transform.
            try
            {
                // Resolved here, not injected, to avoid a DI cycle.
                var pickupManagerService = Plugin.Services.GetRequiredService<IPickupManagerService>();

                // May be null; the sweep then matches destroyed targets instead.
                var departingTransform = GetNetPlayerByNetplayId(connectionId)?.Model?.transform;
                var stopped = pickupManagerService.StopFollowingDepartedPlayer(connectionId, departingTransform);
                if (stopped > 0)
                {
                    logger.LogInfo($"Stopped {stopped} pickup(s) following departed player {connectionId}.");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Could not stop pickups following {connectionId}: {ex.Message}");
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
            CleanPositionRequestQueue(connectionId);
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

        private void CleanPositionRequestQueue(uint connectionId)
        {
            var kept = getNetplayerPositionRequestQueue.Where(r => r.ConnectionId != connectionId).ToList();
            getNetplayerPositionRequestQueue.Clear();
            foreach (var request in kept)
            {
                getNetplayerPositionRequestQueue.Enqueue(request);
            }
        }

        public Player GetPlayer(uint connectionId)
        {
            if (players.TryGetValue(connectionId, out var player))
            {
                return player;
            }

            // Throttled per id: per-frame callers keep asking for departed players.
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
            foreach (var other in GetAllPlayersExceptLocal())
            {
                EnsureNetPlayerSpawned(other.ConnectionId);
            }
        }

        /// <summary>
        /// Gives a known peer an avatar if it lacks one. Idempotent and on demand, so a delayed
        /// start barrier cannot leave a peer invisible for the run. Main thread only.
        /// </summary>
        public NetPlayer EnsureNetPlayerSpawned(uint connectionId)
        {
            if (spawnedPlayers.TryGetValue(connectionId, out var existing))
            {
                // Unity ==: a destroyed object compares equal to null.
                if (existing != null)
                {
                    if (players.TryGetValue(connectionId, out var known))
                    {
                        var wanted = (ECharacter)known.Character;

                        // Rebuild if the model failed to build during a level load, or was built
                        // before the roster knew the character. The roster is the truth.
                        if (existing.Model == null)
                        {
                            logger.LogInfo($"Re-initializing NetPlayer {connectionId}: it has no model.");
                            existing.Initialize(wanted, connectionId, known.Skin);
                            ApplyHat(existing, known);
                        }
                        else if (existing.BuiltAs != wanted || existing.BuiltWithSkin != known.Skin)
                        {
                            logger.LogInfo(
                                $"Rebuilding NetPlayer {connectionId}: built as {existing.BuiltAs}/"
                                + $"'{existing.BuiltWithSkin}', the roster now says {wanted}/'{known.Skin}'.");
                            existing.Destroy();
                            existing.Initialize(wanted, connectionId, known.Skin);
                            ApplyHat(existing, known);
                        }
                        else if (existing.BuiltWithHat != known.Hat)
                        {
                            ApplyHat(existing, known);
                        }
                    }

                    return existing;
                }

                // Destroyed by a scene load: remove the entry, or TryAdd below fails forever.
                logger.LogInfo($"Replacing NetPlayer {connectionId}: the previous one was destroyed.");
                spawnedPlayers.TryRemove(connectionId, out _);
            }

            var local = GetLocalPlayer();
            if (local != null && local.ConnectionId == connectionId)
            {
                // The local player has no avatar.
                return null;
            }

            if (!players.TryGetValue(connectionId, out var player))
            {
                ReportMissingPlayer(connectionId);
                return null;
            }

            var go = new GameObject($"NetPlayer-{connectionId}");
            var netPlayer = go.AddComponent<NetPlayer>();

            if (!spawnedPlayers.TryAdd(connectionId, netPlayer))
            {
                // Lost a race; keep the winner, destroy ours.
                UnityEngine.Object.Destroy(go);
                logger.LogInfo($"Discarded a duplicate NetPlayer for {connectionId}; another call won.");
                return spawnedPlayers.TryGetValue(connectionId, out var winner) && winner != null ? winner : null;
            }

            netPlayer.Initialize((ECharacter)player.Character, connectionId, player.Skin);
            ApplyHat(netPlayer, player);
            logger.LogInfo($"Spawned NetPlayer for {connectionId}.");

            return netPlayer;
        }

        /// <summary>
        /// Applies the roster's hat. Read from the record because <c>HatChanged</c> never fires
        /// for avatars built later.
        /// </summary>
        private void ApplyHat(NetPlayer netPlayer, Player player)
        {
            if (netPlayer == null || netPlayer.Model == null)
            {
                return;
            }

            netPlayer.BuiltWithHat = player.Hat;

            if (player.Hat == 0)
            {
                return;
            }

            try
            {
                var hatData = DataManager.Instance.GetHat((EHat)player.Hat);
                if (hatData != null)
                {
                    using (Plugin.SuppressOutbound())
                    {
                        netPlayer.SetHat(hatData);
                    }
                }
            }
            catch (System.Exception ex)
            {
                // Cosmetic; must not take the avatar down.
                logger.LogWarning($"Could not put hat {player.Hat} on {player.ConnectionId}: {ex.Message}");
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
                // Once per transition: the lobby panel polls this.
                if (!warnedLocalPlayerUnset)
                {
                    warnedLocalPlayerUnset = true;
                    logger.LogWarning("Local connection ID is not set.");
                }

                return null;
            }

            warnedLocalPlayerUnset = false;

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
            // Inventory arrives after spawn, and a throw in a hit prefix aborts the hit.
            return spawnedPlayers.Values
                .FirstOrDefault(np => np.Inventory?.weaponInventory?.weapons?.ContainsValue(weapon) ?? false);
        }

        public PlayerInventory FindPlayerInventory(uint connectionId)
        {
            return playerInventories.TryGetValue(connectionId, out var inventory) ? inventory : null;
        }

        public uint? GetWeaponOwnerId(WeaponBase weapon)
        {
            if (weapon == null)
            {
                return null;
            }

            foreach (var kv in playerInventories)
            {
                var weapons = kv.Value?.weaponInventory?.weapons;
                if (weapons != null && weapons.ContainsValue(weapon))
                {
                    return kv.Key;
                }
            }

            return null;
        }

        public uint? GetItemInventoryOwnerId(System.IntPtr itemInventory)
        {
            foreach (var kv in playerInventories)
            {
                var items = kv.Value?.itemInventory;
                if (items != null && items.Pointer == itemInventory)
                {
                    return kv.Key;
                }
            }

            return null;
        }

        public uint? GetPassiveAbilityOwnerId(System.IntPtr passiveAbility)
        {
            foreach (var kv in playerInventories)
            {
                var passive = kv.Value?.passiveAbility;
                if (passive != null && passive.Pointer == passiveAbility)
                {
                    return kv.Key;
                }
            }

            return null;
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
            // FIX P1-11: see PeakNetplayerPositionRequest.
            getNetplayerPositionRequestQueue.Enqueue((connectionId, Time.frameCount));
        }


        /// <summary>
        /// The front entry redirects the local player's transforms to a netplayer for one game call.
        /// <para>FIX P1-11: drops entries from earlier frames. Prefix/postfix pairs cannot use
        /// try/finally, so a skipped pop would otherwise redirect the local player for the rest of
        /// the session.</para>
        /// </summary>
        public uint? PeakNetplayerPositionRequest()
        {
            // PERF: hottest path in the mod (every transform access). Check IsEmpty before the
            // native Time.frameCount call.
            if (getNetplayerPositionRequestQueue.IsEmpty)
            {
                return null;
            }

            var currentFrame = Time.frameCount;

            while (getNetplayerPositionRequestQueue.TryPeek(out var request))
            {
                if (request.Frame == currentFrame)
                {
                    return request.ConnectionId;
                }

                if (!getNetplayerPositionRequestQueue.TryDequeue(out var stale))
                {
                    break;
                }

                ReportStalePositionRequest(stale.ConnectionId, currentFrame - stale.Frame);
            }

            return null;
        }

        /// <summary>Reports at most once per 5s.</summary>
        private void ReportStalePositionRequest(uint connectionId, int ageInFrames)
        {
            stalePositionRequests++;

            var now = Time.unscaledTime;
            if (now - lastStalePositionRequestReport < STALE_REQUEST_REPORT_INTERVAL_SECONDS)
            {
                return;
            }
            lastStalePositionRequestReport = now;

            logger.LogWarning(
                $"Dropped {stalePositionRequests} stale netplayer position request(s) in the last ~5s " +
                $"(most recent: ConnectionId {connectionId}, {ageInFrames} frames old). A push was not " +
                "matched by its pop - without this the local player's transforms would keep resolving " +
                "to that netplayer.");

            stalePositionRequests = 0;
        }

        private void ResetStalePositionRequestDiagnostics()
        {
            stalePositionRequests = 0;
            lastStalePositionRequestReport = -999f;
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
            ResetStalePositionRequestDiagnostics();
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

        public ICollection<PlayerInventory> GetRemoteInventories()
        {
            return playerInventories.Values;
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

            // FIX P1-7: guard per item, so one throw cannot leak the rest.
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
                    // A freed native object can still throw on .gameObject.
                    logger.LogWarning($"Could not destroy spawned player {kv.Key} during reset: {ex.Message}");
                }
            }

            spawnedPlayers.Clear();
            projectileToSpawnQueue.Clear();
            getNetplayerPositionQueue.Clear();
            getNetplayerPositionRequestQueue.Clear();
            ResetStalePositionRequestDiagnostics();
            // FIX P1-7: same per-item guard.
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

        /// <summary>
        /// OB-2: whether a remote player owns this ghost (every instance hears the static
        /// interaction event). Null-hardened: called from a Harmony prefix, and the
        /// <c>== null</c> is Unity's, to catch destroyed avatars.
        /// </summary>
        public bool IsRemoteItem(ItemGhost instance)
        {
            if (instance == null)
            {
                return false;
            }

            foreach (var netPlayer in GetAllSpawnedNetPlayers())
            {
                if (netPlayer == null)
                {
                    continue;
                }

                var inventory = netPlayer.Inventory;
                if (inventory == null || inventory.itemInventory == null)
                {
                    continue;
                }

                var items = inventory.itemInventory.items;
                if (items != null && items.ContainsValue(instance))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

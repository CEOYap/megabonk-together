using OmniBonk.Configuration;
using Actors.Enemies;
using Assets.Scripts._Data.Hats;
using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Actors;
using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Camera;
using Assets.Scripts.Game.Combat;
using Assets.Scripts.Game.Combat.EnemySpecialAttacks;
using Assets.Scripts.Game.Other;
using Assets.Scripts.Game.Spawning.New.Timelines;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Chests;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Attacks;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using Assets.Scripts.Managers;
using Assets.Scripts.Menu.Shop;
using Assets.Scripts.Utility;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using OmniBonk.Common.Messages;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Common.Models;
using OmniBonk.Extensions;
using OmniBonk.Helpers;
using OmniBonk.Patches;
using OmniBonk.Scripts.Interactables;
using OmniBonk.Scripts.Snapshot;
using MemoryPack;
using MonoMod.Utils;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace OmniBonk.Services
{
    public enum GameEvent
    {
        Loading,
        Ready,
        Start,
        PortalOpened,
        FinalPortalOpened,
        GameOver,
    }

    public enum State
    {
        None,
        Loading,
        Ready,
        Started,
        LoadingNextLevel,
        Endgame,
        GameOver,
    }

    public interface ISynchronizationService
    {
        public bool IsLobbyReady();
        public void TransitionToState(GameEvent gameEvent);
        public bool? IsServerMode();
        public void OnSpawnedObject(GameObject obj);
        public void StartGame();

        public bool HasNetplaySessionStarted();
        public bool HasNetplaySessionInitialized();

        /// <summary>OB-9. Host only. Replicates an enemy's HP after a post-spawn rescale.</summary>
        public void OnEnemyHpRescaled(Enemy enemy);

        public void Reset();

        public bool IsLoading();

        public void OnSpawnedEnemy(Enemy enemy, EEnemy enemyName, Vector3 position, int waveNumber, bool forceSpawn, EEnemyFlag flag, bool canBeElite, float extraSizeMultiplier);
        public void OnSelectedCharacter();
        public void OnEnemyDied(Enemy instance, DamageContainer dc = null, uint? ownerId = null);
        public void OnSpawnedProjectile(Il2CppObjectBase instance, uint? owner = null);
        public void OnProjectileDone(ProjectileBase instance);
        public void OnPickupOrbSpawned(EPickup ePickup, Vector3 pos);
        public void OnPickupApplied(Pickup instance);
        public void OnSpawnedChest(Vector3 position, Quaternion rotation, UnityEngine.Object obj);
        public void OnChestOpened(OpenChest instance);
        public void OnWeaponAdded(WeaponInventory instance, WeaponData weaponData, Il2CppSystem.Collections.Generic.List<StatModifier> upgradeOffer);
        public void OnInteractableUsed(BaseInteractable instance);
        public bool OnStartingToChargingShrine(uint shrineNetplayId);
        public bool OnStoppingChargingShrine(uint shrineNetplayId);
        public void OnPickupSpawned(Pickup result, EPickup ePickup, Vector3 pos, int value);
        public void OnEnemyExploder(Enemy enemy);
        public void OnEnemyDamaged(Enemy instance, DamageContainer damageContainer);

        /// <summary>Host: sends the enemy hits summed since the last call. Once per enemy tick.</summary>
        public void FlushEnemyDamage();
        public void OnSpawnedEnemySpecialAttack(Enemy enemy, EnemySpecialAttack attack);

        public void PrepareForNextLevel();
        public bool IsLoadingNextLevel();
        public bool OnStartingToChargingPylon(uint pylonNetplayId);
        public bool OnStoppingChargingPylon(uint pylonNetplayId);
        public void OnFinalBossOrbsSpawned(Orb orb);
        public void OnFinalBossOrbDestroyed(uint removed);
        public void OnSwarmEvent(TimelineEvent currentEvent);
        public void OnPlayerDied();
        public void OnRunStarted(RunConfig newRunConfig);
        public void OnTomeAdded(TomeInventory instance, TomeData tomeData, Il2CppSystem.Collections.Generic.List<StatModifier> upgradeOffer, ERarity rarity);
        public void OnLightningStrike(Enemy enemy, int bounces, DamageContainer dc, float bounceRange, float bounceProcCoefficient);
        public void OnTornadoesSpawned(int amount);
        public void OnStormStarted(DesertStorm instance);
        public void OnStormStopped();
        public void OnTumbleWeedSpawned(InteractableTumbleWeed instance);
        public void OnTumbleWeedDespawned(InteractableTumbleWeed instance);
        public void OnInteractableFightEnemySpawned(InteractableCharacterFight instance);
        public void OnWantToStartFollowingPickup(Pickup instance);
        public void SendPickupFollowingPlayer(uint pickupId, uint playerId);
        public void OnItemAdded(EItem item);
        public void OnItemRemoved(EItem item);
        public void OnWeaponToggled(WeaponInventory instance, EWeapon eWeapon, bool enable);
        public void OnSpawnedObjectInCrypt(GameObject obj);
        public bool OnStartingToChargingLamp(uint value);
        public bool OnStoppingChargingLamp(uint value);
        public void OnTimerStarted();
        public void OnHatChanged(EHat eHat);
        public void OnSkinSelected(SkinData skin);
        public void OnRespawn(uint ownerId, Vector3 position);
        public bool IsSharedExperienceEnabled();
        public void PlayerXpAddXp(int xp, int amount, float leftOverXp);
        public void RewardFinished();

        /// <summary>Breaks a stuck shared-experience barrier. See the audit doc.</summary>
        public void ForceCloseEncounter(string reason);

        /// <summary>OB-6. Host: broadcast that this shrine completed.</summary>
        public void OnChargeShrineCompleted(ChargeShrine shrine);

        /// <summary>OB-6. Client: our bar filled first; ask the host to complete this shrine.</summary>
        public void RequestChargeShrineCompletion(ChargeShrine shrine);
        public void OnChangeGold(int amount);
    }
    internal class SynchronizationService : ISynchronizationService
    {
        // The transport seam, not the LiteNetLib class: a Steam session must not send via LiteNetLib.
        private readonly INetTransport udpClientService;
        private readonly IStateBroadcastService stateBroadcastService;
        private readonly IPlayerManagerService playerManagerService;
        private readonly IProjectileManagerService projectileManagerService;
        private readonly IEnemyManagerService enemyManagerService;
        private readonly IPickupManagerService pickupManagerService;
        private readonly IChestManagerService chestManagerService;
        private readonly ISpawnedObjectManagerService spawnedObjectManagerService;
        private readonly IFinalBossOrbManagerService finalBossOrbManagerService;
        private readonly IGameBalanceService gameBalanceService;
        private readonly IEncounterService encounterService;
        private readonly IReadinessService readinessService;
        private readonly ILobbyViewService lobbyViewService;
        private readonly ITrackerService trackerService;
        private readonly ILobbySettingsService lobbySettingsService;
        private readonly IEndlessRunService endlessRunService;
        private readonly ManualLogSource logger;
        private readonly ConcurrentBag<SpawnedObject> toSpawns = [];
        private readonly ConcurrentBag<SpawnedObjectInCrypt> toUpdate = [];

        /// <summary>Retry passes per unmatched host crypt object; past the cap it is not coming.</summary>
        private readonly Dictionary<uint, int> cryptUpdateAttempts = [];
        private const int MAX_CRYPT_UPDATE_ATTEMPTS = 20;

        /// <summary>Retry passes per unspawned host object. Counted only while Started, so a merely late prefab is not dropped.</summary>
        private readonly Dictionary<uint, int> spawnAttempts = [];
        private const int MAX_SPAWN_ATTEMPTS = 20;
        private readonly ConcurrentQueue<SpawnedEnemy> pendingEnemySpawns = new();
        private readonly HashSet<uint> enemiesDiedBeforeSpawn = [];

        // Host only. Hits applied this enemy tick, summed per enemy, attacker, source and flags, sent batched.
        private readonly EnemyDamageAggregator enemyDamageAggregator = new();
        private readonly Action<EnemyDamagedBatch> sendEnemyDamagedToClients;

        private readonly Dictionary<EEnemy, Material> clonedExtraEnemyMaterials = [];
        private Coroutine newObjectToSpawnRoutine;
        private Coroutine pendingEnemySpawnRoutine;
        private Coroutine readyRetryRoutine;

        /// <summary>Invalidates a retry routine left over from a previous round. See <see cref="ClientReadyRoutine"/>.</summary>
        private int readyGeneration;

        /// <summary>Round the host acknowledged us for, or 0. Unlike replicated <c>IsReady</c>, cannot be stale from the previous round.</summary>
        private uint acknowledgedRoundId;

        /// <summary>Lobby-ready defect A. See <see cref="ClientReadyRoutine"/>.</summary>
        private const float ReadyRetrySeconds = Tunables.READY_RETRY_SECONDS;

        /// <summary>~30 s of re-reporting, then it gives up and says who.</summary>
        private const int ReadyRetryAttempts = Tunables.READY_RETRY_ATTEMPTS;
        private const int MAX_ENEMY_SPAWNS_PER_FRAME = Tunables.MAX_ENEMY_SPAWNS_PER_FRAME;
        private readonly ConcurrentDictionary<uint, ICollection<uint>> shrineChargingPlayers = new();

        /// <summary>Guards <see cref="OnBossDefeated"/> against its two call sites both firing.</summary>
        private bool hasHandledBossDefeatedThisStage = false;
        private readonly ConcurrentDictionary<uint, ICollection<uint>> pylonChargingPlayers = new();
        private readonly ConcurrentDictionary<uint, ICollection<uint>> lampsChargingPlayers = [];
        private readonly List<GameObject> specificDesertGraves = [];
        private InteractableCoffin currentCoffin = null;

        private CancellationTokenSource cancellationTokenSource = new();
        private CancellationToken cancellationToken = default;

        private State currentState = State.None;

        public SynchronizationService(
            IPlayerManagerService playerManagerService,
            IEnemyManagerService enemyManagerService,
            ManualLogSource logger,
            INetTransport udpClientService,
            IStateBroadcastService stateBroadcastService,
            IProjectileManagerService projectileManagerService,
            IPickupManagerService pickupManagerService,
            IChestManagerService chestManagerService,
            ISpawnedObjectManagerService spawnedObjectManagerService,
            IFinalBossOrbManagerService finalBossOrbManagerService,
            IGameBalanceService gameBalanceService,
            IEncounterService encounterService,
            IReadinessService readinessService,
            ILobbyViewService lobbyViewService,
            ITrackerService trackerService,
            ILobbySettingsService lobbySettingsService,
            IEndlessRunService endlessRunService
            )
        {
            this.lobbySettingsService = lobbySettingsService;
            this.endlessRunService = endlessRunService;
            this.playerManagerService = playerManagerService;
            this.enemyManagerService = enemyManagerService;
            this.projectileManagerService = projectileManagerService;
            this.pickupManagerService = pickupManagerService;
            this.chestManagerService = chestManagerService;
            this.spawnedObjectManagerService = spawnedObjectManagerService;
            this.finalBossOrbManagerService = finalBossOrbManagerService;
            this.gameBalanceService = gameBalanceService;
            this.encounterService = encounterService;
            this.readinessService = readinessService;
            this.lobbyViewService = lobbyViewService;
            this.trackerService = trackerService;
            this.logger = logger;

            // Cached so a flush does not allocate a delegate per tick.
            sendEnemyDamagedToClients = SendEnemyDamagedBatch;

            EventManager.SubscribeSpawnedObjectsEvents(OnNewObjectToSpawn);
            EventManager.SubscribePlayerUpdatesEvents(OnPlayerUpdate);
            EventManager.SubscribeSpawnedEnemyEvents(OnReceivedSpawnedEnemy);
            EventManager.SubscribeEnemyMaxHpScaledEvents(OnReceivedEnemyMaxHpScaled);
            EventManager.SubscribeSelectedCharacterEvents(OnReceivedSelectedCharacter);
            EventManager.SubscribeEnemiesUpdateEvents(OnReceivedEnemiesUpdate);
            EventManager.SubscribeEnemyDiedEvents(OnReceivedEnemyDied);
            EventManager.SubscribeSpawnedProjectileEvents(OnReceivedSpawnedProjectile);
            EventManager.SubscribeProjectileDoneEvents(OnReceivedProjectileDone);
            EventManager.SubscribeSpawnedPickupOrbEvents(OnReceivedSpawnedOrbPickup);
            EventManager.SubscribeSpawnedPickupEvents(OnReceivedSpawnedPickup);
            EventManager.SubscribePickupAppliedEvents(OnReceivedPickupApplied);
            EventManager.SubscribePickupFollowingPlayerEvents(OnReceivedPickupFollowingPlayer);
            EventManager.SubscribeSpawnedChestEvents(OnReceivedSpawnedChest);
            EventManager.SubscribeChestOpenedEvents(OnReceivedChestOpened);
            EventManager.SubscribeWeaponAddedEvents(OnReceivedWeaponAdded);
            EventManager.SubscribeInteractableUsedEvents(OnReceivedInteractableUsed);
            EventManager.SubscribeStartingChargingShrineEvents(OnReceivedStartingToChargingShrine);
            EventManager.SubscribeStoppingChargingShrineEvents(OnReceivedStoppingChargingShrine);
            EventManager.SubscribeEnemyExploderEvents(OnReceivedEnemyExploder);
            EventManager.SubscribeEnemyDamagedEvents(OnReceivedEnemyDamaged);
            EventManager.SubscribeEnemyDamagedBatchEvents(OnReceivedEnemyDamagedBatch);
            EventManager.SubscribeSpawnedEnemySpecialAttackEvents(OnReceivedSpawnedEnemySpecialAttack);
            EventManager.SubscribeStartingChargingPylonEvents(OnReceivedStartingToChargingPylon);
            EventManager.SubscribeStoppingChargingPylonEvents(OnReceivedStoppingChargingPylon);
            EventManager.SubscribeFinalBossOrbSpawnedEvents(OnReceivedFinalBossOrbsSpawned);
            EventManager.SubscribeFinalBossOrbsUpdateEvents(OnReceivedFinalBossOrbsUpdate);
            EventManager.SubscribeFinalBossOrbDestroyedEvents(OnReceivedFinalBossOrbDestroyed);
            EventManager.SubscribeStartedSwarmEventEvents(OnReceivedSwarmEvent);
            EventManager.SubscribeGameOverEvents(OnReceivedGameOver);
            EventManager.SubscribePlayerDiedEvents(OnReceivedPlayerDied);
            EventManager.SubscribeRetargetedEnemiesEvents(OnReceivedRetargetedEnemies);
            EventManager.SubscribeRunStartedEvents(OnReceivedRunStarted);
            EventManager.SubscribePlayerDisconnectedEvents(OnReceivedPlayerDisconnected);
            EventManager.SubscribeProjectilesUpdateEvents(OnReceivedProjectilesUpdate);
            EventManager.SubscribeTomeAddedEvents(OnReceivedTomeAdded);
            EventManager.SubscribeLightningStrikeEvents(OnReceivedLightningStrike);
            EventManager.SubscribeTornadoesSpawnedEvents(OnReceivedTornadoesSpawned);
            EventManager.SubscribeStormStartedEvents(OnReceivedStormStarted);
            EventManager.SubscribeStormStoppedEvents(OnReceivedStormStopped);
            EventManager.SubscribeTumbleWeedSpawnedEvents(OnReceivedTumbleWeedSpawned);
            EventManager.SubscribeTumbleWeedsUpdateEvents(OnReceivedTumbleWeedsUpdate);
            EventManager.SubscribeTumbleWeedDespawnedEvents(OnReceivedTumbleWeedDespawned);
            EventManager.SubscribeInteractableCharacterFightEnemySpawnedEvents(OnReceivedInteractableFightEnemySpawned);
            EventManager.SubscribeWantToStartFollowingPickupEvents(OnReceivedWantToStartFollowingPickup);
            EventManager.SubscribeItemAddedEvents(OnReceivedItemAdded);
            EventManager.SubscribeItemRemovedEvents(OnReceivedItemRemoved);
            EventManager.SubscribeWeaponToggledEvents(OnReceivedWeaponToggled);
            EventManager.SubscribeSpawnedObjectInCryptEvents(OnReceivedSpawnedObjectInCrypt);
            EventManager.SubscribeStartingChargingLampEvents(OnReceivedStartingToChargingLamp);
            EventManager.SubscribeStoppingChargingLampEvents(OnReceivedStoppingChargingLamp);
            EventManager.SubscribeTimerStartedEvents(OnReceivedTimerStarted);
            EventManager.SubscribeHatChangedEvents(OnReceivedHatChanged);
            EventManager.SubscribeSpawnedReviverEvents(OnReceivedSpawnedReviver);
            EventManager.SubscribePlayerRespawnedEvents(OnReceivedPlayerRespawned);
            EventManager.SubscribeAddXpEvents(OnReceivedAddXp);
            EventManager.SubscribeCloseEncounterEvents(OnReceivedCloseEncounter);
            EventManager.SubscribeCloseEncounterStampedEvents(OnReceivedCloseEncounterStamped);
            EventManager.SubscribeReadinessRoundStartedEvents(OnReceivedReadinessRoundStarted);
            EventManager.SubscribeReadinessRoundReAskEvents(BroadcastReadinessRound);
            EventManager.SubscribeReleaseBarrierEvents(ReleaseBarrier);
            EventManager.SubscribeGoldChangedEvents(OnReceivedChangeGold);
            EventManager.SubscribeChargeShrineCompletedEvents(OnReceivedChargeShrineCompleted);
            EventManager.SubscribeMineExplodedEvents(Patches.Projectiles.ProjectileMinesPatches.OnReceivedMineExploded);
            EventManager.SubscribeFlaskExplodedEvents(Patches.Projectiles.ProjectilePoisonFlaskPatches.OnReceivedFlaskExploded);

            cancellationToken = cancellationTokenSource.Token;
            this.udpClientService = udpClientService;
            this.stateBroadcastService = stateBroadcastService;
        }

        public bool IsLoading()
        {
            return currentState == State.Loading;
        }

        public bool IsLobbyReady()
        {
            var host = playerManagerService.GetHost();
            if (host == null)
            {
                logger.LogWarning("No host found when checking if lobby is ready.");
                return false;
            }

            var allPlayers = playerManagerService.GetAllPlayers();

            if (allPlayers.Count() < 2)
            {
                logger.LogInfo("Not enough players to start the game.");
                return false;
            }

            // Defects B/C: the host reads the barrier, not the replicated IsReady flag, which gets overwritten.
            if (IsServerMode() ?? false)
            {
                return readinessService.AreAllParticipantsReady() && udpClientService.HasAllPeersConnected();
            }

            // Defect C: replicated IsReady flags can still carry the previous round, so a client also
            // needs the host's ack for this round. The flag check says every other peer is ready.
            return readinessService.HasStamp
                && acknowledgedRoundId == readinessService.RoundId
                && playerManagerService.GetAllPlayers().All(p => p.IsReady)
                && udpClientService.HasAllPeersConnected();
        }

        public bool? IsServerMode()
        {
            return udpClientService.IsHost();
        }

        public void Reset()
        {
            currentState = State.None;
            lobbySettingsService.Reset();
            endlessRunService.Reset();
#if DEBUG
            Helpers.TestLoadout.Reset();
#endif
            DesyncDetectorService.ClearEnemyDiedReceived();
            pendingSharedXp = 0;
            toSpawns.Clear();
            toUpdate.Clear();
            cryptUpdateAttempts.Clear();
            spawnAttempts.Clear();

            shrineChargingPlayers.Clear();
            pylonChargingPlayers.Clear();

            // OB-9: leak only; enemy ids are never reused.
            pendingRescaledHp.Clear();

            enemyDamageAggregator.Clear();

            // Left latched, the next session's first portal would never open.
            hasHandledBossDefeatedThisStage = false;

            // Barrier state must not survive teardown. The generation bump is what reliably stops
            // the yielding retry routine; Stop() alone can race it.
            readyGeneration++;
            CoroutineRunner.Instance.Stop(readyRetryRoutine);
            readyRetryRoutine = null;
            readinessService.ResetSession();

            lobbyViewService.ResetReadyState();

            encounterService.ClearClosedEncounters();

            // SE-5: round state is per run; teardown is the only place it is dropped.
            encounterService.ResetSession();

            cancellationTokenSource.Cancel();
            cancellationTokenSource = new CancellationTokenSource();
            cancellationToken = cancellationTokenSource.Token;
            PrepareForNextLevel();
            playerManagerService.Reset();
            enemyManagerService.ResetReviverSpawnCounts();

            Plugin.Instance.HasDungeonTimerStarted = false;
        }

        public void OnSpawnedObject(GameObject obj)
        {
            if (GameManager.Instance == null || GameManager.Instance.player == null)
            {
                return; //Game not started yet, ignore menu interactables !
            }
            var id = spawnedObjectManagerService.AddSpawnedObject(obj);
            SendSpawnedObject(id, obj);
        }

        public void OnNewObjectToSpawn(SpawnedObject toSpawn)
        {
            toSpawns.Add(toSpawn);
        }

        public void OnPlayerUpdate(PlayerUpdate playerUpdate)
        {
            if (currentState < State.Started)
            {
                return;
            }

            if (playerUpdate == null)
            {
                logger.LogWarning("Received null PlayerUpdate.");
                return;
            }

            if (playerUpdate.ConnectionId == playerManagerService.GetLocalPlayer().ConnectionId) // Ignore updates for local player
            {
                return;
            }

            // Spawn a missing avatar here; otherwise a peer that missed the lobby-ready spawn stays invisible.
            var netplayer = playerManagerService.EnsureNetPlayerSpawned(playerUpdate.ConnectionId);

            if (netplayer == null)
            {
                // Unknown peer. GetPlayer already logs (throttled); don't log at 60 Hz here.
                return;
            }

            netplayer.AddUpdate(playerUpdate);

            netplayer.Inventory.playerHealth.hp = (int)playerUpdate.Hp;
            netplayer.Inventory.playerHealth.maxHp = (int)playerUpdate.MaxHp;
            //netplayer.Inventory.playerXp.xp = (int)playerUpdate.Xp;
            netplayer.Inventory.playerHealth.shield = (int)playerUpdate.Shield;
            netplayer.Inventory.playerHealth.maxShield = (int)playerUpdate.MaxShield;

            Plugin.Instance.NetPlayersDisplayer.OnUpdate(playerUpdate);
        }

        private void OnReceivedProjectilesUpdate(IEnumerable<Projectile> projectiles)
        {
            if (currentState < State.Started)
            {
                return;
            }

            if (projectiles == null || !projectiles.Any())
            {
                return;
            }

            var projectileSnapshots = new List<ProjectileSnapshot>();
            foreach (var proj in projectiles)
            {
                projectileSnapshots.Add(new ProjectileSnapshot
                {
                    Timestamp = Time.timeAsDouble,
                    Id = proj.Id,
                    Position = Quantizer.Dequantize(proj.Position),
                    Rotation = Quantizer.Dequantize(proj.FordwardVector)
                });
            }

            projectileManagerService.UpdateProjectileSnapshots(projectileSnapshots);
        }

        private bool SpawnObject(SpawnedObject toSpawn)
        {
            var prefab = Plugin.Instance.GetPrefab(toSpawn.PrefabName);
            if (prefab == null)
            {
                return false;
            }

            // Instantiate inactive so Awake (which can cache transforms, e.g. the charge shrine's rune
            // stone) runs only after the position is set. Prefab state restored in finally (P0-6).
            var prefabWasActive = prefab.activeSelf;
            GameObject spawned;

            try
            {
                prefab.SetActive(false);
                spawned = HandleSpawn(prefab);
            }
            finally
            {
                prefab.SetActive(prefabWasActive);
            }

            if (spawned == null)
            {
                logger.LogWarning($"Failed to instantiate prefab: {toSpawn.PrefabName}");
                return false;
            }

            spawned.transform.position = toSpawn.Position.ToUnityVector3();
            spawned.transform.rotation = toSpawn.Rotation.ToUnityQuaternion();
            spawned.transform.localScale = toSpawn.Scale.ToUnityVector3();

            // Before the GetComponentInChildren lookups below, which skip inactive hierarchies.
            // Desert graves stay inactive for the grave sequence to reveal.
            if (prefabWasActive && !IsDeferredRevealSpawn(toSpawn.PrefabName))
            {
                spawned.SetActive(true);
            }

            spawnedObjectManagerService.SetSpawnedObject(toSpawn.Id, spawned);

            if (toSpawn.SpecificData != null && toSpawn.SpecificData.ShadyGuyRarity >= 0)
            {
                var shadyGuy = spawned.GetComponentInChildren<InteractableShadyGuy>();
                if (shadyGuy != null)
                {
                    DynamicData.For(shadyGuy).Set("rarity", (EItemRarity)toSpawn.SpecificData.ShadyGuyRarity);
                }

                var microWave = spawned.GetComponentInChildren<InteractableMicrowave>();
                if (microWave != null)
                {
                    DynamicData.For(microWave).Set("rarity", (EItemRarity)toSpawn.SpecificData.ShadyGuyRarity);
                }
            }

            DynamicData.For(spawned).Set("netplayId", toSpawn.Id);
            return true;
        }

        /// <summary>
        /// Matches a host crypt object to ours. True when done (matched or not), false to retry.
        /// Must not throw: the crypt can already be gone, and a throw kills <see cref="NewObjectToSpawnRoutine"/>.
        /// </summary>
        private bool UpdateObject(SpawnedObjectInCrypt toUpdate)
        {
            var rsg = RsgController.Instance;
            if (rsg == null || rsg.allPieces == null)
            {
                return true;
            }

            var allPieces = rsg.allPieces;
            var position = toUpdate.Position;

            if (toUpdate.IsCryptLeave)
            {
                if (rsg.rsgEnd == null)
                {
                    return true;
                }

                spawnedObjectManagerService.SetSpawnedObject(toUpdate.NetplayId, rsg.rsgEnd.gameObject);
                DynamicData.For(rsg.rsgEnd.gameObject).Set("netplayId", toUpdate.NetplayId);
                return true;
            }

            foreach (var piece in allPieces)
            {
                if (piece == null || piece.children == null)
                {
                    continue;
                }

                var children = Il2CppFindHelper.RuntimeGetComponentsInChildren<Component>(piece.children);

                foreach (var child in children)
                {
                    var quantizedPos = Quantizer.Quantize(child.transform.position);
                    if (quantizedPos.QuantizedX == position.QuantizedX &&
                        quantizedPos.QuantizedY == position.QuantizedY &&
                        quantizedPos.QuantizedZ == position.QuantizedZ)
                    {
                        spawnedObjectManagerService.SetSpawnedObject(toUpdate.NetplayId, child.gameObject);
                        DynamicData.For(child.gameObject).Set("netplayId", toUpdate.NetplayId);

                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Desert-grave chain objects, left inactive for the grave sequence to reveal.</summary>
        private static bool IsDeferredRevealSpawn(string prefabName)
        {
            return prefabName != null
                && (prefabName.Contains("DesertGrave") || prefabName.Contains("SkeletonKingStatue"));
        }

        private GameObject HandleSpawn(GameObject toSpawn)
        {
            if (!IsDeferredRevealSpawn(toSpawn.name))
            {
                return GameObject.Instantiate(toSpawn);
            }

            if (toSpawn.name == "SkeletonKingStatue")
            {
                var skeletonKingStatue = GameObject.Instantiate(toSpawn);
                specificDesertGraves.Add(skeletonKingStatue);
                skeletonKingStatue.gameObject.SetActive(false);
                return skeletonKingStatue;
            }

            if (toSpawn.name.EndsWith("4"))
            {
                var skeletonKingStatue = specificDesertGraves.FirstOrDefault(g => g.name.Contains("SkeletonKingStatue"));
                if (skeletonKingStatue != null)
                {
                    var desertGrave4 = GameObject.Instantiate(toSpawn);
                    desertGrave4.GetComponent<InteractableDesertGrave>().nextShrine = skeletonKingStatue.GetComponent<ShrineSpawnAnimation>();
                    specificDesertGraves.Add(desertGrave4);
                    desertGrave4.gameObject.SetActive(false);
                    return desertGrave4;
                }

                return null;
            }

            if (toSpawn.name.EndsWith("3"))
            {
                var desertGrave4 = specificDesertGraves.FirstOrDefault(g => g.name.Contains("DesertGrave4"));
                if (desertGrave4 != null)
                {
                    var desertGrave3 = GameObject.Instantiate(toSpawn);
                    desertGrave3.GetComponent<InteractableDesertGrave>().nextShrine = desertGrave4.GetComponent<ShrineSpawnAnimation>();
                    specificDesertGraves.Add(desertGrave3);
                    desertGrave3.gameObject.SetActive(false);
                    return desertGrave3;
                }
                return null;
            }

            if (toSpawn.name.EndsWith("2"))
            {
                var desertGrave3 = specificDesertGraves.FirstOrDefault(g => g.name.Contains("DesertGrave3"));
                if (desertGrave3 != null)
                {
                    var desertGrave2 = GameObject.Instantiate(toSpawn);
                    desertGrave2.GetComponent<InteractableDesertGrave>().nextShrine = desertGrave3.GetComponent<ShrineSpawnAnimation>();
                    specificDesertGraves.Add(desertGrave2);
                    desertGrave2.gameObject.SetActive(false);
                    return desertGrave2;
                }
                return null;
            }

            if (toSpawn.name.EndsWith("1"))
            {
                var desertGrave2 = specificDesertGraves.FirstOrDefault(g => g.name.Contains("DesertGrave2"));
                if (desertGrave2 != null)
                {
                    var desertGrave1 = GameObject.Instantiate(toSpawn);
                    desertGrave1.GetComponent<InteractableDesertGrave>().nextShrine = desertGrave2.GetComponent<ShrineSpawnAnimation>();
                    specificDesertGraves.Add(desertGrave1);
                    return desertGrave1;
                }
                return null;
            }

            return null;
        }

        public void StartGame()
        {
            if (currentState >= State.Started)
            {
                logger.LogWarning("Game has already started.");
                return;
            }

            currentState = State.Started;
            EventManager.OnGameStarted();

#if DEBUG
            // OB-45 test loadout. After Started, so the item-add postfix replicates each item.
            Helpers.TestLoadout.GrantOnce();

            // The lobby is closed once a run starts; stop test bots joining from the room-code file.
            if (IsServerMode() ?? false)
            {
                BotRoomCodeFile.Delete();
            }
#endif

            var pause = UiManager.Instance.pause; //Disable restart buttons in pause menu
            var buttons = Il2CppFindHelper.RuntimeGetComponentsInChildren<MyButton>(pause);
            foreach (var button in buttons)
            {
                if (button.name == "B_Restart") //If one day we want to disable setting : button.name == "B_Settings" 
                {
                    button.state = MyButton.EButtonState.Inactive;
                    button.RefreshState();
                }
            }

            PickupManager.Instance.xpList.maxObjects = 10000; //Increase max Xp pickup since on netplay, there are more enemies
            PickupManager.Instance.goldList.maxObjects = 10000; //Increase max Gold pickup since on netplay, there are more enemies

            var allNetPlayers = playerManagerService.GetAllPlayersExceptLocal();
            var minimapCamera = GameManager.Instance.player.minimapCamera.GetComponent<MinimapCamera>();
            foreach (var netPlayer in allNetPlayers)
            {
                Plugin.Instance.NetPlayersDisplayer.AddPlayer(netPlayer);
                var spawnedPlayer = playerManagerService.EnsureNetPlayerSpawned(netPlayer.ConnectionId);
                var playerColor = Plugin.Instance.NetPlayersDisplayer.GetPlayerColor(netPlayer.ConnectionId);

                // Both null checks are load-bearing: an avatar can exist before its model, and a throw
                // here kills StartGame (and the WaitForLobbyReady coroutine). Two causes, two messages.
                if (spawnedPlayer == null)
                {
                    logger.LogWarning(
                        $"[netplayer] {netPlayer.ConnectionId} is in the roster but has no avatar, so "
                        + "it gets no minimap arrow. EnsureNetPlayerSpawned refused to build one - "
                        + "look for its reason above this line.");
                    continue;
                }

                if (spawnedPlayer.Model == null)
                {
                    logger.LogWarning(
                        $"[netplayer] {netPlayer.ConnectionId} has an avatar but no model, so it gets "
                        + "no minimap arrow and will be invisible. Initialize did not build one.");
                    continue;
                }

                minimapCamera.AddArrow(spawnedPlayer.Model.transform, playerColor);
            }

            gameBalanceService.Initialize();

            Plugin.Instance.PreventDeath();
        }

        private void SendSpawnedObject(uint netplayId, GameObject obj)
        {
            DynamicData.For(obj).Set("netplayId", netplayId);

            var characterFight = obj.GetComponentInChildren<InteractableCharacterFight>();
            if (characterFight != null)
            {
                DynamicData.For(characterFight.gameObject).Set("netplayId", netplayId);
            }

            var shadyGuy = obj.GetComponentInChildren<InteractableShadyGuy>();
            EItemRarity? rarity = null;
            if (shadyGuy != null)
            {
                rarity = shadyGuy.rarity;
            }

            var microWave = obj.GetComponentInChildren<InteractableMicrowave>();
            if (microWave != null)
            {
                rarity = microWave.rarity;
            }

            var prefabName = obj.name.Split('(').FirstOrDefault();

            IGameNetworkMessage message = new SpawnedObject
            {
                Id = netplayId,
                PrefabName = prefabName,
                Position = obj.transform.position.ToNumericsVector3(),
                Rotation = obj.transform.rotation.ToNumericsQuaternion(),
                Scale = obj.transform.localScale.ToNumericsVector3(),
                SpecificData = new Specific
                {
                    ShadyGuyRarity = rarity.HasValue ? (int)rarity.Value : -1
                }
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableUnordered);

        }

        public void TransitionToState(GameEvent gameEvent)
        {
            switch (gameEvent)
            {
                case GameEvent.Loading:
                    PickupManager.maxXpObjects = 10000;
                    PickupManager.maxGoldObjects = 10000;
                    PickupManager.maxPowerupsOnMap = 1000;

                    currentState = State.Loading;
                    break;
                case GameEvent.Ready:
                    if (currentState == State.Ready)
                    {
                        logger.LogWarning("Client is already in Ready state.");
                        break;
                    }
                    currentState = State.Ready;

                    Plugin.Instance.HasDungeonTimerStarted = false;

                    var isServer = IsServerMode() ?? false;
                    Player player = isServer ? playerManagerService.GetHost() : playerManagerService.GetLocalPlayer();

                    logger.LogInfo($"Players in lobby: {string.Join(", ", playerManagerService.GetAllPlayers().Select(p => p.ConnectionId.ToString()))}");

                    if (player == null)
                    {
                        logger.LogWarning("no player not found when transitioning to Ready state.");
                        break;
                    }

                    if (isServer)
                    {
                        // Host only. A client must never write its own IsReady: ClientReadyRoutine reads
                        // it as the host's acknowledgement.
                        player.IsReady = true;
                        playerManagerService.UpdatePlayer(player);

                        // Participants captured once; a live count moves under the barrier (cf. SE-6).
                        var participants = playerManagerService.GetAllPlayers().Select(p => p.ConnectionId).ToList();
                        var stamp = readinessService.OpenRound(participants);

                        readinessService.TryMarkReady(player.ConnectionId, stamp.SessionId, stamp.RoundId);

                        BroadcastReadinessRound();
                    }
                    else
                    {
                        HandleGameEvent(gameEvent);
                    }

                    break;
                case GameEvent.Start:
                    if (currentState < State.Ready)
                    {
                        logger.LogWarning("Cannot start game when not in Ready state.");
                        break;
                    }
                    if (!IsLobbyReady())
                    {
                        logger.LogWarning("Cannot start game when lobby is not ready.");
                        break;
                    }

                    playerManagerService.SpawnPlayers();

                    StartGame();

                    if (IsServerMode() == false)
                    {
                        CoroutineRunner.Instance.Stop(newObjectToSpawnRoutine);
                        newObjectToSpawnRoutine = CoroutineRunner.Instance.Run(NewObjectToSpawnRoutine());

                        CoroutineRunner.Instance.Stop(pendingEnemySpawnRoutine);
                        pendingEnemySpawnRoutine = CoroutineRunner.Instance.Run(PendingEnemySpawnRoutine());

                        EnemyManager.Instance.summonerController.timeline.events.Clear(); //Remove all timelines events from client, will be handled by server
                    }
                    break;
                case GameEvent.PortalOpened:
                    currentState = State.LoadingNextLevel;

                    // Before ResetForNextLevel, so the previous round cannot read as satisfied mid-transition.
                    readinessService.CloseRound();

                    playerManagerService.ResetForNextLevel();
                    PrepareForNextLevel();
                    Plugin.Instance.ClearPrefabs();
                    Plugin.Instance.RestoreDeath(false);

                    // OB-20: after RestoreDeath, which resets the spectator camera.
                    ReviveLocalPlayerIfDeadForNextLevel();

                    CoroutineRunner.Instance.Stop(LevelUpScreenPatches.CurrentRoutine);
                    //CoroutineRunner.Instance.Stop(EncounterUiPatches.CurrentRoutine);
                    CoroutineRunner.Instance.Stop(ChestWindowUiPatches.CurrentRoutine);

                    EventManager.OnPortalOpened();

                    break;
                case GameEvent.FinalPortalOpened:
                    currentState = State.Endgame;
                    break;
                case GameEvent.GameOver:
                    currentState = State.GameOver;
                    Plugin.Instance.RestoreDeath(true);
                    break;
                default:
                    logger.LogWarning($"Unhandled client event: {gameEvent}");
                    break;
            }
        }

        public void PrepareForNextLevel()
        {
            // OB-40. Pooled enemies are reused by the next stage.
            Patches.Enemies.DebuffAttribution.Clear();
            DesyncDetectorService.ClearEnemyDiedReceived(); // OB-51: enemy ids restart per stage

            encounterService.ClearClosedEncounters();

            hasHandledBossDefeatedThisStage = false;

            spawnedObjectManagerService.ResetForNextLevel();
            enemyManagerService.ResetForNextLevel();
            enemyDamageAggregator.Clear();
            projectileManagerService.ResetForNextLevel();
            pickupManagerService.ResetForNextLevel();
            chestManagerService.ResetForNextLevel();
            specificDesertGraves.Clear();
            currentCoffin = null;

            pendingEnemySpawns.Clear();
            enemiesDiedBeforeSpawn.Clear();

            // Queued objects belong to the level they were sent in.
            while (toUpdate.TryTake(out _)) { }
            cryptUpdateAttempts.Clear();

            while (toSpawns.TryTake(out _)) { }
            spawnAttempts.Clear();
            requestedShrineCompletions.Clear();
            Patches.Enemies.EnemyPatch.EnemiesDistanceThrottler.ClearAll();

            Plugin.Instance.NetPlayersDisplayer.ResetCards();
            Plugin.Instance.ClearMapEventsManager();
        }

        private void HandleGameEvent(GameEvent gameEvent)
        {
            switch (gameEvent)
            {
                case GameEvent.Ready:
                    CoroutineRunner.Instance.Stop(readyRetryRoutine);

                    // Diagnostic: if a round stalls and this line is missing, the routine never started.
                    logger.LogInfo($"[readiness] Starting the report routine (generation {readyGeneration + 1}).");
                    readyRetryRoutine = CoroutineRunner.Instance.Run(ClientReadyRoutine(++readyGeneration));
                    break;
                case GameEvent.Start:
                    break;
                default:
                    logger.LogWarning($"Unhandled game event: {gameEvent}");
                    break;
            }
        }
        /// <summary>
        /// Host. Broadcasts the open readiness round's stamp; resent when a report names the wrong round.
        /// </summary>
        private void BroadcastReadinessRound()
        {
            if (!(IsServerMode() ?? false) || !readinessService.HasStamp)
            {
                return;
            }

            var missing = readinessService.MissingParticipants();
            if (missing.Count > 0)
            {
                logger.LogInfo(
                    $"[readiness] Announcing round {readinessService.RoundId}; still waiting on " +
                    $"{missing.Count} peer(s): {string.Join(", ", missing)}.");
            }

            IGameNetworkMessage message = new ReadinessRoundStarted
            {
                SessionId = readinessService.SessionId,
                RoundId = readinessService.RoundId,
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        /// <summary>
        /// Client. Stores the host's round stamp; <see cref="ClientReadyRoutine"/> picks it up, so arrival order does not matter (defect D).
        /// </summary>
        private void OnReceivedReadinessRoundStarted(ReadinessRoundStarted started)
        {
            if (readinessService.AdoptStamp(started.SessionId, started.RoundId))
            {
                logger.LogInfo($"[readiness] Adopted round {started.RoundId} (session {started.SessionId}).");
            }
        }

        /// <summary>
        /// Lobby-ready defect A: re-reports readiness against the host's round stamp until the host's
        /// replicated <c>IsReady</c> acknowledges it. Reliable delivery is not enough; the host can drop
        /// the report (defects B, C). Generation-guarded and bounded by <see cref="ReadyRetryAttempts"/>.
        /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
        /// </summary>
        private IEnumerator ClientReadyRoutine(int generation)
        {
            var localPlayer = playerManagerService.GetLocalPlayer();
            if (localPlayer == null)
            {
                logger.LogWarning("[readiness] Cannot report: no local player.");
                yield break;
            }

            var connectionId = localPlayer.ConnectionId;

            // 0 = nothing reported yet; open round ids are never 0.
            uint reportedRoundId = 0;

            acknowledgedRoundId = 0;

            for (var attempt = 1; attempt <= ReadyRetryAttempts; attempt++)
            {
                if (generation != readyGeneration || currentState != State.Ready)
                {
                    logger.LogInfo(
                        $"[readiness] Stopping the report routine before attempt {attempt}: " +
                        (generation != readyGeneration
                            ? $"generation {generation} superseded by {readyGeneration}."
                            : $"state is {currentState}, not Ready."));
                    yield break;
                }

                // No stamp yet: wait rather than send a report the host would reject.
                if (readinessService.HasStamp)
                {
                    IGameNetworkMessage message = new ClientReadyStamped
                    {
                        ConnectionId = connectionId,
                        SessionId = readinessService.SessionId,
                        RoundId = readinessService.RoundId,
                    };

                    udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
                    reportedRoundId = readinessService.RoundId;

                    if (attempt == 1)
                    {
                        logger.LogInfo(
                            $"[readiness] Reported ready for round {readinessService.RoundId} " +
                            $"(session {readinessService.SessionId}).");
                    }

                    if (attempt > 1)
                    {
                        logger.LogInfo(
                            $"[readiness] Re-reporting for round {readinessService.RoundId} " +
                            $"(attempt {attempt}/{ReadyRetryAttempts}).");
                    }
                }
                else if (attempt > 1)
                {
                    logger.LogInfo($"[readiness] Still waiting for the host to name a round (attempt {attempt}/{ReadyRetryAttempts}).");
                }

                // unscaledDeltaTime: WaitForSeconds stalls at timeScale 0, and WaitForSecondsRealtime
                // under the IL2CPP coroutine driver is unverified.
                var waited = 0f;
                while (waited < ReadyRetrySeconds)
                {
                    yield return null;
                    waited += Time.unscaledDeltaTime;

                    if (generation != readyGeneration || currentState != State.Ready)
                    {
                        logger.LogInfo(
                            $"[readiness] Stopping the report routine while waiting on attempt {attempt}: " +
                            (generation != readyGeneration
                                ? $"generation {generation} superseded by {readyGeneration}."
                                : $"state is {currentState}, not Ready."));
                        yield break;
                    }

                    // Read fresh: the record is replaced wholesale. Defect C: IsReady may be the previous
                    // round's, so we must also have reported for the current round.
                    var acknowledged = playerManagerService.GetPlayer(connectionId);
                    if (acknowledged != null
                        && acknowledged.IsReady
                        && readinessService.HasStamp
                        && reportedRoundId == readinessService.RoundId)
                    {
                        acknowledgedRoundId = readinessService.RoundId;
                        logger.LogInfo(
                            $"[readiness] The host acknowledged round {readinessService.RoundId}; " +
                            "stopping the report routine.");
                        yield break;
                    }
                }
            }

            logger.LogWarning(
                $"[readiness] Peer {connectionId} gave up after {ReadyRetryAttempts} attempts over " +
                $"~{ReadyRetryAttempts * ReadyRetrySeconds:F0}s. Stamp held: {readinessService.HasStamp} " +
                $"(session {readinessService.SessionId}, round {readinessService.RoundId}). The host " +
                "has not acknowledged this peer as ready and the run will not start without it.");
        }

        private IEnumerator NewObjectToSpawnRoutine() //TODO: add a auto cancel after X seconds
        {
            var token = cancellationToken;
            while (!token.IsCancellationRequested)
            {
                if (currentState == State.Started)
                {
                    var canSpawn = toSpawns.Count > 0;
                    var unspawnedYet = new List<SpawnedObject>();
                    while (canSpawn)
                    {
                        if (toSpawns.TryTake(out var toSpawn))
                        {
                            if (SpawnObject(toSpawn))
                            {
                                spawnAttempts.Remove(toSpawn.Id);
                            }
                            else
                            {
                                spawnAttempts.TryGetValue(toSpawn.Id, out var attempts);
                                if (++attempts >= MAX_SPAWN_ATTEMPTS)
                                {
                                    spawnAttempts.Remove(toSpawn.Id);
                                    logger.LogInfo($"Gave up spawning {toSpawn.PrefabName} ({toSpawn.Id}) after {MAX_SPAWN_ATTEMPTS / 2} s; there is no such prefab here.");
                                }
                                else
                                {
                                    spawnAttempts[toSpawn.Id] = attempts;
                                    unspawnedYet.Add(toSpawn);
                                }
                            }
                        }
                        canSpawn = toSpawns.Count > 0;
                    }

                    foreach (var item in unspawnedYet) //Add back to retry later; logged once, when given up
                    {
                        toSpawns.Add(item);
                    }

                    var canUpdate = toUpdate.Count > 0;
                    var unupdatedYet = new List<SpawnedObjectInCrypt>();
                    while (canUpdate)
                    {
                        if (toUpdate.TryTake(out var toUpd))
                        {
                            bool success;
                            try
                            {
                                success = UpdateObject(toUpd);
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning($"Dropped a crypt object update that failed: {ex.Message}");
                                success = true;
                            }

                            if (success)
                            {
                                cryptUpdateAttempts.Remove(toUpd.NetplayId);
                            }
                            else
                            {
                                cryptUpdateAttempts.TryGetValue(toUpd.NetplayId, out var attempts);
                                if (++attempts >= MAX_CRYPT_UPDATE_ATTEMPTS)
                                {
                                    cryptUpdateAttempts.Remove(toUpd.NetplayId);
                                    logger.LogInfo($"Gave up matching crypt object {toUpd.NetplayId} after {MAX_CRYPT_UPDATE_ATTEMPTS / 2} s; its crypt is not here.");
                                }
                                else
                                {
                                    cryptUpdateAttempts[toUpd.NetplayId] = attempts;
                                    unupdatedYet.Add(toUpd);
                                }
                            }
                        }
                        canUpdate = toUpdate.Count > 0;
                    }

                    foreach (var item in unupdatedYet) //Add back to retry later; logged once, when given up
                    {
                        toUpdate.Add(item);
                    }
                }

                yield return new WaitForSeconds(0.5f);
            }
        }

        public bool HasNetplaySessionStarted()
        {
            return currentState == State.Started;
        }

        public bool HasNetplaySessionInitialized()
        {
            return Plugin.Instance.NetworkHandler.HasFoundMatch.HasValue && Plugin.Instance.NetworkHandler.HasFoundMatch.Value;
        }

        public void OnSpawnedEnemy(Enemy enemy, EEnemy enemyName, Vector3 position, int waveNumber, bool forceSpawn, EEnemyFlag flag, bool canBeElite, float extraSizeMultiplier)
        {
            if (enemy == null)
            {
                logger.LogWarning("Enemy is null ?? when processing OnSpawnedEnemy.");
                return;
            }

            var dynEnemy = DynamicData.For(enemy);
            var targetId = dynEnemy.Get<uint?>("targetId");

            if (!targetId.HasValue)
            {
                logger.LogWarning("Enemy targetId not found in DynamicData when processing OnSpawnedEnemy.");
                EnemyManager.Instance.RemoveEnemy(enemy);
            }

            var netplayId = enemyManagerService.AddSpawnedEnemy(enemy);

            enemyManagerService.RebalanceIfNeededReviverEnemy(enemy, Plugin.Instance.CurrentReviver, Plugin.Instance.CurrentReviverOwner);

            IGameNetworkMessage message = new SpawnedEnemy
            {
                Flag = enemy.IsElite() ? (int)EEnemyFlag.Elite : (int)flag,
                Id = netplayId,
                TargetId = targetId.Value,
                Name = (int)enemyName,
                ShouldForce = forceSpawn,
                Position = position.ToNumericsVector3(),
                Wave = waveNumber,
                CanBeElite = enemy.IsElite(),
                Hp = enemy.hp,
                ExtraSizeMultiplier = extraSizeMultiplier,
                ReviverId = Plugin.Instance.CurrentReviver
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableUnordered);

            if (BossDiagnostics.IsBoss(enemy))
            {
                BossDiagnostics.LogEvent($"host sent spawn (flag {flag})", netplayId, enemy, enemy.hp);
            }
        }

        #region OB-9 — HP rescaled after the spawn message went out

        /// <summary>OB-9. Corrections that outran their spawn (both are ReliableUnordered), by enemy id.</summary>
        private readonly Dictionary<uint, float> pendingRescaledHp = [];

        /// <summary>OB-9. Host side; see <c>EnemyPatch.SetSwarmMultiplierHp_Postfix</c>.</summary>
        public void OnEnemyHpRescaled(Enemy enemy)
        {
            var registered = enemyManagerService.GetEnemyByReference(enemy);
            if (registered.Value == null)
            {
                return;
            }

            IGameNetworkMessage message = new EnemyMaxHpScaled
            {
                Id = registered.Key,
                Hp = enemy.hp
            };

            if (BossDiagnostics.IsBoss(enemy))
            {
                BossDiagnostics.LogEvent("host sent max-HP rescale", registered.Key, enemy, enemy.hp);
            }

            // Reliable: one-shot; the 40 Hz delta carries current HP only, never the max.
            udpClientService.SendToAllClients(message, NetDelivery.ReliableUnordered);
        }

        /// <summary>OB-9. Client side.</summary>
        private void OnReceivedEnemyMaxHpScaled(EnemyMaxHpScaled scaled)
        {
            var enemy = enemyManagerService.GetEnemyById(scaled.Id);
            if (enemy == null)
            {
                pendingRescaledHp[scaled.Id] = scaled.Hp;
                Plugin.Log.LogInfo($"[boss] client got max-HP rescale for id {scaled.Id} ({scaled.Hp:F0}) before its spawn; held.");
                return;
            }

            ApplyRescaledHp(enemy, scaled.Hp);
            if (BossDiagnostics.IsBoss(enemy))
            {
                BossDiagnostics.LogEvent("client applied max-HP rescale", scaled.Id, enemy, scaled.Hp);
            }
        }

        /// <summary>OB-9. Same four fields as the spawn path; a fresh enemy has current == max.</summary>
        private static void ApplyRescaledHp(Enemy enemy, float hp)
        {
            enemy.hp = hp;
            enemy.controlHp = hp;
            enemy.maxHp = hp;
            enemy._hp_k__BackingField = hp;

            // The boss bar only redraws on this event.
            if ((enemy.enemyFlag & Assets.Scripts.Actors.Enemies.EEnemyFlag.AnyBoss) != 0)
            {
                Enemy.A_HealthChange?.Invoke(enemy);
            }
        }

        /// <summary>OB-9. Applies a correction that outran its spawn.</summary>
        private void ApplyPendingRescaledHp(uint netplayId, Enemy enemy)
        {
            if (!pendingRescaledHp.TryGetValue(netplayId, out var hp))
            {
                return;
            }

            pendingRescaledHp.Remove(netplayId);
            ApplyRescaledHp(enemy, hp);
        }

        #endregion

        private void OnReceivedSpawnedEnemy(SpawnedEnemy spawnedEnemy)
        {
            // Accept in Ready too: the host's held final boss (OB-8) can arrive before our start.
            // Queued spawns wait for Started; LoadingNextLevel still drops old-stage spawns.
            if (currentState != State.Started && currentState != State.Ready)
            {
                return;
            }

            pendingEnemySpawns.Enqueue(spawnedEnemy);
        }

        private IEnumerator PendingEnemySpawnRoutine()
        {
            var token = cancellationToken;
            while (!token.IsCancellationRequested)
            {
                var budget = MAX_ENEMY_SPAWNS_PER_FRAME;
                while (budget-- > 0 && currentState == State.Started && pendingEnemySpawns.TryDequeue(out var spawnedEnemy))
                {
                    if (enemiesDiedBeforeSpawn.Remove(spawnedEnemy.Id))
                    {
                        continue; //Death message already arrived, don't spawn a ghost
                    }

                    try
                    {
                        ProcessSpawnedEnemy(spawnedEnemy);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"Failed to process spawned enemy {spawnedEnemy.Id}: {ex}");
                    }
                }

                yield return null;
            }
        }

        private void ProcessSpawnedEnemy(SpawnedEnemy spawnedEnemy)
        {
            var enemy = EnemyManager.Instance.SpawnEnemy
                (DataManager.Instance.GetEnemyData((EEnemy)spawnedEnemy.Name),
                spawnedEnemy.Position.ToUnityVector3(),
                spawnedEnemy.Wave,
                true,
                (EEnemyFlag)spawnedEnemy.Flag,
                spawnedEnemy.CanBeElite,
                spawnedEnemy.ExtraSizeMultiplier
            );

            if (enemy == null)
            {
                logger.LogWarning($"Failed to spawn enemy: {(EEnemy)spawnedEnemy.Name} at position: {spawnedEnemy.Position.ToUnityVector3()}");
                return;
            }

            var enemyName = enemy.enemyData.enemyName;
            var extraMaterial = spawnedObjectManagerService.GetExtraEnemyMaterial(enemyName);
            if (extraMaterial != null)
            {
                var original = enemy.renderer.sharedMaterial;

                if (!clonedExtraEnemyMaterials.TryGetValue(enemyName, out var clone) || clone == null)
                {
                    clone = new Material(extraMaterial);
                    clonedExtraEnemyMaterials[enemyName] = clone;
                }

                Il2CppFindHelper.RuntimeSetSharedMaterials(enemy.renderer, [original, clone]);
            }

            enemy.hp = spawnedEnemy.Hp;
            enemy.controlHp = spawnedEnemy.Hp;
            enemy.maxHp = spawnedEnemy.Hp;
            enemy._hp_k__BackingField = spawnedEnemy.Hp;

            if (BossDiagnostics.IsBoss(enemy))
            {
                BossDiagnostics.LogEvent($"client applied spawn (flag {spawnedEnemy.Flag})", spawnedEnemy.Id, enemy, spawnedEnemy.Hp);
            }

            if (enemy.IsFinalBoss())
            {
                MusicController.Instance.finalFightController.boss = enemy;
                MusicController.Instance.finalFightController.numWeaponsToTake = GameManager.Instance.player.inventory.weaponInventory.GetNumWeapons();
                MusicController.Instance.finalFightController.takeWeaponAtTime = MyTime.time + 4.0f;
            }

            if (MapController.currentMap.eMap == Assets.Scripts._Data.MapsAndStages.EMap.Desert) //TODO to test
            {
                if (enemy.enemyData.enemyName == EEnemy.Ghost)
                {
                    InteractableDesertGrave grave = specificDesertGraves.FirstOrDefault(go => go.name.Contains("DesertGrave1"))?.GetComponent<InteractableDesertGrave>();
                    if (grave != null) grave.myEnemy = enemy;
                }

                if (enemy.enemyData.enemyName == EEnemy.GreaterGhost)
                {
                    InteractableDesertGrave grave = specificDesertGraves.FirstOrDefault(go => go.name.Contains("DesertGrave2"))?.GetComponent<InteractableDesertGrave>();
                    if (grave != null) grave.myEnemy = enemy;
                }

                if (enemy.enemyData.enemyName == EEnemy.GhostPurple)
                {
                    InteractableDesertGrave grave = specificDesertGraves.FirstOrDefault(go => go.name.Contains("DesertGrave3"))?.GetComponent<InteractableDesertGrave>();
                    if (grave != null) grave.myEnemy = enemy;
                }

                if (enemy.enemyData.enemyName == EEnemy.GhostRed)
                {
                    InteractableDesertGrave grave = specificDesertGraves.FirstOrDefault(go => go.name.Contains("DesertGrave4"))?.GetComponent<InteractableDesertGrave>();
                    if (grave != null) grave.myEnemy = enemy;
                }

                if (enemy.enemyData.enemyName == EEnemy.CalciumDad)
                {
                    InteractableSkeletonKingStatue skeletonStatue = specificDesertGraves.FirstOrDefault(go => go.name.Contains("SkeletonKingStatue"))?.GetComponent<InteractableSkeletonKingStatue>();
                    if (skeletonStatue == null)
                    {
                        logger.LogWarning("SkeletonKingStatue not found for CalciumDad enemy.");
                    }

                    if (skeletonStatue != null) skeletonStatue.myEnemy = enemy;
                }
            }

            if (MapController.currentMap.eMap == Assets.Scripts._Data.MapsAndStages.EMap.Graveyard) //TODO to test
            {
                // Vanilla coffin ghosts only. Our revive ghost is also GhostGrave4 with no coffin
                // open; a throw here would skip its registration below.
                if (!spawnedEnemy.ReviverId.HasValue
                && currentCoffin?.minibossEnemies != null
                && (enemy.enemyData.enemyName == EEnemy.GhostGrave1
                || enemy.enemyData.enemyName == EEnemy.GhostGrave2
                || enemy.enemyData.enemyName == EEnemy.GhostGrave3
                || enemy.enemyData.enemyName == EEnemy.GhostGrave4))
                {
                    currentCoffin.minibossEnemies.Add(enemy);
                }

                if (enemy.enemyData.enemyName == EEnemy.GhostKing)
                {
                    RsgController.Instance.roomBoss.bossEnemy = enemy;
                    RsgController.Instance.roomBoss.RefreshBossArmor();
                    UiManager.Instance.objective.OnBossSpawned();
                }
            }

            var interpolator = enemy.gameObject.AddComponent<EnemyInterpolator>();
            interpolator.Initialize(enemy);
            enemyManagerService.SetSpawnedEnemy(spawnedEnemy.Id, enemy);

            // OB-9: apply a rescale that outran this spawn.
            ApplyPendingRescaledHp(spawnedEnemy.Id, enemy);

            DynamicData.For(enemy).Set("targetId", spawnedEnemy.TargetId);

            // FIX 3/6 + 4/6: cosmetic reviver naming stays after registration and inside a catch,
            // so a throw here cannot leave an unregistered, frozen enemy.
            if (spawnedEnemy.ReviverId.HasValue)
            {
                try
                {
                    var reviverObject = spawnedObjectManagerService.GetSpawnedObject(spawnedEnemy.ReviverId.Value);
                    var reviver = reviverObject != null ? reviverObject.GetComponent<InteractableReviver>() : null;

                    if (reviver != null)
                    {
                        reviver.SetSpawnedEnemy(enemy);
                        enemyManagerService.AddReviverEnemy_Name(enemy, reviver.GetFullName());
                    }
                    else
                    {
                        logger.LogWarning($"Reviver {spawnedEnemy.ReviverId.Value} not found for enemy {spawnedEnemy.Id}; enemy is registered, only its ghost name is missing.");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Could not name the reviver ghost for enemy {spawnedEnemy.Id}: {ex.Message}");
                }
            }
        }

        public void OnSpawnedProjectile(Il2CppObjectBase proj, uint? owner = null)
        {
            var instance = IL2CPP.PointerToValueGeneric<ProjectileBase>(proj.Pointer, false, false);

            // FIX P2-5: the manager needs the owner.
            var ownerId = owner ?? playerManagerService.GetLocalPlayer().ConnectionId;
            var netplayId = projectileManagerService.AddSpawnedProjectile(instance, ownerId);

            IGameNetworkMessage message;

            switch (instance.weaponBase.weaponData.eWeapon)
            {
                case EWeapon.Shotgun:
                    var shotgun = IL2CPP.PointerToValueGeneric<ProjectileShotgun>(proj.Pointer, false, false);
                    var muzzle = shotgun.weaponAttack.muzzle;
                    message = new SpawnedShotgunProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        MuzzlePosition = Quantizer.Quantize(muzzle.transform.position),
                        MuzzleRotation = Quantizer.Quantize(muzzle.transform.eulerAngles)
                    };
                    break;
                case EWeapon.Revolver:
                    var revolver = IL2CPP.PointerToValueGeneric<ProjectileBasic>(proj.Pointer, false, false);
                    message = new SpawnedRevolverProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        MuzzlePosition = Quantizer.Quantize(revolver.weaponAttack.muzzle.transform.position),
                        MuzzleRotation = Quantizer.Quantize(revolver.weaponAttack.muzzle.transform.eulerAngles)
                    };
                    break;
                case EWeapon.Axe:
                    var axe = IL2CPP.PointerToValueGeneric<ProjectileAxe>(proj.Pointer, false, false);
                    message = new SpawnedAxeProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        StartPosition = Quantizer.Quantize(axe.startPosition),
                        DesiredPosition = Quantizer.Quantize(axe.desiredPosition),
                    };
                    break;
                case EWeapon.BlackHole:
                    var blackHole = IL2CPP.PointerToValueGeneric<ProjectileBlackHole>(proj.Pointer, false, false);
                    message = new SpawnedBlackHoleProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        StartPosition = Quantizer.Quantize(blackHole.startPosition),
                        DesiredPosition = Quantizer.Quantize(blackHole.desiredPosition),
                        StartScaleSize = Quantizer.Quantize(blackHole.startScaleSize)
                    };
                    break;
                case EWeapon.CorruptSword:
                    var cringeSword = IL2CPP.PointerToValueGeneric<ProjectileCringeSword>(proj.Pointer, false, false);
                    message = new SpawnedCringeSwordProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        MovingProjectilePosition = Quantizer.Quantize(cringeSword.movingProjectilePosition),
                        MovingProjectileRotation = Quantizer.Quantize(cringeSword.movingProjectileRotation)
                    };
                    break;
                case EWeapon.Flamewalker:
                    var flameWalker = IL2CPP.PointerToValueGeneric<ProjectileFirefield>(proj.Pointer, false, false);
                    message = new SpawnedFireFieldProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        ExpirationTime = flameWalker.expirationTime
                    };
                    break;
                case EWeapon.HeroSword:
                    var heroSword = IL2CPP.PointerToValueGeneric<ProjectileHeroSword>(proj.Pointer, false, false);
                    message = new SpawnedHeroSwordProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        MovingProjectilePosition = Quantizer.Quantize(heroSword.movingProjectilePosition),
                        MovingProjectileRotation = Quantizer.Quantize(heroSword.movingProjectileRotation)
                    };
                    break;
                case EWeapon.Rockets:
                    var rocketProj = IL2CPP.PointerToValueGeneric<ProjectileRocket>(proj.Pointer, false, false);
                    message = new SpawnedRocketProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        RocketPosition = Quantizer.Quantize(rocketProj.rocket.transform.position),
                        RocketRotation = Quantizer.Quantize(rocketProj.rocket.transform.eulerAngles)
                    };
                    break;
                case EWeapon.Dexecutioner:
                    var dexecutionerProj = IL2CPP.PointerToValueGeneric<ProjectileDexecutioner>(proj.Pointer, false, false);
                    message = new SpawnedDexecutionerProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        AttackDir = Quantizer.Quantize(dexecutionerProj.attackDir),
                        Chance = dexecutionerProj.executionChance,
                        ForwardOffset = dexecutionerProj.forwardOffset,
                        ProjectileDistance = dexecutionerProj.projectileDistance,
                        UpOffset = dexecutionerProj.upOffset,
                        UseAudio = dexecutionerProj.useAudio
                    };
                    break;
                case EWeapon.Sniper:
                    var sniperProj = IL2CPP.PointerToValueGeneric<ProjectileSniper>(proj.Pointer, false, false);

                    // The muzzle can be missing at spawn; fall back rather than lose the shot.
                    var sniperMuzzle = sniperProj.weaponAttack != null && sniperProj.weaponAttack.muzzle != null
                        ? sniperProj.weaponAttack.muzzle.transform
                        : instance.transform;
                    message = new SpawnedSniperProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        MuzzlePosition = Quantizer.Quantize(sniperMuzzle.position),
                        MuzzleRotation = Quantizer.Quantize(sniperMuzzle.eulerAngles),
                    };
                    break;
                default:
                    message = new SpawnedProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon
                    };
                    break;
            }

            DynamicData.For(instance).Set("netplayId", netplayId);
            DynamicData.For(instance).Set("ownerId", ownerId);

            udpClientService.SendToAllClients(message, NetDelivery.ReliableUnordered);
        }

        private void OnReceivedSpawnedProjectile(AbstractSpawnedProjectile projectile)
        {
            try
            {
                PlayerInventory owner;

                if (playerManagerService.IsLocalConnectionId(projectile.OwnerId))
                {
                    owner = GameManager.Instance.player.inventory;
                }
                else
                {
                    // Null once the owner has left; their late projectiles are dropped.
                    var netPlayer = playerManagerService.GetNetPlayerByNetplayId(projectile.OwnerId);
                    if (netPlayer == null)
                    {
                        return;
                    }

                    owner = netPlayer.Inventory;
                }

                var weapons = owner.weaponInventory.weapons;
                var eweapon = (EWeapon)projectile.Weapon;
                if (weapons.TryGetValue(eweapon, out var weapon))
                {
                    var attack = weapon.weaponData.attack.GetComponent<WeaponAttack>();
                    attack.weaponBase = weapon;
                    var proj = GameObject.Instantiate(attack.prefabProjectile);

                    PoolHelper.EnsureWeaponPoolExists(eweapon);

                    switch ((EWeapon)projectile.Weapon)
                    {
                        case EWeapon.Axe:
                            var axeProjectile = projectile as SpawnedAxeProjectile;
                            var axeProjectileInstance = proj.GetComponent<ProjectileAxe>();
                            axeProjectileInstance.startPosition = Quantizer.Dequantize(axeProjectile.StartPosition);
                            axeProjectileInstance.desiredPosition = Quantizer.Dequantize(axeProjectile.DesiredPosition);
                            axeProjectileInstance.weaponBase = weapon;
                            axeProjectileInstance.weaponAttack = attack;
                            axeProjectileInstance.hitEnemies = new();

                            axeProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            axeProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            DynamicData.For(axeProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(axeProjectileInstance).Set("ownerId", projectile.OwnerId);
                            break;
                        case EWeapon.BlackHole:
                            var blackHoleProjectile = projectile as SpawnedBlackHoleProjectile;
                            var blackHoleProjectileInstance = proj.GetComponent<ProjectileBlackHole>();
                            blackHoleProjectileInstance.startPosition = Quantizer.Dequantize(blackHoleProjectile.StartPosition);
                            blackHoleProjectileInstance.desiredPosition = Quantizer.Dequantize(blackHoleProjectile.DesiredPosition);
                            blackHoleProjectileInstance.weaponBase = weapon;
                            blackHoleProjectileInstance.weaponAttack = attack;
                            blackHoleProjectileInstance.hitEnemies = new();
                            blackHoleProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            blackHoleProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            blackHoleProjectileInstance.startScaleSize = Quantizer.Dequantize(blackHoleProjectile.StartScaleSize);

                            DynamicData.For(blackHoleProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(blackHoleProjectileInstance).Set("ownerId", projectile.OwnerId);
                            break;
                        case EWeapon.CorruptSword:
                            var cringeSwordProjectile = projectile as SpawnedCringeSwordProjectile;
                            var cringeSwordProjectileInstance = proj.GetComponent<ProjectileCringeSword>();
                            cringeSwordProjectileInstance.weaponBase = weapon;
                            cringeSwordProjectileInstance.weaponAttack = attack;
                            cringeSwordProjectileInstance.hitEnemies = new();
                            cringeSwordProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            cringeSwordProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            cringeSwordProjectileInstance.movingProjectile.transform.position = Quantizer.Dequantize(cringeSwordProjectile.MovingProjectilePosition);
                            cringeSwordProjectileInstance.movingProjectile.transform.rotation = Quantizer.Dequantize(cringeSwordProjectile.MovingProjectileRotation);
                            DynamicData.For(cringeSwordProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(cringeSwordProjectileInstance).Set("ownerId", projectile.OwnerId);
                            break;
                        case EWeapon.Flamewalker:
                            var fireFieldProjectile = projectile as SpawnedFireFieldProjectile;
                            var fireFieldProjectileInstance = proj.GetComponent<ProjectileFirefield>();
                            fireFieldProjectileInstance.expirationTime = fireFieldProjectile.ExpirationTime;
                            fireFieldProjectileInstance.weaponBase = weapon;
                            fireFieldProjectileInstance.weaponAttack = attack;
                            fireFieldProjectileInstance.hitEnemies = new();
                            fireFieldProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            fireFieldProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            fireFieldProjectileInstance.TryInit(0);
                            DynamicData.For(fireFieldProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(fireFieldProjectileInstance).Set("ownerId", projectile.OwnerId);
                            break;
                        case EWeapon.HeroSword:
                            var heroSwordProjectile = projectile as SpawnedHeroSwordProjectile;
                            var heroSwordProjectileInstance = proj.GetComponent<ProjectileHeroSword>();
                            heroSwordProjectileInstance.weaponBase = weapon;
                            heroSwordProjectileInstance.weaponAttack = attack;
                            heroSwordProjectileInstance.hitEnemies = new();
                            heroSwordProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            heroSwordProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            heroSwordProjectileInstance.movingProjectile.transform.position = Quantizer.Dequantize(heroSwordProjectile.MovingProjectilePosition);
                            heroSwordProjectileInstance.movingProjectile.transform.rotation = Quantizer.Dequantize(heroSwordProjectile.MovingProjectileRotation);
                            DynamicData.For(heroSwordProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(heroSwordProjectileInstance).Set("ownerId", projectile.OwnerId);
                            break;
                        case EWeapon.Rockets:
                            var rocketProjectile = projectile as SpawnedRocketProjectile;
                            var rocketProjectileInstance = proj.GetComponent<ProjectileRocket>();
                            rocketProjectileInstance.weaponBase = weapon;
                            rocketProjectileInstance.weaponAttack = attack;
                            rocketProjectileInstance.hitEnemies = new();
                            rocketProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            rocketProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            rocketProjectileInstance.rocket.transform.position = Quantizer.Dequantize(rocketProjectile.RocketPosition);
                            rocketProjectileInstance.rocket.transform.eulerAngles = Quantizer.Dequantize(rocketProjectile.RocketRotation);
                            DynamicData.For(rocketProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(rocketProjectileInstance.rocket).Set("ownerId", projectile.OwnerId);
                            Patches.Projectiles.RocketPatches.MarkReceived(rocketProjectileInstance.rocket);
                            break;
                        case EWeapon.Shotgun:
                            var message = projectile as SpawnedShotgunProjectile;
                            var projectileShotgun = proj.GetComponent<ProjectileShotgun>();
                            projectileShotgun.weaponBase = weapon;
                            projectileShotgun.weaponAttack = attack;
                            projectileShotgun.hitEnemies = new();
                            projectileShotgun.TryInit(0);
                            projectileShotgun.psBullets.transform.position = Quantizer.Dequantize(projectile.Position);
                            projectileShotgun.psBullets.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            DynamicData.For(projectileShotgun).Set("netplayId", projectile.Id);
                            DynamicData.For(projectileShotgun).Set("ownerId", projectile.OwnerId);

                            if (attack.prefabMuzzle != null)
                            {
                                var muzzle = GameObject.Instantiate(attack.prefabMuzzle);
                                AttackMuzzle attMuzzle = muzzle.GetComponent<AttackMuzzle>();
                                attMuzzle.transform.position = Quantizer.Dequantize(message.MuzzlePosition);
                                attMuzzle.transform.eulerAngles = Quantizer.Dequantize(message.MuzzleRotation);
                                attMuzzle.Set(1, WeaponUtility.GetBurstInterval(weapon));
                                attMuzzle.Play();

                                RandomSfx muzzleSfx = muzzle.GetComponent<RandomSfx>();
                                if (muzzleSfx != null)
                                {
                                    muzzleSfx.Play();
                                }
                            }
                            break;
                        case EWeapon.Sword:
                            var swordProjectileInstance = proj.GetComponent<ProjectileMelee>();
                            swordProjectileInstance.weaponBase = weapon;
                            swordProjectileInstance.weaponAttack = attack;
                            swordProjectileInstance.hitEnemies = new();
                            swordProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            DynamicData.For(swordProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(swordProjectileInstance).Set("ownerId", projectile.OwnerId);
                            break;
                        case EWeapon.Dexecutioner:
                            var dexecutionerProjectile = projectile as SpawnedDexecutionerProjectile;
                            var dexecutionerProjectileInstance = proj.GetComponent<ProjectileDexecutioner>();
                            dexecutionerProjectileInstance.attackDir = Quantizer.Dequantize(dexecutionerProjectile.AttackDir);
                            dexecutionerProjectileInstance.executionChance = dexecutionerProjectile.Chance;
                            dexecutionerProjectileInstance.forwardOffset = dexecutionerProjectile.ForwardOffset;
                            dexecutionerProjectileInstance.projectileDistance = dexecutionerProjectile.ProjectileDistance;
                            dexecutionerProjectileInstance.useAudio = dexecutionerProjectile.UseAudio;
                            dexecutionerProjectileInstance.weaponBase = weapon;
                            dexecutionerProjectileInstance.weaponAttack = attack;
                            dexecutionerProjectileInstance.hitEnemies = new();
                            dexecutionerProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            dexecutionerProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            DynamicData.For(dexecutionerProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(dexecutionerProjectileInstance).Set("ownerId", projectile.OwnerId);
                            break;
                        case EWeapon.Bananarang:
                            var bananarangProjectileInstance = proj.GetComponent<ProjectileBanana>();
                            bananarangProjectileInstance.weaponBase = weapon;
                            bananarangProjectileInstance.weaponAttack = attack;
                            bananarangProjectileInstance.hitEnemies = new();
                            bananarangProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            bananarangProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            bananarangProjectileInstance.rb.velocity = new Vector3(10, 10, 10); //Hack to avoid staying stuck at 0,0,0
                            DynamicData.For(bananarangProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(bananarangProjectileInstance).Set("ownerId", projectile.OwnerId);
                            break;
                        case EWeapon.Scythe:
                            var scytheProjectileInstance = proj.GetComponent<ProjectileScythe>();
                            scytheProjectileInstance.weaponBase = weapon;
                            scytheProjectileInstance.weaponAttack = attack;
                            scytheProjectileInstance.hitEnemies = new();
                            scytheProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            scytheProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            scytheProjectileInstance.expirationTime = 5f; //Hack to avoid staying stuck at 0,0,0
                            DynamicData.For(scytheProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(scytheProjectileInstance).Set("ownerId", projectile.OwnerId);
                            break;
                        case EWeapon.Revolver:
                            var messageRevolver = projectile as SpawnedRevolverProjectile;
                            var revolverProjectileInstance = proj.GetComponent<ProjectileBasic>();
                            revolverProjectileInstance.weaponBase = weapon;
                            revolverProjectileInstance.weaponAttack = attack;
                            revolverProjectileInstance.hitEnemies = new();
                            revolverProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            revolverProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            DynamicData.For(revolverProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(revolverProjectileInstance).Set("ownerId", projectile.OwnerId);

                            if (attack.prefabMuzzle != null)
                            {
                                var muzzle = GameObject.Instantiate(attack.prefabMuzzle);
                                AttackMuzzle attMuzzle = muzzle.GetComponent<AttackMuzzle>();
                                attMuzzle.transform.position = Quantizer.Dequantize(messageRevolver.MuzzlePosition);
                                attMuzzle.transform.eulerAngles = Quantizer.Dequantize(messageRevolver.MuzzleRotation);
                                attMuzzle.Set(1, WeaponUtility.GetBurstInterval(weapon));
                                attMuzzle.Play();

                                RandomSfx muzzleSfx = muzzle.GetComponent<RandomSfx>();
                                if (muzzleSfx != null)
                                {
                                    muzzleSfx.Play();
                                }
                            }
                            break;
                        case EWeapon.Sniper:
                            var messageSniper = projectile as SpawnedSniperProjectile;
                            var sniperProjectileInstance = proj.GetComponent<ProjectileSniper>();
                            sniperProjectileInstance.weaponBase = weapon;
                            sniperProjectileInstance.weaponAttack = attack;
                            sniperProjectileInstance.hitEnemies = new();
                            sniperProjectileInstance.transform.position = Quantizer.Dequantize(projectile.Position);
                            sniperProjectileInstance.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            DynamicData.For(sniperProjectileInstance).Set("netplayId", projectile.Id);
                            DynamicData.For(sniperProjectileInstance).Set("ownerId", projectile.OwnerId);
                            if (attack.prefabMuzzle != null)
                            {
                                var muzzle = GameObject.Instantiate(attack.prefabMuzzle);
                                AttackMuzzle attMuzzle = muzzle.GetComponent<AttackMuzzle>();
                                attMuzzle.transform.position = Quantizer.Dequantize(messageSniper.MuzzlePosition);
                                attMuzzle.transform.eulerAngles = Quantizer.Dequantize(messageSniper.MuzzleRotation);
                                attMuzzle.Set(1, WeaponUtility.GetBurstInterval(weapon));
                                attMuzzle.Play();

                                RandomSfx muzzleSfx = muzzle.GetComponent<RandomSfx>();
                                if (muzzleSfx != null)
                                {
                                    muzzleSfx.Play();
                                }
                            }
                            break;

                        default:
                            var projectileBase = proj.GetComponent<ProjectileBase>();
                            projectileBase.weaponBase = weapon;
                            projectileBase.weaponAttack = attack;
                            projectileBase.hitEnemies = new();

                            projectileBase.transform.position = Quantizer.Dequantize(projectile.Position);
                            projectileBase.transform.eulerAngles = Quantizer.Dequantize(projectile.Rotation);
                            DynamicData.For(projectileBase).Set("netplayId", projectile.Id);
                            DynamicData.For(projectileBase).Set("ownerId", projectile.OwnerId);
                            break;
                    }

                    if (attack.prefabMuzzle == null)
                    {
                        RandomSfx sfx = proj.GetComponentInChildren<RandomSfx>();
                        if (sfx != null)
                        {
                            sfx.Play();
                        }
                    }


                    Patches.Projectiles.ProjectileBasePatches.MarkReceived(proj.GetComponent<ProjectileBase>());

                    var mine = proj.GetComponent<ProjectileMines>();
                    if (mine != null)
                    {
                        Patches.Projectiles.ProjectileMinesPatches.InitReceived(mine, weapon);
                    }

                    // FIX P2-5: owner registered so these are dropped when that peer leaves.
                    projectileManagerService.RegisterProjectileForInterpolation(projectile.Id, proj, projectile.OwnerId);

                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in OnReceivedSpawnedProjectile: {ex}");
            }
        }


        public void OnSelectedCharacter()
        {
            ECharacter character = CharacterMenu.selectedCharacter;

            var localPlayer = playerManagerService.GetLocalPlayer();
            localPlayer.Character = (uint)character;
            playerManagerService.UpdatePlayer(localPlayer);

            var isHost = IsServerMode() ?? false;

            IGameNetworkMessage message = new SelectedCharacter
            {
                ConnectionId = localPlayer.ConnectionId,
                Skin = localPlayer.Skin,
                Character = (uint)character
            };

            if (!isHost)
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                playerManagerService.OnSelectedCharacterSet();
                udpClientService.SendToAllClients(message, NetDelivery.ReliableUnordered);
            }
        }

        private void OnReceivedSelectedCharacter(SelectedCharacter character)
        {
            logger.LogInfo(
                $"[netplayer] {character.ConnectionId} selected character {character.Character}, " +
                $"skin '{character.Skin}'.");

            var localPlayer = playerManagerService.GetLocalPlayer();
            if (localPlayer.ConnectionId == character.ConnectionId)
            {
                return;
            }

            var player = playerManagerService.GetPlayer(character.ConnectionId);
            if (player == null)
            {
                logger.LogWarning($"Player not found for ConnectionId: {character.ConnectionId}");
                return;
            }

            player.Character = character.Character;
            player.Skin = character.Skin;
            playerManagerService.UpdatePlayer(player);
        }

        private void OnReceivedEnemiesUpdate(IEnumerable<EnemyModel> enemiesModel)
        {
            foreach (var enemyModel in enemiesModel)
            {
                var enemy = enemyManagerService.GetEnemyById(enemyModel.Id);
                if (enemy == null)
                {
                    continue;
                }

                var interpolator = enemy.GetComponent<EnemyInterpolator>();
                if (interpolator == null)
                {
                    continue;
                }

                var snapshot = enemyModel.ToSnapshot(Time.timeAsDouble);

                interpolator.AddSnapshot(snapshot);
            }
        }

        public void OnEnemyDamaged(Enemy instance, DamageContainer damageContainer)
        {
            var enemySpawned = enemyManagerService.GetEnemyByReference(instance);
            if (enemySpawned.Value == null)
            {
                return; //Might already be dead
            }

            // OB-14: the hit's real attacker (from the tracker), not the forwarding host.
            var attackerId = trackerService.GetCurrentPlayerId() ?? playerManagerService.GetLocalPlayer().ConnectionId;
            var damageSource = DamageSourceHelper.Normalize(damageContainer.damageSource);

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // OB-34: summed per enemy tick; flushed by FlushEnemyDamage and OnEnemyDied.
                enemyDamageAggregator.Add(
                    enemySpawned.Key,
                    attackerId,
                    damageSource,
                    damageContainer.damage,
                    (int)damageContainer.damageEffect,
                    damageContainer.damageBlockedByArmor,
                    damageContainer.procCoefficient,
                    (int)damageContainer.element,
                    (int)damageContainer.flags,
                    damageContainer.knockback,
                    damageContainer.crit);
                return;
            }

            IGameNetworkMessage message = new EnemyDamaged
            {
                EnemyId = enemySpawned.Key,
                Damage = damageContainer.damage,
                DamageEffect = (int)damageContainer.damageEffect,
                DamageBlockedByArmor = damageContainer.damageBlockedByArmor,
                DamageSource = damageSource,
                DamageIsCrit = damageContainer.crit,
                DamageProcCoefficient = damageContainer.procCoefficient,
                DamageElement = (int)damageContainer.element,
                DamageFlags = (int)damageContainer.flags,
                DamageKnockback = damageContainer.knockback,
                AttackerId = attackerId
            };

            udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
        }

        public void FlushEnemyDamage()
        {
            enemyDamageAggregator.Flush(sendEnemyDamagedToClients);
        }

        /// <summary>
        /// Serialized here to check the size: the aggregator's estimate keeps batches under the cap, and a
        /// miss is promoted rather than sent unreliable over the MTU, where it would be lost.
        /// </summary>
        private void SendEnemyDamagedBatch(EnemyDamagedBatch batch)
        {
            var payload = MemoryPackSerializer.Serialize<IGameNetworkMessage>(batch);
            var delivery = payload.Length >= Tunables.MAX_PACKET_SIZE_BYTES
                ? NetDelivery.ReliableOrdered
                : NetDelivery.Unreliable;

            udpClientService.SendToAllClients(payload, delivery, nameof(EnemyDamagedBatch));
        }

        /// <summary>Client side. Each entry replays exactly like one <see cref="EnemyDamaged"/>.</summary>
        private void OnReceivedEnemyDamagedBatch(EnemyDamagedBatch batch)
        {
            if (!batch.IsWellFormed())
            {
                logger.LogWarning($"Dropped a malformed EnemyDamagedBatch ({batch.EnemyIds?.Count ?? 0} entries).");
                return;
            }

            for (var i = 0; i < batch.EnemyIds.Count; i++)
            {
                ApplyReceivedEnemyDamage(
                    batch.EnemyIds[i],
                    batch.AttackerIds[batch.AttackerIndices[i]],
                    batch.DamageSources[batch.SourceIndices[i]],
                    batch.Damages[i],
                    batch.DamageEffects[i],
                    batch.DamagesBlockedByArmor[i],
                    batch.ProcCoefficients[i],
                    batch.DamageElements[i],
                    batch.DamageFlags[i],
                    batch.Knockbacks[i],
                    batch.DamageIsCrits[i]);
            }
        }

        private void OnReceivedEnemyDamaged(EnemyDamaged damaged)
        {
            ApplyReceivedEnemyDamage(
                damaged.EnemyId,
                damaged.AttackerId,
                damaged.DamageSource,
                damaged.Damage,
                damaged.DamageEffect,
                damaged.DamageBlockedByArmor,
                damaged.DamageProcCoefficient,
                damaged.DamageElement,
                damaged.DamageFlags,
                damaged.DamageKnockback,
                damaged.DamageIsCrit);
        }

        private void ApplyReceivedEnemyDamage(
            uint enemyId,
            uint attackerId,
            string rawDamageSource,
            float damage,
            int damageEffect,
            int damageBlockedByArmor,
            float damageProcCoefficient,
            int damageElement,
            int damageFlags,
            float damageKnockback,
            bool damageIsCrit)
        {
            var enemy = enemyManagerService.GetEnemyById(enemyId);
            if (enemy == null)
            {
                return; //Might already be dead
            }

            // A null source makes RunStats.OnEnemyDamaged throw inside enemy.Damage.
            var damageSource = DamageSourceHelper.Normalize(rawDamageSource);

            var damageContainer = new DamageContainer(damageProcCoefficient, damageSource)
            {
                damage = damage,
                damageEffect = (EDamageEffect)damageEffect,
                damageBlockedByArmor = damageBlockedByArmor,
                crit = damageIsCrit,
                element = (EElement)damageElement,
                flags = (DcFlags)damageFlags,
                knockback = damageKnockback,
                damageSource = damageSource
            };

            // All three globals are restored in finally (P1-10, P0-6 shape). A latched
            // CAN_DAMAGE_ENEMIES would let this client resolve enemy damage locally for the run.
            Plugin.Instance.CAN_DAMAGE_ENEMIES = true;
            playerManagerService.AddGetNetplayerPositionRequest(attackerId);
            trackerService.SetCurrentPlayerId(attackerId);

            try
            {
                enemy.Damage(damageContainer);

                // OB-42: host procs the sender's on-hit items once (clients no longer do, OB-37);
                // HitItemOwnership redirects PostDamage to the tracked sender.
                if ((IsServerMode() ?? false) && damageContainer.procCoefficient > 0f)
                {
                    damageContainer.enemy = enemy;
                    GameManager.Instance?.player?.inventory?.itemInventory?.PostDamage(damageContainer);
                }
            }
            finally
            {
                trackerService.UnsetCurrentPlayerId();
                playerManagerService.UnqueueNetplayerPositionRequest();
                Plugin.Instance.CAN_DAMAGE_ENEMIES = false;
            }
        }


        public void OnEnemyDied(Enemy enemy, DamageContainer dc = null, uint? diedByOwnerId = null)
        {
            var enemySpawned = enemyManagerService.GetEnemyByReference(enemy);
            if (enemySpawned.Value == null)
            {
                logger.LogWarning("Enemy not found in EnemyManagerService when processing OnEnemyDied.");
                return;
            }

            enemyManagerService.RemoveEnemyById(enemySpawned.Key);

            var procCoefficient = dc != null ? dc.procCoefficient : 0f;

            IGameNetworkMessage message = new EnemyDied
            {
                EnemyId = enemySpawned.Key,
                DiedByOwnerId = diedByOwnerId ?? playerManagerService.GetLocalPlayer().ConnectionId,
                DamageProcCoefficient = procCoefficient,
                DamageSource = dc != null ? DamageSourceHelper.Normalize(dc.damageSource) : string.Empty
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // Flush pending hits first; clients drop hits for an already removed enemy.
                enemyDamageAggregator.FlushIfPending(enemySpawned.Key, sendEnemyDamagedToClients);
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                //Manually invoke boss defeated event client side. OB-44: only once no stage boss is left.
                if (enemy.IsStageBoss() && !enemy.IsFinalBoss() && !IsAnotherStageBossAlive(enemySpawned.Key))
                {
                    OnBossDefeated();
                }

                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedEnemyDied(EnemyDied died)
        {
            // OB-51 diagnostic: did a lingering enemy's death ever arrive?
            DesyncDetectorService.NoteEnemyDiedReceived(died.EnemyId);

            var enemy = enemyManagerService.GetEnemyById(died.EnemyId);
            if (enemy == null)
            {
                //don't spawn a ghost :/
                enemiesDiedBeforeSpawn.Add(died.EnemyId);
                return;
            }

            var damageContainer = new DamageContainer(0.0f, DamageSourceHelper.Normalize(died.DamageSource))
            {
                damage = enemy.hp + 1,
                enemy = enemy
            }; //TODO track dmgContainer ?

            damageContainer.procCoefficient = died.DamageProcCoefficient;

            using (Plugin.SuppressOutbound())
            {
                // try/finally: the death sequence can throw, which would latch the tracker (OB-14).
                trackerService.SetCurrentPlayerId(died.DiedByOwnerId);

                try
                {
                    enemy.EnemyDied(damageContainer);
                }
                finally
                {
                    trackerService.UnsetCurrentPlayerId();
                }
            }

            var isHost = IsServerMode() ?? false;
            if (!isHost)
            {
                // OB-37: no client-side on-hit procs here; the host runs them (HitItemOwnership).

                //Manually invoke boss defeated event client side. OB-44: only once no stage boss is left.
                if (enemy.IsStageBoss() && !enemy.IsFinalBoss() && !IsAnotherStageBossAlive(died.EnemyId))
                {
                    OnBossDefeated();
                }
            }

            if (enemy.enemyData.enemyName != EEnemy.BoomerSpider) //Will be removed when exploding
            {
                enemyManagerService.RemoveEnemyById(died.EnemyId);
            }
        }

        /// <summary>
        /// OB-44. The game opens the portal only when every boss its spawner spawned is gone
        /// (decompiled: <c>InteractableBossSpawner.OnEnemyReleasedFromPool</c>, VA 0x1804C5B80).
        /// </summary>
        private bool IsAnotherStageBossAlive(uint dyingEnemyId)
        {
            foreach (var kv in enemyManagerService.GetAllRegisteredEnemies())
            {
                if (kv.Key == dyingEnemyId)
                {
                    continue;
                }

                var other = kv.Value;
                if (other != null && other.IsStageBoss() && !other.IsFinalBoss() && !other.IsDead())
                {
                    logger.LogInfo($"[boss] OB-44: a stage boss died but another (id {kv.Key}) is still alive; the portal waits for it.");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Client-side boss-defeated event. Idempotent per stage: a client that kills the boss
        /// reaches this from its own OnEnemyDied and again from the host's broadcast. Reset per stage and session (SE-2).
        /// </summary>
        private void OnBossDefeated()
        {
            if (hasHandledBossDefeatedThisStage)
            {
                return;
            }
            hasHandledBossDefeatedThisStage = true;

            var cam = GameManager.Instance.player.minimapCamera.GetComponent<MinimapCamera>();
            cam.arrowDict.Clear(); //Prevent sometimes double add for portal arrow

            // OB-46: the game's rule (decompiled CanSpawnPortal, VA 0x1804C5540). The tier's last
            // stage opens no portal and raises A_BossDefeated(false).
            var bossSpawner = spawnedObjectManagerService.GetSpecific<InteractableBossSpawner>();
            var canSpawnPortal = bossSpawner == null || bossSpawner.CanSpawnPortal();
            logger.LogInfo(canSpawnPortal
                ? "Boss defeated, activating portal."
                : "Boss defeated; the tier's last stage has no portal (OB-46).");

            if (bossSpawner != null && canSpawnPortal)
            {
                bossSpawner.portal.SetActive(true);
            }
            InteractableBossSpawner.A_BossDefeated?.Invoke(canSpawnPortal);
        }

        public void OnProjectileDone(ProjectileBase instance)
        {
            var projectileSpawned = projectileManagerService.GetProjectileByReference(instance);
            if (projectileSpawned.Value == null)
            {
                return;
            }

            projectileManagerService.RemoveProjectileById(projectileSpawned.Key);

            IGameNetworkMessage message = new ProjectileDone
            {
                ProjectileId = projectileSpawned.Key,
                SenderConnectionId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedProjectileDone(ProjectileDone done)
        {
            projectileManagerService.UnregisterProjectileFromInterpolation(done.ProjectileId);
        }

        public void OnPickupSpawned(Pickup pickup, EPickup ePickup, Vector3 pos, int value)
        {
            if (IsServerMode() == false)
            {
                return;
            }

            if (ePickup == EPickup.Xp && !IsSharedExperienceEnabled())
            {
                pickup.value = gameBalanceService.GetPickupXpValue();
            }

            pickup.readyForPickupTime += 2.0f; //Attempt to compensate for network delay ?

            var netplayId = pickupManagerService.AddSpawnedPickup(pickup);

            DynamicData.For(pickup).Set("pickupId", netplayId);

            IGameNetworkMessage message = new SpawnedPickup
            {
                Id = netplayId,
                Pickup = (int)ePickup,
                Position = pos.ToNumericsVector3(),
                Value = value
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedSpawnedPickup(SpawnedPickup pickup)
        {
            // FIX (P1-10, P0-6): restored in finally so a throw cannot latch the flag.
            Plugin.CAN_SPAWN_PICKUPS = true;
            Pickup spawnedPickup;
            try
            {
                spawnedPickup = PickupManager.Instance.SpawnPickup((EPickup)pickup.Pickup, pickup.Position.ToUnityVector3(), pickup.Value, false);
            }
            finally
            {
                Plugin.CAN_SPAWN_PICKUPS = false;
            }

            //if (spawnedPickup.ePickup == EPickup.Xp && !IsSharedExperienceEnabled())
            //{
            //    spawnedPickup.value = gameBalanceService.GetPickupXpValue();
            //}


            var dynP = DynamicData.For(spawnedPickup);
            dynP.Data.Clear();

            pickupManagerService.SetSpawnedPickup(pickup.Id, spawnedPickup);
            dynP.Set("pickupId", pickup.Id);
        }

        public void OnPickupOrbSpawned(EPickup ePickup, Vector3 pos)
        {
            IGameNetworkMessage message = new SpawnedPickupOrb
            {
                Pickup = (int)ePickup,
                Position = pos.ToNumericsVector3(),
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedSpawnedOrbPickup(SpawnedPickupOrb pickup)
        {
            // FIX (P1-10, P0-6): restored in finally so a throw cannot latch the flag.
            Plugin.CAN_SPAWN_PICKUPS = true;
            try
            {
                EffectManager.Instance.SpawnPickupOrb((EPickup)pickup.Pickup, pickup.Position.ToUnityVector3());
            }
            finally
            {
                Plugin.CAN_SPAWN_PICKUPS = false;
            }
        }

        public void OnPickupApplied(Pickup instance)
        {
            (var pickupId, var pickupSpawned) = pickupManagerService.GetPickupByReference(instance);
            if (pickupSpawned == null)
            {
                PickupLifetimeDiagnostics.RecordMiss("OnPickupApplied", pickupId);
                DynamicData.For(instance).Data.Clear();
                PickupManager.Instance.DespawnPickup(instance);

                return;
            }

            pickupManagerService.RemoveSpawnedPickupById(pickupId);
            DynamicData.For(instance).Data.Clear();

            IGameNetworkMessage message = new PickupApplied
            {
                PickupId = pickupId,
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedPickupApplied(PickupApplied applied)
        {
            var pickup = pickupManagerService.GetSpawnedPickupById(applied.PickupId);
            if (pickup == null)
            {
                PickupLifetimeDiagnostics.RecordMiss("OnReceivedPickupApplied", applied.PickupId);
                return;
            }

            if (pickup.ePickup == EPickup.Time || pickup.ePickup == EPickup.Magnet) //Apply for all clients
            {
                using (Plugin.SuppressOutbound())
                {
                    pickup.ApplyPickup();
                }
            }
            //else if (pickup.ePickup == EPickup.Xp && IsSharedExperienceEnabled()) //Apply xp pickup for all clients if shared xp enabled
            //{
            //    Plugin.CAN_SEND_MESSAGES = false;
            //    pickup.ApplyPickup();
            //    Plugin.CAN_SEND_MESSAGES = true;

            //    logger.LogInfo($"received Current player XP : {MyPlayer.Instance.inventory.playerXp.GetXpInt()} , Pending XP : {MyPlayer.Instance.inventory.pendingXp}");
            //}
            else
            {
                var isServer = IsServerMode() ?? false;
                var netPlayer = playerManagerService.GetNetPlayerByNetplayId(applied.OwnerId);

                if (isServer && pickup.ePickup == EPickup.Rage) //Apply rage on server since projectiles are spawned server side
                {
                    playerManagerService.AddGetNetplayerPositionRequest(applied.OwnerId);
                    netPlayer.Inventory.statusEffects.OnPickupTriggered(pickup);
                    playerManagerService.UnqueueNetplayerPositionRequest();
                }

                try
                {
                    playerManagerService.AddGetNetplayerPositionRequest(applied.OwnerId);
                    switch (pickup.ePickup)
                    {
                        case EPickup.Rage:
                            var rage = EffectManager.Instance.ragePickup;
                            var copiedPickup = GameObject.Instantiate(rage).GetComponent<StatusEffectPickup>();
                            copiedPickup.useFeetPosition = false;
                            copiedPickup.Set();
                            break;
                        case EPickup.Shield:
                            var shield = EffectManager.Instance.shieldPickup;
                            var copiedShieldPickup = GameObject.Instantiate(shield).GetComponent<StatusEffectPickup>();
                            copiedShieldPickup.useFeetPosition = false;
                            copiedShieldPickup.Set();
                            var y = copiedShieldPickup.transform.position.y;
                            y += Plugin.PLAYER_FEET_OFFSET_Y;
                            copiedShieldPickup.transform.position = new Vector3(copiedShieldPickup.transform.position.x, y, copiedShieldPickup.transform.position.z);
                            break;
                        case EPickup.Haste:
                            var haste = EffectManager.Instance.hastePickup;
                            var copiedHastePickup = GameObject.Instantiate(haste).GetComponent<StatusEffectPickup>();
                            copiedHastePickup.useFeetPosition = false;
                            copiedHastePickup.Set();
                            break;
                        case EPickup.Nuke:
                            var nuke = EffectManager.Instance.nukePickup;
                            var explosion = GameObject.Instantiate(nuke).GetComponent<Explosion>();
                            explosion.transform.position = netPlayer.Model.transform.position;
                            break;
                        default:
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error applying pickup {pickup.ePickup} for player {applied.OwnerId}: {ex}");
                }
                finally
                {
                    playerManagerService.UnqueueNetplayerPositionRequest();
                }
            }

            DynamicData.For(pickup).Data.Clear();

            PickupManager.Instance.DespawnPickup(pickup);
            pickupManagerService.RemoveSpawnedPickupById(applied.PickupId);
        }

        private void OnReceivedPickupFollowingPlayer(PickupFollowingPlayer player)
        {
            var pickup = pickupManagerService.GetSpawnedPickupById(player.PickupId);
            if (pickup == null)
            {
                PickupLifetimeDiagnostics.RecordMiss("OnReceivedPickupFollowingPlayer", player.PickupId);
                return;
            }

            Transform target;
            var netPlayer = playerManagerService.GetNetPlayerByNetplayId(player.PlayerId);
            if (netPlayer == null)
            {
                if (player.PlayerId == playerManagerService.GetLocalPlayer().ConnectionId)
                {
                    target = GameManager.Instance.player.transform;
                }
                else
                {
                    logger.LogWarning("NetPlayer not found in PlayerManager when processing OnReceivedPickupFollowingPlayer.");
                    return;
                }
            }
            else
            {
                target = netPlayer.Model.transform;
            }

            var dynPickup = DynamicData.For(pickup);
            dynPickup.Set("ownerId", player.PlayerId);

            using (Plugin.SuppressOutbound())
            {
                pickup.StartFollowingPlayer(target);
            }
        }

        public void OnWantToStartFollowingPickup(Pickup instance)
        {
            var isServer = IsServerMode() ?? false;

            if (!isServer)
            {
                DynamicData.For(instance).Set("hasSentAlready", true);

                IGameNetworkMessage message = new WantToStartFollowingPickup
                {
                    PickupId = pickupManagerService.GetPickupByReference(instance).Key,
                    OwnerId = playerManagerService.GetLocalPlayer().ConnectionId
                };

                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                HandleWantToStartFollowingPickup(playerManagerService.GetLocalPlayer().ConnectionId, pickupManagerService.GetPickupByReference(instance).Key);
            }
        }

        private void HandleWantToStartFollowingPickup(uint ownerId, uint pickupId)
        {
            var pickup = pickupManagerService.GetSpawnedPickupById(pickupId);
            if (pickup == null)
            {
                PickupLifetimeDiagnostics.RecordMiss("HandleWantToStartFollowingPickup", pickupId);
                return;
            }

            var currentOwnerId = DynamicData.For(pickup).Get<uint?>("ownerId");
            if (currentOwnerId.HasValue)
            {
                if (currentOwnerId.Value != ownerId)
                {
                    IGameNetworkMessage msg = new PickupFollowingPlayer
                    {
                        PickupId = pickupId,
                        PlayerId = currentOwnerId.Value
                    };
                    udpClientService.SendToAllClients(msg, NetDelivery.ReliableOrdered);
                }

                return;
            }

            DynamicData.For(pickup).Set("ownerId", ownerId);

            IGameNetworkMessage message = new PickupFollowingPlayer
            {
                PickupId = pickupId,
                PlayerId = ownerId
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            OnReceivedPickupFollowingPlayer((PickupFollowingPlayer)message);
        }

        private void OnReceivedWantToStartFollowingPickup(WantToStartFollowingPickup pickupMessage)
        {
            HandleWantToStartFollowingPickup(pickupMessage.OwnerId, pickupMessage.PickupId);
        }

        public void SendPickupFollowingPlayer(uint ownerId, uint pickupId)
        {
            IGameNetworkMessage message = new PickupFollowingPlayer
            {
                PickupId = pickupId,
                PlayerId = ownerId
            };
            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        public void OnSpawnedChest(Vector3 position, Quaternion rotation, UnityEngine.Object obj)
        {
            var chestId = chestManagerService.AddChest(obj);

            IGameNetworkMessage message = new SpawnedChest
            {
                Position = position.ToNumericsVector3(),
                Rotation = rotation.ToNumericsQuaternion(),
                ChestId = chestId
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableUnordered);
        }

        private void OnReceivedSpawnedChest(SpawnedChest chest)
        {
            chestManagerService.PushNextChestId(chest.ChestId);
            // FIX (P1-10, P0-6): restored in finally so a throw cannot latch the flag.
            Plugin.CAN_SPAWN_CHESTS = true;
            try
            {
                EffectManager.Instance.SpawnChest(EffectManager.Instance.openChestNormal, chest.Position.ToUnityVector3());
            }
            finally
            {
                Plugin.CAN_SPAWN_CHESTS = false;
            }
        }

        public void OnChestOpened(OpenChest instance)
        {
            var chestSpawned = chestManagerService.GetChestByReference(instance);

            if (chestSpawned.Value == null)
            {
                logger.LogWarning("Chest not found in ChestManagerService when processing OnChestOpened.");
                return;
            }
            IGameNetworkMessage message = new ChestOpened
            {
                ChestId = chestSpawned.Key,
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId
            };
            var isHost = IsServerMode() ?? false;

            if (isHost)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedChestOpened(ChestOpened opened)
        {
            var chestObject = chestManagerService.GetChest(opened.ChestId);
            if (chestObject == null)
            {
                logger.LogWarning("Chest object not found in ChestManagerService when processing OnReceivedChestOpened.");
                return;
            }

            if (IsSharedExperienceEnabled())
            {
                UiManager.Instance.encounterWindows.AddEncounter(Assets.Scripts.UI.InGame.Rewards.EEncounter.ChestNormal);
            }

            GameObject.DestroyImmediate(chestObject);
            chestManagerService.RemoveChest(opened.ChestId);
        }

        public void OnWeaponAdded(WeaponInventory instance, WeaponData weaponData, Il2CppSystem.Collections.Generic.List<StatModifier> upgradeOffer)
        {
            var upgrades = new List<StatModifierModel>();

            if (upgradeOffer != null)
            {
                foreach (var modifier in upgradeOffer)
                {
                    upgrades.Add(new StatModifierModel
                    {
                        StatType = (int)modifier.stat,
                        Value = modifier.modification,
                        ModificationType = (int)modifier.modifyType
                    });
                }
            }

            IGameNetworkMessage msg = new WeaponAdded
            {
                Weapon = (int)weaponData.eWeapon,
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId,
                Upgrades = upgrades
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                udpClientService.SendToAllClients(msg, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(msg, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedWeaponAdded(WeaponAdded added)
        {
            var player = playerManagerService.GetNetPlayerByNetplayId(added.OwnerId);
            if (player == null)
            {
                logger.LogWarning($"Player not found for ConnectionId: {added.OwnerId} when processing OnReceivedWeaponAdded.");
                return;
            }

            if (DataManager.Instance.weapons.TryGetValue((EWeapon)added.Weapon, out var weaponData))
            {
                var upgradeModifiers = new Il2CppSystem.Collections.Generic.List<StatModifier>();
                foreach (var upgrade in added.Upgrades)
                {
                    var modifier = new StatModifier
                    {
                        stat = (EStat)upgrade.StatType,
                        modification = upgrade.Value,
                        modifyType = (EStatModifyType)upgrade.ModificationType
                    };
                    upgradeModifiers.Add(modifier);
                }

                // try/finally (P0-6 shape): a throw must not leave messages off and game statics nulled.
                Plugin.CAN_SEND_MESSAGES = false;
                Plugin.Instance.SavePlayerInventoryActions();
                try
                {
                    player.Inventory.weaponInventory.AddWeapon(weaponData, upgradeModifiers);
                    player.RefreshConstantAttack(upgradeModifiers);
                }
                finally
                {
                    Plugin.Instance.RestorePlayerInventoryActions();
                    Plugin.CAN_SEND_MESSAGES = true;
                }
            }
        }

        public void OnTomeAdded(TomeInventory instance, TomeData tomeData, Il2CppSystem.Collections.Generic.List<StatModifier> upgradeOffer, ERarity rarity)
        {
            var upgrades = new List<StatModifierModel>();
            foreach (var modifier in upgradeOffer)
            {
                upgrades.Add(new StatModifierModel
                {
                    StatType = (int)modifier.stat,
                    Value = modifier.modification,
                    ModificationType = (int)modifier.modifyType
                });
            }

            if (tomeData.eTome == ETome.Xp)
            {
                var xpMult = GameManager.Instance.player.inventory.playerStats.GetStat(EStat.XpIncreaseMultiplier);
                logger.LogInfo($"Adding tome {tomeData.eTome} , current player XP : {xpMult}");
            }

            IGameNetworkMessage msg = new TomeAdded
            {
                Tome = (int)tomeData.eTome,
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId,
                Upgrades = upgrades,
                Rarity = (int)rarity
            };
            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                udpClientService.SendToAllClients(msg, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(msg, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedTomeAdded(TomeAdded added)
        {
            var player = playerManagerService.GetNetPlayerByNetplayId(added.OwnerId);
            if (player == null)
            {
                logger.LogWarning($"Player not found for ConnectionId: {added.OwnerId} when processing OnReceivedTomeAdded.");
                return;
            }

            if (DataManager.Instance.tomeData.TryGetValue((ETome)added.Tome, out var tomeData))
            {
                var upgradeModifiers = new Il2CppSystem.Collections.Generic.List<StatModifier>();
                foreach (var upgrade in added.Upgrades)
                {
                    var modifier = new StatModifier
                    {
                        stat = (EStat)upgrade.StatType,
                        modification = upgrade.Value,
                        modifyType = (EStatModifyType)upgrade.ModificationType
                    };
                    upgradeModifiers.Add(modifier);
                }
                using (Plugin.SuppressOutbound())
                {
                    // P1-9 shape: restore the nulled game static in finally, or tome upgrades die for the process.
                    var callbacks = TomeInventory.A_TomeUpgrade;
                    TomeInventory.A_TomeUpgrade = null;
                    try
                    {
                        player.Inventory.tomeInventory.AddTome(tomeData, upgradeModifiers, (ERarity)added.Rarity);
                        player.RefreshConstantAttack(upgradeModifiers);
                    }
                    finally
                    {
                        TomeInventory.A_TomeUpgrade = callbacks;
                    }
                }
            }
        }

        public void OnInteractableUsed(BaseInteractable instance)
        {
            var netplayId = DynamicData.For(instance.gameObject).Get<uint?>("netplayId");

            if (!netplayId.HasValue)
            {
                try
                {
                    var id = spawnedObjectManagerService.GetByReferenceInChildren<InteractableCharacterFight>(instance.gameObject);
                    if (id.HasValue)
                    {
                        netplayId = id;
                    }

                    var boomBox = spawnedObjectManagerService.GetByReferenceInChildren<InteractableBoombox>(instance.gameObject);
                    if (boomBox.HasValue)
                    {
                        netplayId = boomBox;
                    }

                    var coffin = spawnedObjectManagerService.GetByReferenceInChildren<InteractableCoffin>(instance.gameObject);
                    if (coffin.HasValue)
                    {
                        netplayId = coffin;
                    }

                    var present = spawnedObjectManagerService.GetByReferenceInChildren<InteractableGift>(instance.gameObject);
                    if (present.HasValue)
                    {
                        netplayId = present;
                    }

                    var crypt = spawnedObjectManagerService.GetByReferenceInChildren<InteractableCrypt>(instance.gameObject);
                    if (crypt.HasValue)
                    {
                        netplayId = crypt;
                    }

                    var tumbleWeed = spawnedObjectManagerService.GetByReferenceInChildren<InteractableTumbleWeed>(instance.gameObject);
                    if (tumbleWeed.HasValue)
                    {
                        netplayId = tumbleWeed;
                    }

                    var reviver = spawnedObjectManagerService.GetByReferenceInChildren<InteractableReviver>(instance.gameObject);
                    if (reviver.HasValue)
                    {
                        netplayId = reviver;
                    }

                    var egg = spawnedObjectManagerService.GetByReferenceInChildren<InteractableEgg>(instance.gameObject);
                    if (egg.HasValue)
                    {
                        netplayId = egg;
                    }

                    var shadyGuy = spawnedObjectManagerService.GetByReferenceInChildren<InteractableShadyGuy>(instance.gameObject);
                    if (shadyGuy.HasValue)
                    {
                        netplayId = shadyGuy;
                    }

                    var moai = spawnedObjectManagerService.GetByReferenceInChildren<InteractableShrineMoai>(instance.gameObject);
                    if (moai.HasValue)
                    {
                        netplayId = moai;
                    }

                    var microwave = spawnedObjectManagerService.GetByReferenceInChildren<InteractableMicrowave>(instance.gameObject);
                    if (microwave.HasValue)
                    {
                        netplayId = microwave;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Error while getting netplayId for InteractableCharacterFight: {ex}");
                }
            }

            var isPortal = instance.GetComponentInChildren<InteractablePortal>() != null;
            var isFinalPortal = instance.GetComponentInChildren<InteractablePortalFinal>() != null;
            var isCryptKey = instance.GetComponentInChildren<InteractableCageKey>() != null && instance.name.Contains("CryptKeyPickup");

            if (!isPortal && !isFinalPortal && !isCryptKey)
            {
                if (!netplayId.HasValue)
                {
                    if (!instance.name.Contains("ShadyGuy") && !instance.name.Contains("Microwave")) //TODO: those guys are so shady that they don't work on client side for some reason ¯\_(ツ)_/¯ (edit, they might work like interactable character fight ?)
                    {
                        logger.LogWarning("Interactable does not have a netplayId when processing OnInteractableUsed.");
                    }
                    return;
                }

                var interactable = spawnedObjectManagerService.GetSpawnedObject(netplayId.Value);

                if (interactable == null)
                {
                    logger.LogWarning("Interactable not found in SpawnedObjectManagerService when processing OnInteractableUsed.");
                    return;
                }
            }

            IGameNetworkMessage message = new InteractableUsed
            {
                NetplayId = netplayId.HasValue ? netplayId.Value : 0,
                Action = IsSharedExperienceEnabled() ? InteractableAction.Interact : GetActionByInteractable(instance),
                IsPortal = isPortal,
                IsFinalPortal = isFinalPortal,
                IsCryptKey = isCryptKey,
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId,
                IsMicrowaveAndHaveItem = instance.GetComponentInChildren<InteractableMicrowave>()?.hasItem ?? false
            };

            var isHost = IsServerMode() ?? false;

            if (instance.GetComponentInChildren<InteractableCoffin>() != null)
            {
                currentCoffin = instance.GetComponentInChildren<InteractableCoffin>();
            }

            if (isHost)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                if (instance.GetComponentInChildren<InteractableCoffin>() != null)
                {
                    currentCoffin.minibossEnemies = new Il2CppSystem.Collections.Generic.HashSet<Enemy>();
                }

                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }

            if (isPortal || instance.GetComponentInChildren<InteractableBossSpawnerFinal>() != null)
            {
                TransitionToState(GameEvent.PortalOpened);
            }

            if (isFinalPortal)
            {
                // Endless: a final boss's portal leads to the next room, not to the end of the run.
                TransitionToState(endlessRunService.NextRoomKnown ? GameEvent.PortalOpened : GameEvent.FinalPortalOpened);
            }
        }

        private static InteractableAction GetActionByInteractable(BaseInteractable interactable)
        {
            if (interactable == null) return InteractableAction.Used;

            if (interactable.GetComponentInChildren<InteractableCharacterFight>() != null)
            {
                return InteractableAction.Interact;
            }

            if (interactable.GetComponentInChildren<InteractableChest>() != null)
            {
                return InteractableAction.Destroy;
            }

            if (
                interactable.GetComponentInChildren<InteractableShrineCursed>() != null ||
                interactable.GetComponentInChildren<InteractableShrineGreed>() != null ||
                interactable.GetComponentInChildren<InteractableShrineChallenge>() != null ||
                interactable.GetComponentInChildren<InteractableShrineMagnet>() != null ||
                interactable.GetComponentInChildren<InteractableBossSpawner>() != null ||
                interactable.GetComponentInChildren<InteractablePortal>() != null ||
                interactable.GetComponentInChildren<InteractableBossSpawnerFinal>() != null ||
                interactable.GetComponentInChildren<InteractablePortalFinal>() != null ||
                interactable.GetComponentInChildren<InteractableTumbleWeed>() != null ||
                interactable.GetComponentInChildren<InteractablePot>() != null ||
                interactable.GetComponentInChildren<InteractableBoombox>() != null ||
                interactable.GetComponentInChildren<InteractableDesertGrave>() != null ||
                interactable.GetComponentInChildren<InteractableSkeletonKingStatue>() != null ||
                interactable.GetComponentInChildren<InteractableCryptLeave>() != null ||
                interactable.GetComponentInChildren<InteractableCoffin>() != null ||
                interactable.GetComponentInChildren<InteractableCageKey>() != null ||
                interactable.GetComponentInChildren<InteractableCrypt>() != null ||
                interactable.GetComponentInChildren<InteractableGhostBossLeave>() != null ||
                interactable.GetComponentInChildren<InteractableGift>() != null ||
                interactable.GetComponentInChildren<InteractableGravestone>() != null ||
                interactable.GetComponentInChildren<InteractableReviver>() != null ||
                interactable.GetComponentInChildren<InteractableEgg>() != null
            )
            {
                return InteractableAction.Interact;
            }

            return InteractableAction.Used;
        }

        /// <summary>
        /// OB-12. Counts a peer's interaction on our counter.
        /// <para>The try must stay one level above the helper: its interop call can throw
        /// <c>MissingMethodException</c> at JIT time of the containing method.</para>
        /// </summary>
        private void CountInteractableAsUsed(GameObject interactableObj)
        {
            try
            {
                Helpers.InteractableCounterHelper.CountAsUsed(interactableObj);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    $"[interactable-count] Could not count a replicated interaction: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>OB-12. Try placement as in <see cref="CountInteractableAsUsed"/>.</summary>
        private Helpers.InteractableCounterHelper.Snapshot SnapshotInteractableCounter(GameObject interactableObj)
        {
            try
            {
                return Helpers.InteractableCounterHelper.Take(interactableObj);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    $"[interactable-count] Could not read the counter before dispatch: {ex.GetType().Name}: {ex.Message}");
                return default;
            }
        }

        /// <summary>OB-12. Counts only if the dispatch did not — see the helper for why measured.</summary>
        private void CountInteractableIfDispatchDidNot(
            GameObject interactableObj, Helpers.InteractableCounterHelper.Snapshot before)
        {
            try
            {
                Helpers.InteractableCounterHelper.CountIfDispatchDidNot(interactableObj, before);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    $"[interactable-count] Could not count a replicated interaction: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnReceivedInteractableUsed(InteractableUsed used)
        {
            // SE-11 / OB-12: drop our own message echoed back (the relay's exclude filter can be
            // empty). An echo would double-count or replay the interaction (e.g. a second challenge wave).
            var localPlayerForEcho = playerManagerService.GetLocalPlayer();
            if (localPlayerForEcho != null && used.OwnerId == localPlayerForEcho.ConnectionId)
            {
                logger.LogDebug(
                    $"OB-12: dropping our own InteractableUsed coming back (owner {used.OwnerId}, " +
                    $"action {used.Action}).");
                return;
            }

            if (used.IsPortal)
            {
                var bossSpawner = spawnedObjectManagerService.GetSpecific<InteractableBossSpawner>();
                if (bossSpawner != null)
                {
                    if (!bossSpawner.portal.activeSelf || !bossSpawner.portal.activeInHierarchy)
                    {
                        bossSpawner.portal.SetActive(true); //We might not received boss death yet, so...
                    }

                    var portal = bossSpawner.portal.GetComponent<InteractablePortal>();
                    if (portal != null)
                    {
                        using (Plugin.SuppressOutbound())
                        {
                            portal.Interact();
                            TransitionToState(GameEvent.PortalOpened);
                        }
                    }
                }

                return;
            }

            if (used.IsFinalPortal)
            {
                var finalPortal = Il2CppFindHelper.FindAllGameObjects().FirstOrDefault(go => go.GetComponent<InteractablePortalFinal>() != null)?.GetComponent<InteractablePortalFinal>(); //Find the componnt somehow instead of whole gameobject search
                if (finalPortal != null)
                {
                    // Read before Interact: in Endless that already counts the stage change.
                    var endlessExit = endlessRunService.NextRoomKnown;
                    using (Plugin.SuppressOutbound())
                    {
                        finalPortal.Interact();
                        TransitionToState(endlessExit ? GameEvent.PortalOpened : GameEvent.FinalPortalOpened);
                    }

                    // Done: falling through looked up object 0 and, in shared experience, used to pause
                    // this peer behind the barrier on every final-boss exit (2026-10-22 logs).
                    return;
                }
                else
                {
                    logger.LogWarning("Final Portal not found ?");
                    return;
                }
            }

            if (used.IsCryptKey)
            {
                var key = currentCoffin.keyPickup.GetComponentInChildren<InteractableCageKey>();
                if (key != null)
                {
                    currentCoffin.OnInteracted(key, key.Interact());
                }
                else
                {
                    logger.LogWarning("Crypt Key not found ?");
                }
                return;
            }

            var interactableObj = spawnedObjectManagerService.GetSpawnedObject(used.NetplayId);

            // Not found: warn and skip. This used to pause and join the shared-experience barrier,
            // assuming a reward window on the sender. For a pot or crypt exit the sender opens none,
            // never reports, and this peer sat frozen until the 20 s failsafe (2026-10-22 playtest).
            if (interactableObj == null)
            {
                logger.LogWarning(
                    $"[interactable] {used.OwnerId}'s interaction with object {used.NetplayId} ({used.Action}) "
                    + $"has no match here ({spawnedObjectManagerService.GetAllSpawnedObjects().Count()} registered); skipped.");
                return;
            }

            // OB-12: snapshot the tally so the Interact branch can be counted whichever sub-path ran.
            var counterBefore = SnapshotInteractableCounter(interactableObj);

            using (Plugin.SuppressOutbound())
            {
                switch (used.Action)
                {
                    case InteractableAction.Destroy:
                        CountInteractableAsUsed(interactableObj);
                        GameObject.DestroyImmediate(interactableObj);
                        break;
                    case InteractableAction.Used:
                        CountInteractableAsUsed(interactableObj);
                        logger.LogInfo($"Net player used interactable with ID: {used.NetplayId}");
                        break;
                    case InteractableAction.Interact:
                        var microwave = interactableObj.GetComponentInChildren<InteractableMicrowave>();
                        if (microwave != null)
                        {

                            if (used.IsMicrowaveAndHaveItem && !microwave.hasItem)
                            {
                                break;
                            }

                            if (microwave.hasItem && !used.IsMicrowaveAndHaveItem)
                            {
                                microwave.Interact();
                                MyTime.Pause();
                                RewardFinished();
                                ScreenTextHelper.Show("Waiting for other player(s) choices in Microwave...", new Vector2(0, -350));
                                break;
                            }

                            // Opt out where Interact() would refuse, so this peer still releases the
                            // barrier. CanInteract() does not check gold (CONFIRMED, see MicrowaveHelper).
                            if (!microwave.hasItem && (GameManager.Instance.player.IsDead()
                                                       || !microwave.CanInteract()
                                                       || !MicrowaveHelper.CanStartCooking(microwave)))
                            {
                                MyTime.Pause();
                                RewardFinished();
                                ScreenTextHelper.Show("Waiting for other player(s) choices in Microwave...", new Vector2(0, -350));
                            }
                            else
                            {
                                microwave.Interact();
                            }
                            break;
                        }

                        var shrineBalance = interactableObj.GetComponentInChildren<InteractableShrineBalance>();
                        if (shrineBalance != null)
                        {
                            if (GameManager.Instance.player.IsDead())
                            {
                                MyTime.Pause();
                                RewardFinished();
                                ScreenTextHelper.Show("Waiting for other player(s) choices in Balance Shrine...", new Vector2(0, -350));
                            }
                            else
                            {
                                shrineBalance.Interact();
                            }
                            break;
                        }

                        var moai = interactableObj.GetComponentInChildren<InteractableShrineMoai>();
                        if (moai != null)
                        {
                            if (GameManager.Instance.player.IsDead())
                            {
                                MyTime.Pause();
                                RewardFinished();
                                ScreenTextHelper.Show("Waiting for other player(s) choices in Moai Shrine...", new Vector2(0, -350));
                            }
                            else
                            {
                                moai.Interact();
                            }
                            break;
                        }

                        var chest = interactableObj.GetComponent<InteractableChest>();
                        if (chest != null)
                        {
                            if (GameManager.Instance.player.IsDead() || !chest.CanAfford())
                            {
                                MyTime.Pause();
                                RewardFinished();
                                ScreenTextHelper.Show("Waiting for other player(s) choices in Chest...", new Vector2(0, -350));
                            }
                            else
                            {
                                chest.Interact();
                            }
                            break;
                        }

                        var shadyGuy = interactableObj.GetComponentInChildren<InteractableShadyGuy>();
                        if (shadyGuy != null)
                        {
                            if (GameManager.Instance.player.IsDead())
                            {
                                MyTime.Pause();
                                RewardFinished();
                                ScreenTextHelper.Show("Waiting for other player(s) choices with Shady Guy...", new Vector2(0, -350));
                            }
                            else
                            {
                                shadyGuy.Interact();
                            }
                            break;
                        }

                        var shrineCursed = interactableObj.GetComponent<InteractableShrineCursed>();
                        if (shrineCursed != null)
                        {
                            shrineCursed.Interact();
                            break;
                        }

                        var shrineGreed = interactableObj.GetComponent<InteractableShrineGreed>();
                        if (shrineGreed != null)
                        {
                            shrineGreed.Interact();
                            break;
                        }

                        var shrineChallenge = interactableObj.GetComponent<InteractableShrineChallenge>();
                        if (shrineChallenge != null)
                        {
                            var isHost = IsServerMode() ?? false;
                            if (isHost)
                            {
                                shrineChallenge.Interact();
                            }
                            else
                            {
                                shrineChallenge.done = true;
                                shrineChallenge.fx.SetActive(true);
                                GameObject.Destroy(shrineChallenge.alertIcon);
                            }

                            break;
                        }

                        var shrineMagnet = interactableObj.GetComponent<InteractableShrineMagnet>();
                        if (shrineMagnet != null)
                        {
                            shrineMagnet.Interact();
                            break;
                        }

                        var bossSpawner = interactableObj.GetComponent<InteractableBossSpawner>();
                        if (bossSpawner != null)
                        {
                            bossSpawner.Interact();
                            break;
                        }

                        var finalBossSpawner = interactableObj.GetComponent<InteractableBossSpawnerFinal>();
                        if (finalBossSpawner != null)
                        {
                            finalBossSpawner.Interact();
                            TransitionToState(GameEvent.PortalOpened);
                            break;
                        }

                        var characterFight = interactableObj.GetComponentInChildren<InteractableCharacterFight>();
                        if (characterFight != null)
                        {
                            characterFight.chargeFx.SetActive(true);
                            interactableObj.SetActive(false);
                            break;
                        }

                        var interactableTumbleWeed = interactableObj.GetComponent<InteractableTumbleWeed>();
                        if (interactableTumbleWeed != null)
                        {
                            playerManagerService.AddGetNetplayerPositionRequest(used.OwnerId);
                            interactableTumbleWeed.Interact();
                            playerManagerService.UnqueueNetplayerPositionRequest();
                            break;
                        }

                        var interactablePot = interactableObj.GetComponent<InteractablePot>();
                        if (interactablePot != null)
                        {
                            // Restored in a finally: a throw in Interact would otherwise strand the
                            // request and redirect this peer's position reads (P1-11).
                            playerManagerService.AddGetNetplayerPositionRequest(used.OwnerId);
                            try
                            {
                                interactablePot.Interact();
                            }
                            finally
                            {
                                playerManagerService.UnqueueNetplayerPositionRequest();
                            }
                            break;
                        }

                        var interactableBoombox = interactableObj.GetComponentInChildren<InteractableBoombox>();
                        if (interactableBoombox != null)
                        {
                            interactableBoombox.Interact();
                            break;
                        }

                        var interactableDesertGrave = interactableObj.GetComponentInChildren<InteractableDesertGrave>();
                        if (interactableDesertGrave != null)
                        {
                            interactableDesertGrave.Interact();
                            interactableObj.SetActive(false);
                            break;
                        }

                        var interactableSkeletonKingStatue = interactableObj.GetComponentInChildren<InteractableSkeletonKingStatue>();
                        if (interactableSkeletonKingStatue != null)
                        {
                            interactableSkeletonKingStatue.Interact();
                            interactableObj.SetActive(false);
                            break;
                        }

                        var interactableCryptLeave = interactableObj.GetComponentInChildren<InteractableCryptLeave>();
                        if (interactableCryptLeave != null)
                        {
                            interactableCryptLeave.Interact();
                            break;
                        }

                        var interactableCoffin = interactableObj.GetComponentInChildren<InteractableCoffin>();
                        if (interactableCoffin != null)
                        {
                            interactableCoffin.Interact();
                            currentCoffin = interactableCoffin;

                            if (IsServerMode() == false)
                            {
                                currentCoffin.minibossEnemies = new Il2CppSystem.Collections.Generic.HashSet<Enemy>();
                            }
                            break;
                        }

                        var interactableCrypt = interactableObj.GetComponentInChildren<InteractableCrypt>();
                        if (interactableCrypt != null)
                        {
                            interactableCrypt.Interact();
                            break;
                        }

                        var interactableGhostBossLeave = interactableObj.GetComponentInChildren<InteractableGhostBossLeave>();
                        if (interactableGhostBossLeave != null)
                        {
                            interactableGhostBossLeave.Interact();
                            break;
                        }

                        var interactableGift = interactableObj.GetComponentInChildren<InteractableGift>();
                        if (interactableGift != null)
                        {
                            interactableGift.Interact();
                            break;
                        }

                        var interactableGravestone = interactableObj.GetComponentInChildren<InteractableGravestone>();
                        if (interactableGravestone != null)
                        {
                            interactableGravestone.Interact();
                            break;
                        }

                        var interactableReviver = interactableObj.GetComponentInChildren<InteractableReviver>();
                        if (interactableReviver != null)
                        {
                            interactableReviver.Interact();
                            break;
                        }

                        var interactableEgg = interactableObj.GetComponentInChildren<InteractableEgg>();
                        if (interactableEgg != null)
                        {
                            var isHost = IsServerMode() ?? false;
                            if (isHost)
                            {
                                interactableEgg.Interact();
                            }
                            else
                            {
                                interactableEgg.done = true;
                                interactableEgg.breakFx.SetActive(true);
                                GameObject.Destroy(interactableObj);
                            }
                            break;
                        }

                        logger.LogWarning("Interactable type for Interact action not recognized.");

                        break;
                }
            }

            // OB-12: outside the switch so Interact counts exactly once whichever exit ran.
            if (used.Action == InteractableAction.Interact)
            {
                CountInteractableIfDispatchDidNot(interactableObj, counterBefore);
            }
        }

        /// <summary>
        /// FIX P2-3: shared charge-start for shrine / pylon / lamp. P0-1: the occupancy check must
        /// come before the write, or a rejected charger overwrites the current one.
        /// </summary>
        /// <returns>True when the caller should let the vanilla charge start run locally.</returns>
        /// <param name="replayOnEveryPeer">
        /// Broadcast even when already charging, so every peer runs its own per-peer <c>OnTriggerEnter</c> visuals.
        /// <para>Only safe where the trigger is idempotent: decompiled <c>ChargeShrine$$OnTriggerEnter</c>
        /// opens with <c>if (rewardGiven || charging) return;</c>. Pylon and lamp triggers are
        /// <b>UNVERIFIED</b>, so they pass <c>false</c>.</para>
        /// </param>
        private bool HandleChargingStart(
            uint netplayId,
            ConcurrentDictionary<uint, ICollection<uint>> chargingPlayers,
            IGameNetworkMessage message,
            string label,
            bool replayOnEveryPeer = false)
        {
            var isHost = IsServerMode() ?? false;

            if (!isHost)
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
                return false;
            }

            var localId = playerManagerService.GetLocalPlayer().ConnectionId;

            if (chargingPlayers.TryGetValue(netplayId, out var chargers)
                && chargers != null && chargers.Count > 0)
            {
                logger.LogInfo(
                    $"Another player is already charging this {label} {netplayId}. Preventing re "
                    + $"trigger. Chargers: [{string.Join(", ", chargers)}], joining: {localId}.");

                // Record the second charger, or their stop is later ignored.
                if (!chargers.Contains(localId))
                {
                    chargers.Add(localId);
                }

                if (replayOnEveryPeer)
                {
                    udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
                }

                return false;
            }

            chargingPlayers[netplayId] = [localId];

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);

            return true;
        }

        /// <summary>
        /// FIX P2-3: stop counterpart of <see cref="HandleChargingStart"/>. P0-2: a stop can arrive with no recorded start.
        /// </summary>
        /// <returns>True when this was the last charger and the vanilla stop should run.</returns>
        private bool HandleChargingStop(
            uint netplayId,
            ConcurrentDictionary<uint, ICollection<uint>> chargingPlayers,
            IGameNetworkMessage message,
            string label)
        {
            var isHost = IsServerMode() ?? false;

            if (!isHost)
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
                return false;
            }

            if (!chargingPlayers.TryGetValue(netplayId, out var chargers)
                || chargers == null || chargers.Count == 0)
            {
                logger.LogInfo(
                    $"No one is charging this {label} {netplayId}; ignoring stop from "
                    + $"{playerManagerService.GetLocalPlayer()?.ConnectionId}.");
                return false;
            }

            chargers.Remove(playerManagerService.GetLocalPlayer().ConnectionId);

            if (chargers.Count > 0)
            {
                // Ids logged: a never-removed charger makes the object charge forever.
                logger.LogInfo(
                    $"Another player is still charging this {label} {netplayId}. Preventing stop "
                    + $"trigger. Remaining chargers: [{string.Join(", ", chargers)}].");
                return false;
            }

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);

            return true;
        }

        public bool OnStartingToChargingShrine(uint shrineNetplayId)
        {
            IGameNetworkMessage message = new StartingChargingShrine
            {
                ShrineNetplayId = shrineNetplayId,
                PlayerChargingId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            return HandleChargingStart(shrineNetplayId, shrineChargingPlayers, message, "shrine", replayOnEveryPeer: true);
        }

        private void OnReceivedStartingToChargingShrine(StartingChargingShrine shrine)
        {
            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // FIX P0-1 / P2-2 — see OnStartingToChargingShrine.
                if (shrineChargingPlayers.TryGetValue(shrine.ShrineNetplayId, out var chargers)
                    && chargers != null && chargers.Count > 0)
                {
                    // Record the joiner and still broadcast, so every peer runs its trigger visuals.
                    if (!chargers.Contains(shrine.PlayerChargingId))
                    {
                        chargers.Add(shrine.PlayerChargingId);
                    }

                    logger.LogInfo(
                        $"Shrine {shrine.ShrineNetplayId}: {shrine.PlayerChargingId} joined an "
                        + $"in-progress charge. Chargers: [{string.Join(", ", chargers)}].");

                    udpClientService.SendToAllClients(shrine, NetDelivery.ReliableOrdered);
                    return;
                }

                shrineChargingPlayers[shrine.ShrineNetplayId] = [shrine.PlayerChargingId];

                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(shrine.ShrineNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Spawned object not found in SpawnedObjectManagerService when processing OnReceivedStartingToChargingShrine.");
                    return;
                }

                var shrineObj = spawnedObj.GetComponent<ChargeShrine>();
                if (shrineObj == null)
                {
                    logger.LogWarning("ChargeShrine component not found on spawned object when processing OnReceivedStartingToChargingShrine.");
                    return;
                }


                using (Plugin.SuppressOutbound())
                {
                    shrineObj.OnTriggerEnter();
                }

                udpClientService.SendToAllClients(shrine, NetDelivery.ReliableOrdered);
            }
            else
            {

                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(shrine.ShrineNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Spawned object not found in SpawnedObjectManagerService when processing OnReceivedStartingToChargingShrine.");
                    return;
                }

                var shrineObj = spawnedObj.GetComponent<ChargeShrine>();
                if (shrineObj == null)
                {
                    logger.LogWarning("ChargeShrine component not found on spawned object when processing OnReceivedStartingToChargingShrine.");
                    return;
                }

                using (Plugin.SuppressOutbound())
                {
                    shrineObj.OnTriggerEnter();
                }

            }
        }

        public bool OnStoppingChargingShrine(uint shrineNetplayId)
        {
            IGameNetworkMessage message = new StoppingChargingShrine
            {
                ShrineNetplayId = shrineNetplayId,
                PlayerChargingId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            return HandleChargingStop(shrineNetplayId, shrineChargingPlayers, message, "shrine");
        }

        private void OnReceivedStoppingChargingShrine(StoppingChargingShrine shrine)
        {
            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // FIX P0-2 / P2-2 — see OnStoppingChargingShrine.
                if (!shrineChargingPlayers.TryGetValue(shrine.ShrineNetplayId, out var chargers)
                    || chargers == null || chargers.Count == 0)
                {
                    return;
                }

                var removed = chargers.Remove(shrine.PlayerChargingId);

                // A stop for someone who was never recorded is the shape that leaves a set
                // permanently non-empty, and it is silent otherwise.
                logger.LogInfo(
                    $"Shrine {shrine.ShrineNetplayId}: {shrine.PlayerChargingId} stopped charging "
                    + $"(was recorded: {removed}). Remaining: [{string.Join(", ", chargers)}].");

                if (chargers.Count > 0)
                {
                    return;
                }

                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(shrine.ShrineNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Spawned object not found in SpawnedObjectManagerService when processing OnReceivedStartingToChargingShrine.");
                    return;
                }

                var shrineObj = spawnedObj.GetComponent<ChargeShrine>();
                if (shrineObj == null)
                {
                    logger.LogWarning("ChargeShrine component not found on spawned object when processing OnReceivedStartingToChargingShrine.");
                    return;
                }


                using (Plugin.SuppressOutbound())
                {
                    shrineObj.OnTriggerExit();
                }

                udpClientService.SendToAllClients(shrine, NetDelivery.ReliableOrdered);
            }
            else
            {
                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(shrine.ShrineNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Spawned object not found in SpawnedObjectManagerService when processing OnReceivedStartingToChargingShrine.");
                    return;
                }

                var shrineObj = spawnedObj.GetComponent<ChargeShrine>();
                if (shrineObj == null)
                {
                    logger.LogWarning("ChargeShrine component not found on spawned object when processing OnReceivedStartingToChargingShrine.");
                    return;
                }

                using (Plugin.SuppressOutbound())
                {
                    shrineObj.OnTriggerExit();
                }

            }
        }

        public void OnEnemyExploder(Enemy enemy)
        {
            var enemySpawned = enemyManagerService.GetEnemyByReference(enemy);
            if (enemySpawned.Value == null)
            {
                logger.LogWarning("Enemy not found in EnemyManagerService when processing OnEnemyExploder.");
                return;
            }

            IGameNetworkMessage message = new EnemyExploder
            {
                EnemyId = enemySpawned.Key,
                SenderId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedEnemyExploder(EnemyExploder exploder)
        {
            var enemy = enemyManagerService.GetEnemyById(exploder.EnemyId);
            if (enemy == null)
            {
                logger.LogWarning("Enemy not found when processing OnReceivedEnemyExploder.");
                return;
            }

            // FIX (P1-10, P0-6): restored in finally so a throw cannot latch the flag.
            Plugin.CAN_ENEMY_EXPLODE = true;
            try
            {
                EffectManager.Instance.ExploderEnemy(enemy);
            }
            finally
            {
                Plugin.CAN_ENEMY_EXPLODE = false;
            }
            enemyManagerService.RemoveEnemyById(exploder.EnemyId);
        }

        public void OnSpawnedEnemySpecialAttack(Enemy enemy, EnemySpecialAttack attack)
        {
            var enemySpawned = enemyManagerService.GetEnemyByReference(enemy);
            if (enemySpawned.Value == null)
            {
                return;
            }

            var targetId = DynamicData.For(enemy).Get<uint?>("targetId");
            if (targetId == null)
            {
                logger.LogWarning("Enemy has no targetId when processing OnSpawnedEnemySpecialAttack.");
                return;
            }


            // FIX P1-4 / P1-6: one allocation, and null instead of a throw when nobody is alive.
            var randomTargetId = playerManagerService.GetRandomPlayerAliveConnectionId();
            if (!randomTargetId.HasValue)
            {
                return; // nobody alive to target — the run is over
            }

            DynamicData.For(enemy).Set("targetId", randomTargetId.Value); //Random target

            IGameNetworkMessage message = new SpawnedEnemySpecialAttack
            {
                EnemyId = enemySpawned.Key,
                AttackName = attack.attackName,
                TargetId = randomTargetId.Value
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedSpawnedEnemySpecialAttack(SpawnedEnemySpecialAttack attack)
        {
            var enemy = enemyManagerService.GetEnemyById(attack.EnemyId);
            if (enemy == null)
            {
                logger.LogWarning("Enemy not found in EnemyManagerService when processing OnReceivedSpawnedEnemySpecialAttack.");
                return;
            }

            if (enemy.specialAttackController == null)
            {
                logger.LogWarning("EnemySpecialAttackController is null on enemy when processing OnReceivedSpawnedEnemySpecialAttack.");
                return;
            }

            EnemySpecialAttack specialAttack = null;
            foreach (var specialAtt in enemy.specialAttackController.attacks)
            {
                if (specialAtt.attackName == attack.AttackName)
                {
                    specialAttack = specialAtt;
                    break;
                }
            }

            if (specialAttack == null)
            {
                logger.LogWarning("EnemySpecialAttack not found on enemy when processing OnReceivedSpawnedEnemySpecialAttack.");
                return;
            }

            DynamicData.For(enemy).Set("targetId", attack.TargetId); //Target might have changed

            // FIX (P1-10, P0-6): restored in finally so a throw cannot latch the flag.
            Plugin.CAN_ENEMY_USE_SPECIAL_ATTACK = true;
            try
            {
                enemy.specialAttackController.UseSpecialAttack(specialAttack);
            }
            finally
            {
                Plugin.CAN_ENEMY_USE_SPECIAL_ATTACK = false;
            }
        }

        public bool IsLoadingNextLevel()
        {
            return currentState == State.LoadingNextLevel;
        }

        public bool OnStartingToChargingPylon(uint pylonNetplayId)
        {
            IGameNetworkMessage message = new StartingChargingPylon
            {
                PylonNetplayId = pylonNetplayId,
                PlayerChargingId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            return HandleChargingStart(pylonNetplayId, pylonChargingPlayers, message, "pylon");
        }

        private void OnReceivedStartingToChargingPylon(StartingChargingPylon pylon)
        {
            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // FIX P0-1 / P2-2 — see OnStartingToChargingShrine.
                if (pylonChargingPlayers.TryGetValue(pylon.PylonNetplayId, out var chargers)
                    && chargers != null && chargers.Count > 0)
                {
                    return;
                }

                pylonChargingPlayers[pylon.PylonNetplayId] = [pylon.PlayerChargingId];

                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(pylon.PylonNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Pylon object not found in SpawnedObjectManagerService when processing OnReceivedStartingToChargingPylon.");
                    return;
                }

                var pylonObj = spawnedObj.GetComponent<BossPylon>();
                if (pylonObj == null)
                {
                    logger.LogWarning("Pylon component not found on spawned object when processing OnReceivedStartingToChargingPylon.");
                    return;
                }


                using (Plugin.SuppressOutbound())
                {
                    pylonObj.OnTriggerEnter();
                }

                udpClientService.SendToAllClients(pylon, NetDelivery.ReliableOrdered);
            }
            else
            {

                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(pylon.PylonNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Pylon object not found in SpawnedObjectManagerService when processing OnReceivedStartingToChargingPylon.");
                    return;
                }

                var pylonObj = spawnedObj.GetComponent<BossPylon>();
                if (pylonObj == null)
                {
                    logger.LogWarning("Pylon component not found on spawned object when processing OnReceivedStartingToChargingPylon.");
                    return;
                }

                using (Plugin.SuppressOutbound())
                {
                    pylonObj.OnTriggerEnter();
                }

            }
        }

        public bool OnStartingToChargingLamp(uint lampNetplayId)
        {
            IGameNetworkMessage message = new StartingChargingLamp
            {
                LampNetplayId = lampNetplayId,
                PlayerChargingId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            return HandleChargingStart(lampNetplayId, lampsChargingPlayers, message, "lamp");
        }

        private void OnReceivedStartingToChargingLamp(StartingChargingLamp lamp)
        {
            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // FIX P0-1 / P2-2 — see OnStartingToChargingShrine.
                if (lampsChargingPlayers.TryGetValue(lamp.LampNetplayId, out var chargers)
                    && chargers != null && chargers.Count > 0)
                {
                    return;
                }

                lampsChargingPlayers[lamp.LampNetplayId] = [lamp.PlayerChargingId];

                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(lamp.LampNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Lamp object not found in SpawnedObjectManagerService when processing OnReceivedStartingToChargingLamp.");
                    return;
                }

                var lampObj = spawnedObj.GetComponent<BossLamp>();
                if (lampObj == null)
                {
                    logger.LogWarning("Lamp component not found on spawned object when processing OnReceivedStartingToChargingLamp.");
                    return;
                }


                using (Plugin.SuppressOutbound())
                {
                    lampObj.OnTriggerEnter();
                }

                udpClientService.SendToAllClients(lamp, NetDelivery.ReliableOrdered);
            }
            else
            {

                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(lamp.LampNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Lamp object not found in SpawnedObjectManagerService when processing OnReceivedStartingToChargingLamp.");
                    return;
                }

                var lampObj = spawnedObj.GetComponent<BossLamp>();
                if (lampObj == null)
                {
                    logger.LogWarning("Lamp component not found on spawned object when processing OnReceivedStartingToChargingLamp.");
                    return;
                }

                using (Plugin.SuppressOutbound())
                {
                    lampObj.OnTriggerEnter();
                }

            }
        }

        public bool OnStoppingChargingPylon(uint pylonNetplayId)
        {
            // Kept at the call site: this log line exists only on the pylon path, and the point of
            // P2-3 is a behaviour-preserving dedup.
            logger.LogInfo($"Player {playerManagerService.GetLocalPlayer().ConnectionId} stopping charging pylon {pylonNetplayId}");

            IGameNetworkMessage message = new StoppingChargingPylon
            {
                PylonNetplayId = pylonNetplayId,
                PlayerChargingId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            return HandleChargingStop(pylonNetplayId, pylonChargingPlayers, message, "pylon");
        }

        private void OnReceivedStoppingChargingPylon(StoppingChargingPylon pylon)
        {
            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // FIX P0-2 / P2-2 — see OnStoppingChargingShrine.
                if (!pylonChargingPlayers.TryGetValue(pylon.PylonNetplayId, out var chargers)
                    || chargers == null || chargers.Count == 0)
                {
                    return;
                }

                chargers.Remove(pylon.PlayerChargingId);

                if (chargers.Count > 0)
                {
                    return;
                }

                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(pylon.PylonNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Spawned Pylon not found in SpawnedObjectManagerService when processing OnReceivedStoppingChargingPylon.");
                    return;
                }

                var pylonObj = spawnedObj.GetComponent<BossPylon>();
                if (pylonObj == null)
                {
                    logger.LogWarning("Pylon component not found on spawned object when processing OnReceivedStoppingChargingPylon.");
                    return;
                }


                using (Plugin.SuppressOutbound())
                {
                    pylonObj.OnTriggerExit();
                }

                udpClientService.SendToAllClients(pylon, NetDelivery.ReliableOrdered);
            }
            else
            {
                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(pylon.PylonNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Spawned pylon not found in SpawnedObjectManagerService when processing OnReceivedStoppingChargingPylon.");
                    return;
                }

                var pylonObj = spawnedObj.GetComponent<BossPylon>();
                if (pylonObj == null)
                {
                    logger.LogWarning("Pylon component not found on spawned object when processing OnReceivedStoppingChargingPylon.");
                    return;
                }

                using (Plugin.SuppressOutbound())
                {
                    pylonObj.OnTriggerExit();
                }
            }
        }

        public bool OnStoppingChargingLamp(uint lampNetplayId)
        {
            IGameNetworkMessage message = new StoppingChargingLamp
            {
                LampNetplayId = lampNetplayId,
                PlayerChargingId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            return HandleChargingStop(lampNetplayId, lampsChargingPlayers, message, "lamp");
        }

        private void OnReceivedStoppingChargingLamp(StoppingChargingLamp lamp)
        {
            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // FIX P0-2 / P2-2 — see OnStoppingChargingShrine.
                if (!lampsChargingPlayers.TryGetValue(lamp.LampNetplayId, out var chargers)
                    || chargers == null || chargers.Count == 0)
                {
                    return;
                }

                chargers.Remove(lamp.PlayerChargingId);

                if (chargers.Count > 0)
                {
                    return;
                }

                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(lamp.LampNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Spawned Lamp not found in SpawnedObjectManagerService when processing OnReceivedStoppingChargingLamp.");
                    return;
                }

                var lampObj = spawnedObj.GetComponent<BossLamp>();
                if (lampObj == null)
                {
                    logger.LogWarning("Lamp component not found on spawned object when processing OnReceivedStoppingChargingLamp.");
                    return;
                }


                using (Plugin.SuppressOutbound())
                {
                    lampObj.OnTriggerExit();
                }

                udpClientService.SendToAllClients(lamp, NetDelivery.ReliableOrdered);
            }
            else
            {
                var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(lamp.LampNetplayId);

                if (spawnedObj == null)
                {
                    logger.LogWarning("Spawned lamp not found in SpawnedObjectManagerService when processing OnReceivedStoppingChargingLamp.");
                    return;
                }

                var lampObj = spawnedObj.GetComponent<BossLamp>();
                if (lampObj == null)
                {
                    logger.LogWarning("Lamp component not found on spawned object when processing OnReceivedStoppingChargingLamp.");
                    return;
                }

                using (Plugin.SuppressOutbound())
                {
                    lampObj.OnTriggerExit();
                }
            }
        }

        public void OnFinalBossOrbsSpawned(Orb orb)
        {
            var nexts = finalBossOrbManagerService.GetNextTargetAndOrbId();

            if (nexts == null)
            {
                logger.LogWarning("No target found for final boss orb spawn.");
                return;
            }

            while (nexts != null)
            {
                (var nextTarget, var orbId) = nexts;

                IGameNetworkMessage message = new FinalBossOrbSpawned
                {
                    OrbType = orb,
                    Target = nextTarget,
                    OrbId = orbId
                };

                udpClientService.SendToAllClients(message, NetDelivery.ReliableUnordered);

                nexts = finalBossOrbManagerService.GetNextTargetAndOrbId();
            }

            finalBossOrbManagerService.ClearQueueNextTarget();
        }

        private void OnReceivedFinalBossOrbsSpawned(FinalBossOrbSpawned spawned)
        {
            var bossPosition = MusicController.Instance.finalFightController.boss.transform.position;

            switch (spawned.OrbType)
            {
                case Orb.Bleed:
                    var gameObject = GameObject.Instantiate(MusicController.Instance.finalFightController.orbBleed);
                    gameObject.transform.position = new Vector3(bossPosition.x, bossPosition.y, bossPosition.z);
                    var orbBleed = gameObject.GetComponent<BossOrbBleed>();
                    orbBleed.isFired = false;
                    orbBleed.Set(MusicController.Instance.finalFightController.boss, MusicController.Instance.finalFightController.currentPhase, MusicController.Instance.finalFightController.currentPhase + 1, 1);

                    var interpolator = gameObject.AddComponent<BossOrbInterpolator>();
                    interpolator.Initialize(gameObject);
                    finalBossOrbManagerService.SetOrbTarget(spawned.Target, gameObject, spawned.OrbId);

                    break;
                case Orb.Following:
                    var gameObjectF = GameObject.Instantiate(MusicController.Instance.finalFightController.orbFollowing);
                    gameObjectF.transform.position = new Vector3(bossPosition.x, bossPosition.y, bossPosition.z);
                    var orbFollowing = gameObjectF.GetComponent<BossOrb>();
                    orbFollowing.isFired = false;
                    orbFollowing.Set(1, MusicController.Instance.finalFightController.currentPhase, MusicController.Instance.finalFightController.boss, 1, 1);

                    var interpolatorF = gameObjectF.AddComponent<BossOrbInterpolator>();
                    interpolatorF.Initialize(gameObjectF);
                    finalBossOrbManagerService.SetOrbTarget(spawned.Target, gameObjectF, spawned.OrbId);

                    break;
                case Orb.Shooty:
                    var gameObjectS = GameObject.Instantiate(MusicController.Instance.finalFightController.orbShooty);
                    gameObjectS.transform.position = new Vector3(bossPosition.x, bossPosition.y, bossPosition.z);
                    var orbShooty = gameObjectS.GetComponent<BossOrbShooty>();
                    orbShooty.isFired = false;
                    orbShooty.Set(MusicController.Instance.finalFightController.boss, MusicController.Instance.finalFightController.currentPhase, MusicController.Instance.finalFightController.currentPhase + 1, 1);

                    var interpolatorS = gameObjectS.AddComponent<BossOrbInterpolator>();
                    interpolatorS.Initialize(gameObjectS);
                    finalBossOrbManagerService.SetOrbTarget(spawned.Target, gameObjectS, spawned.OrbId);

                    break;
            }
        }


        private void OnReceivedFinalBossOrbsUpdate(IEnumerable<BossOrbModel> bossOrbs)
        {
            foreach (var bossOrb in bossOrbs)
            {
                var obj = finalBossOrbManagerService.GetOrbById(bossOrb.Id);
                if (obj == null)
                {
                    continue;
                }

                var interpolator = obj.GetComponent<BossOrbInterpolator>();
                if (interpolator == null)
                {
                    continue;
                }

                var snapshot = new BossOrbSnapshot
                {
                    Timestamp = Time.timeAsDouble,
                    Position = Quantizer.Dequantize(bossOrb.Position),
                };

                interpolator.AddSnapshot(snapshot);
            }
        }

        public void OnFinalBossOrbDestroyed(uint removed)
        {
            IGameNetworkMessage message = new FinalBossOrbDestroyed
            {
                OrbId = removed,
                SenderId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableUnordered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedFinalBossOrbDestroyed(FinalBossOrbDestroyed destroyed)
        {
            var orbObj = finalBossOrbManagerService.GetOrbById(destroyed.OrbId);
            if (orbObj != null)
            {
                finalBossOrbManagerService.RemoveOrbTarget(orbObj);
                GameObject.Destroy(orbObj);
            }
            else
            {
                logger.LogWarning($"Failed to destroy orb with id {destroyed.OrbId} as its not found");
            }
        }

        public void OnSwarmEvent(TimelineEvent currentEvent)
        {
            IGameNetworkMessage message = new StartedSwarmEvent
            {
                Duration = currentEvent.duration,
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedSwarmEvent(StartedSwarmEvent startedSwarmEvent)
        {
            var timeline = new TimelineEvent
            {
                duration = startedSwarmEvent.Duration,
                enemies = new Il2CppSystem.Collections.Generic.List<EEnemy>(),
                eTimelineEvent = ETimelineEvent.ESwarm
            };
            EnemyManager.Instance.summonerController.EventSwarm(timeline);
        }

        private void OnReceivedGameOver(GameOver over)
        {
            stateBroadcastService.GameOver();
            TransitionToState(GameEvent.GameOver);
        }

        public void OnPlayerDied()
        {
            var localPlayer = playerManagerService.GetLocalPlayer();

            if (localPlayer.Hp == 0)
            {
                return;
            }

            localPlayer.Hp = 0;
            playerManagerService.UpdatePlayer(localPlayer);
            GameManager.Instance.player.playerRenderer.gameObject.SetActive(false);

            var isHost = IsServerMode() ?? false;
            var playerId = localPlayer.ConnectionId;

            IGameNetworkMessage diedMessage = new PlayerDied
            {
                PlayerId = localPlayer.ConnectionId
            };

            if (!isHost)
            {
                udpClientService.SendToHost(diedMessage, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToAllClients(diedMessage, NetDelivery.ReliableOrdered);

                var allPlayersAliveIdWithout = playerManagerService.GetAllPlayersAlive().Where(p => p.ConnectionId != playerId).Select(p => p.ConnectionId).ToList();
                var updated = enemyManagerService.ReTargetEnemies(playerId, allPlayersAliveIdWithout);

                // FIX 5/6: apply locally too; see OnReceivedPlayerDisconnected.
                var updatedList = updated as IList<(uint, uint)> ?? updated.ToList();
                enemyManagerService.ApplyRetargetedEnemies(updatedList, playerManagerService.GetConnectionIdsAndRigidBodies());

                IGameNetworkMessage message = new RetargetedEnemies
                {
                    Enemy_NewTargetids = updatedList
                };

                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);

                SpawnReviver(GameManager.Instance.player.transform.position, GameManager.Instance.player.playerRenderer.activeMaterials, localPlayer.ConnectionId);
            }
        }

        private void OnReceivedPlayerDied(PlayerDied died)
        {
            var isServer = IsServerMode() ?? false;
            if (isServer)
            {
                var diedPlayer = playerManagerService.GetPlayer(died.PlayerId);
                if (diedPlayer == null)
                {
                    logger.LogWarning("Died player not found in PlayerManagerService when processing OnReceivedPlayerDied.");
                    return;
                }

                diedPlayer.Hp = 0;
                playerManagerService.UpdatePlayer(diedPlayer);
                var netPlayer = playerManagerService.GetNetPlayerByNetplayId(died.PlayerId);
                if (netPlayer == null)
                {
                    logger.LogWarning("Died netplayer not found in PlayerManagerService when processing OnReceivedPlayerDied.");
                    return;
                }

                netPlayer.OnDied();

                IGameNetworkMessage diedMessage = new PlayerDied
                {
                    PlayerId = died.PlayerId
                };

                udpClientService.SendToAllClients(diedMessage, NetDelivery.ReliableOrdered);


                var allPlayersAliveIdWithout = playerManagerService.GetAllPlayersAlive().Where(p => p.ConnectionId != died.PlayerId).Select(p => p.ConnectionId).ToList();
                var updated = enemyManagerService.ReTargetEnemies(died.PlayerId, allPlayersAliveIdWithout);

                // FIX 5/6: apply locally too — see OnReceivedPlayerDisconnected.
                var updatedList = updated as IList<(uint, uint)> ?? updated.ToList();
                enemyManagerService.ApplyRetargetedEnemies(updatedList, playerManagerService.GetConnectionIdsAndRigidBodies());

                IGameNetworkMessage message = new RetargetedEnemies
                {
                    Enemy_NewTargetids = updatedList
                };

                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);

                SpawnReviver(netPlayer.Model.transform.position, netPlayer.GetActiveMaterials(), diedPlayer.ConnectionId);
            }
            else
            {
                var diedNetPlayer = playerManagerService.GetNetPlayerByNetplayId(died.PlayerId);
                if (diedNetPlayer == null)
                {
                    return;
                }

                diedNetPlayer.OnDied();
            }
        }

        private void SpawnReviver(Vector3 position, Material[] materials, uint ownerConnectionId, uint reviverId = 0)
        {
            var desertGraves = EffectManager.Instance.desertGraves;

            // OB-53: host lifts a coffin from under the map to the surface; clients get the position.
            if (IsServerMode() ?? false)
            {
                position = SnapCoffinToSurface(position, ownerConnectionId);
            }

            logger.LogInfo($"[revive] OB-53: coffin for {ownerConnectionId} at {position}.");

            // Suppression covers only the Instantiate, which trips the spawn patches.
            GameObject desertGraveInstance;
            using (Plugin.SuppressOutbound())
            {
                desertGraveInstance = GameObject.Instantiate(desertGraves[0], position, Quaternion.Euler(-90, 0, 0));
            }

            var interactable = desertGraveInstance.GetComponent<InteractableDesertGrave>();
            var chargeFx = GameObject.Instantiate(interactable.chargeFx, desertGraveInstance.transform);
            var explodeFx = GameObject.Instantiate(interactable.explodeFx, desertGraveInstance.transform);

            var isHost = IsServerMode() ?? false;
            var netplayId = reviverId;

            if (isHost)
            {
                netplayId = spawnedObjectManagerService.AddSpawnedObject(desertGraveInstance);

                IGameNetworkMessage message = new SpawnedReviver
                {
                    Position = position.ToNumericsVector3(),
                    OwnerConnectionId = ownerConnectionId,
                    ReviverId = netplayId
                };

                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                spawnedObjectManagerService.SetSpawnedObject(netplayId, desertGraveInstance);
            }

            var reviver = desertGraveInstance.AddComponent<InteractableReviver>();
            reviver.Initialize(chargeFx, explodeFx, materials[0], netplayId, ownerConnectionId);


            GameObject.Destroy(interactable);

        }

        /// <summary>OB-53. Moves a death point that is under a surface up to that surface.</summary>
        private Vector3 SnapCoffinToSurface(Vector3 position, uint ownerConnectionId)
        {
            // 1. Standing on something: a normal death, even under a tree canopy. Leave it.
            if (Physics.Raycast(position + Vector3.up, Vector3.down, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                return position;
            }

            // 2. Just below a surface: lift a little only; a high ray could land on an unclimbable peak.
            const float rayHeight = 120f;
            const float maxLift = 6f;
            var origin = position + Vector3.up * rayHeight;
            if (Physics.Raycast(origin, Vector3.down, out var hit, rayHeight * 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                && hit.point.y > position.y + 1f && hit.point.y <= position.y + maxLift)
            {
                logger.LogInfo($"[revive] OB-53: {ownerConnectionId} died just under the surface at {position}; coffin moved up to {hit.point}.");
                return hit.point + Vector3.up * 0.2f;
            }

            // 3. Deep under the map, inside a hill or mountain, or out in the void: no surface here
            //    can be trusted to be reachable. Beside the nearest living player is, by definition.
            var beside = NearestLivingPlayerPosition(position, ownerConnectionId);
            if (beside.HasValue)
            {
                var target = beside.Value;
                var spot = target + new Vector3(2f, 0f, 0f);
                if (Physics.Raycast(spot + Vector3.up * 3f, Vector3.down, out var ground, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                    && Mathf.Abs(ground.point.y - target.y) <= 2f)
                {
                    target = ground.point;
                }

                logger.LogInfo($"[revive] OB-53: {ownerConnectionId} died off the walkable map at {position}; coffin placed beside the nearest living player at {target}.");
                return target + Vector3.up * 0.2f;
            }

            logger.LogWarning($"[revive] OB-53: {ownerConnectionId} died off the walkable map at {position} and nobody is alive to place the coffin by; leaving it there.");
            return position;
        }

        /// <summary>The nearest living player's position other than <paramref name="exclude"/>, on the host.</summary>
        private Vector3? NearestLivingPlayerPosition(Vector3 from, uint exclude)
        {
            Vector3? best = null;
            var bestDistance = float.MaxValue;

            void Consider(Vector3 candidate)
            {
                var distance = (candidate - from).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            foreach (var player in playerManagerService.GetAllPlayersAlive())
            {
                if (player.ConnectionId == exclude)
                {
                    continue;
                }

                if (playerManagerService.IsLocalConnectionId(player.ConnectionId))
                {
                    var me = GameManager.Instance?.player;
                    if (me != null && !me.IsDead())
                    {
                        Consider(me.transform.position);
                    }

                    continue;
                }

                var model = playerManagerService.GetNetPlayerByNetplayId(player.ConnectionId)?.Model;
                if (model != null)
                {
                    Consider(model.transform.position);
                }
            }

            return best;
        }

        private void OnReceivedSpawnedReviver(SpawnedReviver reviver)
        {
            var player = playerManagerService.GetNetPlayerByNetplayId(reviver.OwnerConnectionId);
            if (player != null)
            {
                SpawnReviver(reviver.Position.ToUnityVector3(), player.GetActiveMaterials(), reviver.OwnerConnectionId, reviver.ReviverId);

            }
            else if (reviver.OwnerConnectionId == playerManagerService.GetLocalPlayer().ConnectionId)
            {
                SpawnReviver(reviver.Position.ToUnityVector3(), GameManager.Instance.player.playerRenderer.activeMaterials, reviver.OwnerConnectionId, reviver.ReviverId);
            }
            else
            {
                logger.LogWarning("Owner player not found in PlayerManagerService when processing OnReceivedSpawnedRviver.");
            }
        }

        private void OnReceivedRetargetedEnemies(RetargetedEnemies enemies)
        {
            var isServer = IsServerMode() ?? false;
            if (!isServer)
            {
                var playerId_rigidbody = playerManagerService.GetConnectionIdsAndRigidBodies();
                enemyManagerService.ApplyRetargetedEnemies(enemies.Enemy_NewTargetids, playerId_rigidbody);
            }
        }

        public void OnRunStarted(RunConfig runConfig)
        {
            TransitionToState(GameEvent.Loading);

            IGameNetworkMessage message = new RunStarted
            {
                MapData = (int)runConfig.mapData.eMap,
                StageData = runConfig.stageData.name,
                MapTierIndex = runConfig.mapTierIndex,
                MusicTrackIndex = runConfig.musicTrackIndex,
                ChallengeName = runConfig.challenge?.name ?? ""
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // First, on the same ReliableOrdered channel, so every client holds the session's
                // settings before its run begins.
                lobbySettingsService.BroadcastToClients();
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);

                // After RunStarted, so the first room reaches each client behind its run start.
                endlessRunService.BeginRun(isHost: true, runConfig);
            }
        }
        private void OnReceivedRunStarted(RunStarted started)
        {
            var mapData = (Assets.Scripts._Data.MapsAndStages.EMap)started.MapData;
            var stageDataName = started.StageData;

            var map = DataManager.Instance.GetMap(mapData);
            var stageData = map.stages.FirstOrDefault(s => s.name == stageDataName);

            if (stageData == null)
            {
                logger.LogWarning($"Stage data {stageDataName} not found in map {mapData} when processing OnReceivedRunStarted.");
                return;
            }

            var runConfig = new RunConfig
            {
                mapData = map,
                stageData = stageData,
                mapTierIndex = started.MapTierIndex,
                musicTrackIndex = started.MusicTrackIndex,
            };

            ChallengeData currentChallenge = stageData.challenges.FirstOrDefault(c => c.name == started.ChallengeName);

            if (currentChallenge != null)
            {
                runConfig.challenge = currentChallenge;
            }

            logger.LogInfo($"Received RunStarted message. Starting new map {mapData} with stage {stageDataName} at index {runConfig.mapTierIndex} with challenge {started.ChallengeName}.");

            TransitionToState(GameEvent.Loading);

            Plugin.Instance.HideModal();
            MapController.StartNewMap(runConfig);
            endlessRunService.BeginRun(isHost: false, runConfig);
        }

        private void OnReceivedPlayerDisconnected(PlayerDisconnected disconnected)
        {
            // The websocket ClientDisconnected path races this one and may have removed the record
            // already; cleanup needs only the id (P2-1). The cosmetic notification runs last, in its own try.
            var disconnectedPeer = playerManagerService.GetPlayer(disconnected.ConnectionId);

            playerManagerService.Disconnect(disconnected.ConnectionId);
            projectileManagerService.RemoveProjectilesByOwnerId(disconnected.ConnectionId);

            // Or the readiness round waits forever on them (cf. SE-6).
            readinessService.RemoveParticipant(disconnected.ConnectionId);

            var isHost = IsServerMode() ?? false;
            var canRetarget = isHost && GameManager.Instance != null && GameManager.Instance.player != null;

            if (canRetarget)
            {
                RetargetAfterDisconnect(disconnected.ConnectionId);
            }

            try
            {
                if (disconnectedPeer == null)
                {
                    logger.LogWarning($"Disconnected player {disconnected.ConnectionId} was already removed from PlayerManagerService (websocket ClientDisconnected won the race); cleanup ran, notification skipped.");
                }
                else
                {
                    Plugin.StartNotification(
                        ("OmniBonk", "PlayerDisconnected"),
                        ("OmniBonk", "PlayerDisconnected_Description"),
                        [disconnectedPeer.Name],
                        AudioManager.Instance.uiAbort,
                        item: EItem.BobDead
                    );
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Could not show the disconnect notification for {disconnected.ConnectionId}: {ex.Message}");
            }
        }

        /// <summary>Host: release a closable encounter, then retarget enemies off the departed player.</summary>
        private void RetargetAfterDisconnect(uint disconnectedConnectionId)
        {
            if (IsSharedExperienceEnabled() && encounterService.IsClosable()) //Making sure to unblock people if somemone leave and we can close
            {
                // Must go through ReleaseBarrier so it names the current round, or peers drop it as stale.
                ReleaseBarrier();
            }

            var allPlayersAliveIdWithout = playerManagerService.GetAllPlayersAlive().Where(p => p.ConnectionId != disconnectedConnectionId).Select(p => p.ConnectionId).ToList();
            var updated = enemyManagerService.ReTargetEnemies(disconnectedConnectionId, allPlayersAliveIdWithout);

            // FIX 5/6 (P2-1): apply locally too. The host does not receive its own broadcast, and only
            // ApplyRetargetedEnemies sets enemy.target; otherwise it keeps a destroyed Rigidbody.
            var updatedList = updated as IList<(uint, uint)> ?? updated.ToList();
            enemyManagerService.ApplyRetargetedEnemies(updatedList, playerManagerService.GetConnectionIdsAndRigidBodies());

            IGameNetworkMessage message = new RetargetedEnemies
            {
                Enemy_NewTargetids = updatedList
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        public void OnLightningStrike(Enemy enemy, int bounces, DamageContainer dc, float bounceRange, float bounceProcCoefficient)
        {
            var enemySpawned = enemyManagerService.GetEnemyByReference(enemy);
            if (enemySpawned.Value == null)
            {
                //logger.LogWarning("Enemy not found in EnemyManagerService when processing OnLightningStrike.");
                return;
            }

            var ownerId = playerManagerService.GetLocalPlayer().ConnectionId;

            IGameNetworkMessage message = new LightningStrike
            {
                EnemyId = enemySpawned.Key,
                Bounces = bounces,
                Damage = dc.damage,
                DamageEffect = (int)dc.damageEffect,
                DamageBlockedByArmor = dc.damageBlockedByArmor,
                DamageSource = DamageSourceHelper.Normalize(dc.damageSource),
                DamageIsCrit = dc.crit,
                DamageProcCoefficient = dc.procCoefficient,
                DamageElement = (int)dc.element,
                DamageFlags = (int)dc.flags,
                DamageKnockback = dc.knockback,
                BounceRange = bounceRange,
                BounceProcCoefficient = bounceProcCoefficient,
                OwnerId = ownerId
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedLightningStrike(LightningStrike lightningStrike)
        {
            var enemy = enemyManagerService.GetEnemyById(lightningStrike.EnemyId);
            if (enemy == null)
            {
                //logger.LogWarning("Enemy not found in EnemyManagerService when processing OnReceivedLightningStrike.");
                return;
            }

            var damageContainer = new DamageContainer(lightningStrike.DamageProcCoefficient, DamageSourceHelper.Normalize(lightningStrike.DamageSource));
            damageContainer.damage = lightningStrike.Damage;
            damageContainer.damageEffect = (EDamageEffect)lightningStrike.DamageEffect;
            damageContainer.damageBlockedByArmor = lightningStrike.DamageBlockedByArmor;
            damageContainer.crit = lightningStrike.DamageIsCrit;
            damageContainer.element = (EElement)lightningStrike.DamageElement;
            damageContainer.flags = (DcFlags)lightningStrike.DamageFlags;
            damageContainer.knockback = lightningStrike.DamageKnockback;
            damageContainer.damageSource = DamageSourceHelper.Normalize(lightningStrike.DamageSource);
            damageContainer.procCoefficient = lightningStrike.DamageProcCoefficient;

            using (Plugin.SuppressOutbound())
            {
                WeaponUtility.LightningStrike(
                    enemy,
                    lightningStrike.Bounces,
                    damageContainer,
                    lightningStrike.BounceRange,
                    lightningStrike.BounceProcCoefficient
                );
            }
        }

        public void OnTornadoesSpawned(int amount)
        {
            IGameNetworkMessage message = new TornadoesSpawned
            {
                Amount = amount
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedTornadoesSpawned(TornadoesSpawned spawned)
        {
            // FIX (P1-10, P0-6): restored in finally so a throw cannot latch the flag.
            Plugin.Instance.CAN_SPAWN_TORNADOES = true;
            try
            {
                EffectManager.Instance.SpawnTornadoes(spawned.Amount);
            }
            finally
            {
                Plugin.Instance.CAN_SPAWN_TORNADOES = false;
            }
        }

        public void OnStormStarted(DesertStorm desertStorm)
        {
            var stormOverAtTime = desertStorm.fadeOverTime;
            IGameNetworkMessage message = new StormStarted
            {
                StormOverAtTime = stormOverAtTime
            };
            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedStormStarted(StormStarted started)
        {
            // FIX (P1-10, P0-6): restored in finally so a throw cannot latch the flag.
            Plugin.Instance.CAN_START_STOP_STORMS = true;
            try
            {
                var desertEvent = Plugin.Instance.GetMapEventsDesert();
                if (desertEvent == null)
                {
                    // The storm belongs to a Desert stage this peer has already left (or not yet
                    // reached): nothing to start here.
                    logger.LogInfo("A storm start arrived outside a Desert stage; ignored.");
                    return;
                }

                desertEvent.StartStorm();
                desertEvent.stormOverAtTime = started.StormOverAtTime;
            }
            finally
            {
                Plugin.Instance.CAN_START_STOP_STORMS = false;
            }
        }

        public void OnStormStopped()
        {
            IGameNetworkMessage message = new StormStopped();
            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedStormStopped(StormStopped stopped)
        {
            // FIX (P1-10, P0-6): restored in finally so a throw cannot latch the flag.
            Plugin.Instance.CAN_START_STOP_STORMS = true;
            try
            {
                Plugin.Instance.GetMapEventsDesert()?.StopStorm();
            }
            finally
            {
                Plugin.Instance.CAN_START_STOP_STORMS = false;
            }
        }

        public void OnTumbleWeedSpawned(InteractableTumbleWeed tumbleWeed)
        {
            var netplayId = spawnedObjectManagerService.AddSpawnedObject(tumbleWeed.gameObject);

            IGameNetworkMessage message = new TumbleWeedSpawned
            {
                NetplayId = netplayId,
                Position = Quantizer.Quantize(tumbleWeed.transform.position),
                Velocity = Quantizer.Quantize(tumbleWeed.rb.velocity),
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);

            DynamicData.For(tumbleWeed).Set("netplayId", netplayId);
        }

        private void OnReceivedTumbleWeedSpawned(TumbleWeedSpawned spawned)
        {
            var tumbleWeedObj = GameObject.Instantiate(EffectManager.Instance.tumbleweed);
            var interactable = tumbleWeedObj.GetComponent<InteractableTumbleWeed>();
            spawnedObjectManagerService.SetSpawnedObject(spawned.NetplayId, tumbleWeedObj);
            interactable.transform.position = Quantizer.Dequantize(spawned.Position);
            interactable.rb.velocity = Quantizer.Dequantize(spawned.Velocity);

            spawnedObjectManagerService.RegisterTumbleWeedForInterpolation(spawned.NetplayId, tumbleWeedObj);
        }

        private void OnReceivedTumbleWeedsUpdate(IEnumerable<TumbleWeedModel> tumbles)
        {
            if (currentState < State.Started)
            {
                return;
            }

            if (tumbles == null || !tumbles.Any())
            {
                return;
            }

            var tumbleWeedSnapshots = new List<TumbleWeedSnapshot>();

            foreach (var model in tumbles)
            {

                var snapshot = new TumbleWeedSnapshot
                {
                    Timestamp = Time.timeAsDouble,
                    Position = Quantizer.Dequantize(model.Position),
                    Id = model.NetplayId
                };
                tumbleWeedSnapshots.Add(snapshot);
            }

            spawnedObjectManagerService.UpdateTumbleWeedSnapshots(tumbleWeedSnapshots);
        }

        public void OnTumbleWeedDespawned(InteractableTumbleWeed instance)
        {
            var netplayId = spawnedObjectManagerService.GetByReference(instance.gameObject);
            if (!netplayId.HasValue)
            {
                netplayId = DynamicData.For(instance).Get<uint?>("netplayId"); //Second attempt
                if (!netplayId.HasValue)
                {
                    logger.LogWarning("TumbleWeed not found in SpawnedObjectManagerService when processing OnTumbleWeedDespawned.");

                    return;
                }
            }

            spawnedObjectManagerService.RemoveSpawnedObject(netplayId.Value, instance.gameObject, false);

            IGameNetworkMessage message = new TumbleWeedDespawned
            {
                NetplayId = netplayId.Value
            };
            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedTumbleWeedDespawned(TumbleWeedDespawned despawned)
        {
            var tumbleWeedObj = spawnedObjectManagerService.GetSpawnedObject(despawned.NetplayId);
            if (tumbleWeedObj == null)
            {
                logger.LogWarning("TumbleWeed not found in SpawnedObjectManagerService when processing OnReceivedTumbleWeedDespawned.");
                return;
            }

            spawnedObjectManagerService.UnregisterTumbleWeedFromInterpolation(despawned.NetplayId);
            spawnedObjectManagerService.RemoveSpawnedObject(despawned.NetplayId, tumbleWeedObj);
        }

        public void OnInteractableFightEnemySpawned(InteractableCharacterFight instance)
        {
            var netplayId = spawnedObjectManagerService.GetByReferenceInChildren<InteractableCharacterFight>(instance.gameObject);

            if (!netplayId.HasValue)
            {
                logger.LogWarning("InteractableCharacterFight has no id when processing OnInteractableFightEnemySpawned.");
                return;
            }

            IGameNetworkMessage message = new InteractableCharacterFightEnemySpawned
            {
                NetplayId = netplayId.Value,
            };

            udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedInteractableFightEnemySpawned(InteractableCharacterFightEnemySpawned spawned)
        {
            var spawnedObj = spawnedObjectManagerService.GetSpawnedObject(spawned.NetplayId);
            if (spawnedObj == null)
            {
                logger.LogWarning("InteractableCharacterFight not found in SpawnedObjectManagerService when processing OnReceivedInteractableFightEnemySpawned.");
                return;
            }
            var interactable = spawnedObj.GetComponentInChildren<InteractableCharacterFight>();
            interactable.SpawnEnemy();
        }

        public void OnItemAdded(EItem item)
        {
            IGameNetworkMessage message = new ItemAdded
            {
                EItem = (int)item,
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            var isServer = IsServerMode() ?? false;
            if (isServer)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedItemAdded(ItemAdded added)
        {
            var netPlayer = playerManagerService.GetNetPlayerByNetplayId(added.OwnerId);
            if (netPlayer == null)
            {
                logger.LogWarning("NetPlayer not found in PlayerManagerService when processing OnReceivedItemAdded.");
                return;
            }

            var item = (EItem)added.EItem;
            using (Plugin.SuppressOutbound())
            {
                netPlayer.AddItem(item);
            }
        }

        public void OnItemRemoved(EItem item)
        {
            IGameNetworkMessage message = new ItemRemoved
            {
                EItem = (int)item,
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            var isServer = IsServerMode() ?? false;
            if (isServer)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedItemRemoved(ItemRemoved removed)
        {
            var netPlayer = playerManagerService.GetNetPlayerByNetplayId(removed.OwnerId);
            if (netPlayer == null)
            {
                logger.LogWarning("NetPlayer not found in PlayerManagerService when processing OnReceivedItemRemoved.");
                return;
            }

            var item = (EItem)removed.EItem;
            using (Plugin.SuppressOutbound())
            {
                netPlayer.RemoveItem(item);
            }
        }

        public void OnWeaponToggled(WeaponInventory instance, EWeapon eWeapon, bool enable)
        {
            IGameNetworkMessage message = new WeaponToggled
            {
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId,
                EWeapon = (int)eWeapon,
                Enabled = enable
            };

            var isServer = IsServerMode() ?? false;
            if (isServer)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedWeaponToggled(WeaponToggled toggled)
        {
            var netPlayer = playerManagerService.GetNetPlayerByNetplayId(toggled.OwnerId);
            if (netPlayer == null)
            {
                logger.LogWarning("NetPlayer not found in PlayerManagerService when processing OnReceivedWeaponToggled.");
                return;
            }
            var weaponInventory = netPlayer.Inventory.weaponInventory;
            if (weaponInventory == null)
            {
                logger.LogWarning("WeaponInventory not found on NetPlayer when processing OnReceivedWeaponToggled.");
                return;
            }
            using (Plugin.SuppressOutbound())
            {
                netPlayer.ToggleWeapon((EWeapon)toggled.EWeapon, toggled.Enabled);
            }
        }

        public void OnSpawnedObjectInCrypt(GameObject obj)
        {
            var exist = spawnedObjectManagerService.GetByReference(obj);
            if (exist.HasValue)
            {
                return; //already registered
            }

            var netplayId = spawnedObjectManagerService.AddSpawnedObject(obj);
            DynamicData.For(obj).Set("netplayId", netplayId);

            var isCryptLeave = obj == RsgController.Instance.rsgEnd.gameObject;

            IGameNetworkMessage message = new SpawnedObjectInCrypt
            {
                NetplayId = netplayId,
                Position = Quantizer.Quantize(obj.transform.position),
                IsCryptLeave = isCryptLeave
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private void OnReceivedSpawnedObjectInCrypt(SpawnedObjectInCrypt crypt)
        {
            toUpdate.Add(crypt);
        }

        public void OnTimerStarted()
        {
            IGameNetworkMessage message = new TimerStarted
            {
                IsDungeonTimer = true,
                SenderId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedTimerStarted(TimerStarted started)
        {
            if (started.IsDungeonTimer)
            {
                Plugin.Instance.HasDungeonTimerStarted = true;
                GameManager.Instance.StartDungeonTimer();
            }
        }

        public void OnHatChanged(EHat eHat)
        {
            var localPlayer = playerManagerService.GetLocalPlayer();
            if (localPlayer == null)
            {
                return;
            }

            // Recorded as well as announced. The event tells peers who are listening now; the record
            // tells everyone who builds this avatar later, which is every level transition.
            localPlayer.Hat = (uint)eHat;
            playerManagerService.UpdatePlayer(localPlayer);

            IGameNetworkMessage message = new HatChanged
            {
                OwnerId = localPlayer.ConnectionId,
                EHat = (int)eHat
            };

            var isServer = IsServerMode() ?? false;
            if (isServer)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedHatChanged(HatChanged changed)
        {
            var netPlayer = playerManagerService.GetNetPlayerByNetplayId(changed.OwnerId);
            if (netPlayer == null)
            {
                logger.LogWarning("NetPlayer not found in PlayerManagerService when processing OnReceivedHatChanged.");
                return;
            }

            // Mirrored onto the record for the same reason the sender does it: a rebuild of this
            // avatar later reads the record, not the event that has already gone by.
            var owner = playerManagerService.GetPlayer(changed.OwnerId);
            if (owner != null)
            {
                owner.Hat = (uint)changed.EHat;
                playerManagerService.UpdatePlayer(owner);
            }

            var hatData = DataManager.Instance.GetHat((EHat)changed.EHat);

            using (Plugin.SuppressOutbound())
            {
                netPlayer.SetHat(hatData);
            }
        }

        public void OnSkinSelected(SkinData skinData)
        {
            var localPlayer = playerManagerService.GetLocalPlayer();

            if (localPlayer == null) return;

            localPlayer.Skin = skinData.name;
            playerManagerService.UpdatePlayer(localPlayer);
        }

        public void OnRespawn(uint ownerId, Vector3 position)
        {
            var netplayer = playerManagerService.GetNetPlayerByNetplayId(ownerId);
            if (netplayer != null)
            {
                netplayer.Respawn(position);
                var player = playerManagerService.GetPlayer(ownerId);
                player.Hp = player.MaxHp;
                playerManagerService.UpdatePlayer(player);
            }
            else
            {
                ReviveLocalPlayer(position);
            }

            IGameNetworkMessage message = new PlayerRespawned
            {
                OwnerId = ownerId,
                Position = Quantizer.Quantize(position)
            };

            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }


        private void OnReceivedPlayerRespawned(PlayerRespawned respawned)
        {
            var netplayer = playerManagerService.GetNetPlayerByNetplayId(respawned.OwnerId);
            if (netplayer != null)
            {
                netplayer.Respawn(Quantizer.Dequantize(respawned.Position));
            }
            else
            {
                ReviveLocalPlayer(Quantizer.Dequantize(respawned.Position));
            }
        }

        /// <summary>
        /// OB-20: the one place that revives the local player, to full HP. <paramref name="position"/>
        /// is null at a level transition, where the game places the player itself.
        /// </summary>
        private void ReviveLocalPlayer(Vector3? position)
        {
            var player = GameManager.Instance.player;

            if (position.HasValue)
            {
                player.transform.position = position.Value;

                // OB-49: the Rigidbody must move too, or the next physics step puts the player back.
                var rb = player.playerMovement?.rb;
                if (rb != null)
                {
                    rb.position = position.Value;
                    rb.velocity = Vector3.zero;
                }

                logger.LogInfo($"[revive] OB-49: respawned the local player at {position.Value}.");
            }

            player.inventory.playerHealth.hp = player.inventory.playerHealth.maxHp;

            var localPlayer = playerManagerService.GetLocalPlayer();
            if (localPlayer != null)
            {
                localPlayer.Hp = localPlayer.MaxHp;
                playerManagerService.UpdatePlayer(localPlayer);
            }

            Plugin.Instance.CameraSwitcher.ResetToLocalPlayer();

            player.playerRenderer.gameObject.SetActive(true);
        }

        /// <summary>
        /// OB-20: a dead peer revives itself at the portal; no message needed, HP reaches others via PlayerUpdate. Only if dead.
        /// </summary>
        private void ReviveLocalPlayerIfDeadForNextLevel()
        {
            try
            {
                var player = GameManager.Instance?.player;
                if (player == null || player.inventory == null || player.inventory.playerHealth == null)
                {
                    return;
                }

                if (player.inventory.playerHealth.hp > 0)
                {
                    return;
                }

                ReviveLocalPlayer(null);
                logger.LogInfo("OB-20: local player was dead at the portal; revived for the next level.");
            }
            catch (System.Exception ex)
            {
                logger.LogWarning($"OB-20: could not revive the local player at the transition: {ex.Message}");
            }
        }

        public bool IsSharedExperienceEnabled()
        {
            var sharedExperienceEnabled = Plugin.Instance.Mode.EnabledSharedExperience;
            if (sharedExperienceEnabled.HasValue)
            {
                return sharedExperienceEnabled.Value;
            }

            return false;
        }

        public void PlayerXpAddXp(int xp, int amount, float leftOverXp)
        {
            IGameNetworkMessage message = new AddXp
            {
                Xp = xp,
                Amount = amount,
                LeftOverXp = leftOverXp,
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId,
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);

            }
        }

        /// <summary>Shared XP that arrived while this peer had no player (stage load); added with the next gain.</summary>
        private int pendingSharedXp;

        /// <summary>
        /// Shared XP: adds the sender's post-multiplier GAIN to ours. Gains commute; overwriting
        /// totals lost in-flight pickups (PlayerXp.AddXp, VA 0x1803C5440, only raises the level).
        /// <para>Wire-semantics change: <c>Amount</c> is a gain, not a total. Older builds are incompatible.</para>
        /// </summary>
        private void OnReceivedAddXp(AddXp xp)
        {
            // SE-11 shape: never apply our own gain twice if it comes back.
            var localPlayer = playerManagerService.GetLocalPlayer();
            if (localPlayer != null && xp.OwnerId == localPlayer.ConnectionId)
            {
                return;
            }

            if (xp.Amount <= 0)
            {
                return;
            }

            // No player yet (scene loading): hold the gain.
            var playerXp = GameManager.Instance?.player?.inventory?.playerXp;
            if (playerXp == null)
            {
                pendingSharedXp += xp.Amount;
                return;
            }

            var amount = xp.Amount + pendingSharedXp;
            pendingSharedXp = 0;

            using (Plugin.SuppressOutbound())
            {
                playerXp.xp += amount;

                // OB-31: raise to the sender's total if ours is lower. It can never exceed the true
                // shared total, so this cannot over-count; it repairs gains that were never shared.
                if (xp.Xp > playerXp.xp)
                {
                    Plugin.Log.LogInfo($"[xp] behind the shared total by {xp.Xp - playerXp.xp}; raising {playerXp.xp} -> {xp.Xp} (level {playerXp.level}).");
                    playerXp.xp = xp.Xp;
                }

                // Amount 0 adds nothing; it is what runs the level-up check against the new total.
                playerXp.AddXp(0);
            }
        }

        public void RewardFinished()
        {
            // Failsafe clock starts on report; some callers never open an encounter window.
            encounterService.BeginWaiting();

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // SE-5: earliest point any barrier message can exist.
                encounterService.EnsureSession();

                encounterService.AddClosedEncounterForPlayer(playerManagerService.GetLocalPlayer().ConnectionId);

                if (encounterService.IsClosable())
                {
                    ReleaseBarrier();
                }
            }
            else
            {
                IGameNetworkMessage message = new EncounterClosedStamped
                {
                    OwnerId = playerManagerService.GetLocalPlayer().ConnectionId,
                    SessionId = encounterService.SessionId,
                    RoundId = encounterService.RoundId,
                };

                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        /// <summary>Host only. The single place a round release is stamped, broadcast and applied locally.</summary>
        private void ReleaseBarrier()
        {
            encounterService.EnsureSession();

            var sessionId = encounterService.SessionId;
            var roundId = encounterService.RoundId;

            IGameNetworkMessage closeMessage = new CloseEncounterStamped
            {
                SessionId = sessionId,
                RoundId = roundId,
            };

            udpClientService.SendToAllClients(closeMessage, NetDelivery.ReliableOrdered);

            // Logged on success too, so a healthy barrier is verifiable in play. Not a hot path.
            logger.LogInfo($"[barrier] Released round {roundId} (session {sessionId}) to all clients.");

            // Same gate the clients use.
            if (encounterService.TryApplyRelease(sessionId, roundId))
            {
                OnCloseEncounter();
            }
        }

        /// <summary>SE-5 / OB-4. Drops a release for an already applied round, so it cannot close a fresh window.</summary>
        private void OnReceivedCloseEncounterStamped(CloseEncounterStamped close)
        {
            if (!encounterService.TryApplyRelease(close.SessionId, close.RoundId))
            {
                logger.LogInfo(
                    $"[barrier] Ignoring a stale release (session {close.SessionId}, round {close.RoundId}); " +
                    $"this peer is on session {encounterService.SessionId}, round {encounterService.RoundId}.");
                return;
            }

            logger.LogInfo($"[barrier] Applied release for round {close.RoundId} (session {close.SessionId}).");

            encounterService.Close();
            OnCloseEncounter();
        }

        /// <summary>Legacy, for builds without union tag 70. Unstamped (SE-5 hazard); this build never sends it.</summary>
        private void OnReceivedCloseEncounter(CloseEncounter close)
        {
            logger.LogWarning(
                "Received an unstamped CloseEncounter. The sending peer is on an older build; the " +
                "barrier cannot be round-attributed for this release (SE-5).");

            encounterService.Close();
            OnCloseEncounter();
        }

        private void OnCloseEncounter()
        {
            // Guarded: from a network callback the UI can be mid-teardown.
            var encounterWindows = UiManager.Instance == null ? null : UiManager.Instance.encounterWindows;

            // OB-21: clear the spent round FIRST; the branch below can create next-round state that
            // a later clear would wipe (a permanent stall).
            encounterService.ClearClosedEncounters();

            if (encounterWindows != null && encounterWindows.encounterInProgress)
            {
                // Forced so the re-entrant RewardFinished_Prefix closes the window instead of reporting again.
                encounterService.Close();

                encounterWindows.RewardFinished();

                // OB-21: the force flag must not outlive this release.
                encounterService.Unclose();
            }
            else
            {
                MyTime.Unpause();
            }
            //EncounterWindows.A_WindowClosed.Invoke();
        }

        /// <summary>
        /// Failsafe release for a stuck barrier. A client also re-reports, since the likeliest cause is the host never counting its report.
        /// </summary>
        public void ForceCloseEncounter(string reason)
        {
            var waited = encounterService.WaitedSeconds;
            var isHost = IsServerMode() ?? false;

            logger.LogWarning(
                $"Shared-experience failsafe fired after {waited:F1}s ({reason}). Releasing the " +
                "encounter barrier locally" + (isHost ? " and for every client." : " and re-reporting to the host."));

            try
            {
                if (isHost)
                {
                    // Stamped (OB-4), so peers past this round drop it.
                    ReleaseBarrier();
                    return;
                }

                var localPlayer = playerManagerService.GetLocalPlayer();
                if (localPlayer != null)
                {
                    IGameNetworkMessage message = new EncounterClosedStamped
                    {
                        OwnerId = localPlayer.ConnectionId,
                        SessionId = encounterService.SessionId,
                        RoundId = encounterService.RoundId,
                    };

                    udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
                }
            }
            catch (Exception ex)
            {
                // Telling the others is best effort; unsticking this peer is not.
                logger.LogWarning($"Failsafe could not notify peers: {ex.Message}");
            }

            encounterService.Close();
            OnCloseEncounter();
        }

        public void OnChangeGold(int amount)
        {
            IGameNetworkMessage message = new GoldChanged
            {
                Amount = amount,
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        /// <summary>Lets the ChargeShrine.Complete prefix pass the host's completion through. Main thread only.</summary>
        public static bool IsApplyingHostShrineCompletion { get; private set; }

        public void OnChargeShrineCompleted(ChargeShrine shrine)
        {
            if (!(IsServerMode() ?? false) || shrine == null)
            {
                return;
            }

            var shrineNetplayId = MonoMod.Utils.DynamicData.For(shrine.gameObject).Get<uint?>("netplayId");
            if (!shrineNetplayId.HasValue)
            {
                logger.LogWarning("OB-6: a charge shrine completed with no netplay id; clients cannot be told.");
                return;
            }

            IGameNetworkMessage message = new ChargeShrineCompleted { ShrineNetplayId = shrineNetplayId.Value };
            udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            logger.LogInfo($"OB-6: shrine {shrineNetplayId.Value} completed on the host; told every client.");
        }

        /// <summary>Shrines this client has already asked about, so a request is sent once.</summary>
        private readonly HashSet<uint> requestedShrineCompletions = [];

        public void RequestChargeShrineCompletion(ChargeShrine shrine)
        {
            if ((IsServerMode() ?? true) || shrine == null)
            {
                return;
            }

            var shrineNetplayId = MonoMod.Utils.DynamicData.For(shrine.gameObject).Get<uint?>("netplayId");
            if (!shrineNetplayId.HasValue || !requestedShrineCompletions.Add(shrineNetplayId.Value))
            {
                return;
            }

            IGameNetworkMessage message = new ChargeShrineCompleted { ShrineNetplayId = shrineNetplayId.Value };
            udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            logger.LogInfo($"OB-6: shrine {shrineNetplayId.Value} filled here first; asked the host to complete it.");
        }

        private void OnReceivedChargeShrineCompleted(ChargeShrineCompleted message)
        {
            var shrineObj = spawnedObjectManagerService.GetSpawnedObject(message.ShrineNetplayId);
            var shrine = shrineObj == null ? null : shrineObj.GetComponent<ChargeShrine>();
            if (shrine == null)
            {
                logger.LogWarning($"OB-6: completion for unknown shrine {message.ShrineNetplayId} ignored.");
                return;
            }

            if (shrine.completed)
            {
                return;
            }

            // Full bar, so ChargeShrine.Update stops charging.
            shrine.chargeProgress = 1f;

            if (IsServerMode() ?? false)
            {
                // A client filled first. Not suppressed: the Complete postfix broadcasts to everyone.
                shrine.Complete();
                logger.LogInfo($"OB-6: completed shrine {message.ShrineNetplayId} because a client's bar filled first.");
                return;
            }

            IsApplyingHostShrineCompletion = true;
            try
            {
                using (Plugin.SuppressOutbound())
                {
                    shrine.Complete();
                }
            }
            finally
            {
                IsApplyingHostShrineCompletion = false;
            }

            logger.LogInfo($"OB-6: completed shrine {message.ShrineNetplayId} on the host's word.");
        }

        private void OnReceivedChangeGold(GoldChanged changed)
        {
            // SE-11: ignore our own delta echoed back; the relay's exclude filter may be empty
            // (UNVERIFIED, RelayEnvelope.ToFilters). A doubled delta diverges permanently.
            var localPlayer = playerManagerService.GetLocalPlayer();
            if (localPlayer != null && changed.OwnerId == localPlayer.ConnectionId)
            {
                return;
            }

            // Guarded: network callback during teardown or stage change.
            var inventory = GameManager.Instance == null || GameManager.Instance.player == null
                ? null
                : GameManager.Instance.player.inventory;

            if (inventory == null)
            {
                return;
            }

            using (Plugin.SuppressOutbound())
            {
                inventory.ChangeGold(changed.Amount);
            }
        }
    }
}

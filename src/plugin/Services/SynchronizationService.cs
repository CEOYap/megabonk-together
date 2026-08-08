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
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Extensions;
using MegabonkTogether.Helpers;
using MegabonkTogether.Patches;
using MegabonkTogether.Scripts.Interactables;
using MegabonkTogether.Scripts.Snapshot;
using MonoMod.Utils;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace MegabonkTogether.Services
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
        public void OnChangeGold(int amount);
    }
    internal class SynchronizationService : ISynchronizationService
    {
        private readonly IUdpClientService udpClientService;
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
        private readonly ITrackerService trackerService;
        private readonly ManualLogSource logger;
        private readonly ConcurrentBag<SpawnedObject> toSpawns = [];
        private readonly ConcurrentBag<SpawnedObjectInCrypt> toUpdate = [];
        private readonly ConcurrentQueue<SpawnedEnemy> pendingEnemySpawns = new();
        private readonly HashSet<uint> enemiesDiedBeforeSpawn = [];
        private readonly Dictionary<EEnemy, Material> clonedExtraEnemyMaterials = [];
        private Coroutine newObjectToSpawnRoutine;
        private Coroutine pendingEnemySpawnRoutine;
        private Coroutine readyRetryRoutine;

        /// <summary>Invalidates a retry routine left over from a previous round. See <see cref="ClientReadyRoutine"/>.</summary>
        private int readyGeneration;

        /// <summary>Lobby-ready defect A. See <see cref="ClientReadyRoutine"/>.</summary>
        private const float ReadyRetrySeconds = 2f;

        /// <summary>~30 s of re-reporting, then it gives up and says who.</summary>
        private const int ReadyRetryAttempts = 15;
        private const int MAX_ENEMY_SPAWNS_PER_FRAME = 32;
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
            IUdpClientService udpClientService,
            IProjectileManagerService projectileManagerService,
            IPickupManagerService pickupManagerService,
            IChestManagerService chestManagerService,
            ISpawnedObjectManagerService spawnedObjectManagerService,
            IFinalBossOrbManagerService finalBossOrbManagerService,
            IGameBalanceService gameBalanceService,
            IEncounterService encounterService,
            IReadinessService readinessService,
            ITrackerService trackerService
            )
        {
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
            this.trackerService = trackerService;
            this.logger = logger;

            EventManager.SubscribeSpawnedObjectsEvents(OnNewObjectToSpawn);
            EventManager.SubscribePlayerUpdatesEvents(OnPlayerUpdate);
            EventManager.SubscribeSpawnedEnemyEvents(OnReceivedSpawnedEnemy);
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

            cancellationToken = cancellationTokenSource.Token;
            this.udpClientService = udpClientService;
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

            // The host reads the barrier, not the replicated flag. GetAllPlayers().All(p => p.IsReady)
            // asks a field that ResetForNextLevel clears and the full player record overwrites —
            // defects B and C — so it could answer "not ready" about a peer whose report the host is
            // actually holding, or the reverse. The barrier's set is the only copy nothing
            // replicates over.
            if (IsServerMode() ?? false)
            {
                return readinessService.AreAllParticipantsReady() && udpClientService.HasAllPeersConnected();
            }

            return playerManagerService.GetAllPlayers().All(p => p.IsReady) && udpClientService.HasAllPeersConnected();
        }

        public bool? IsServerMode()
        {
            return udpClientService.IsHost();
        }

        public void Reset()
        {
            currentState = State.None;
            toSpawns.Clear();
            toUpdate.Clear();

            shrineChargingPlayers.Clear();
            pylonChargingPlayers.Clear();

            // Same reason as the barrier state below: a session that ended after a boss died would
            // otherwise leave this latched, and the next session's first portal would never open.
            hasHandledBossDefeatedThisStage = false;

            // The barrier used to survive teardown: closedEncounterPerPlayer and forceClose were
            // cleared only by a successful release, so a session that ended mid-encounter poisoned
            // the next one — the next round would either release instantly without anyone
            // reporting, or never release at all. Matches "requires a game restart to recover".
            // Defect A's retry is per lobby. Left running across a teardown it would keep reporting
            // readiness for a connection id the next session may reuse. The generation bump is what
            // actually stops it: Stop() and the field clear are two statements and the routine
            // yields, so the guard inside it is the reliable half.
            readyGeneration++;
            CoroutineRunner.Instance.Stop(readyRetryRoutine);
            readyRetryRoutine = null;
            readinessService.ResetSession();

            encounterService.ClearClosedEncounters();

            // SE-5: round state is per run, not per round, so ClearClosedEncounters deliberately
            // leaves it alone and teardown is the only place it is dropped. Without this the next
            // run would start at round 0 again and accept a message left in flight across the
            // boundary as though it named the new run's first round.
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

            var netplayer = playerManagerService.GetNetPlayerByNetplayId(playerUpdate.ConnectionId);

            if (netplayer == null)
            {
                logger.LogWarning($"NetPlayer not found for ConnectionId: {playerUpdate.ConnectionId}");
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

            // Awake runs synchronously inside Instantiate, and Instantiate clones the source's
            // active state — so with an active prefab every component's Awake fired while the
            // clone still sat at the prefab's authored position, before the three lines below
            // moved it. Anything caching a transform in Awake therefore cached prefab
            // coordinates, identically for every clone.
            //
            // That is what the charge shrine's rune stone did: on a client the game wrote every
            // shrine's stone to the same constant world position (281.15, 16.60, -67.16) —
            // unchanged across shrines, maps and runs — while on the host, whose shrines are
            // built in place by the tile generator, it wrote each shrine's own correct position.
            // The stack on those writes carried no mod frames, so the game was the writer and the
            // wrong input was ours. The shrine prefab's authored position is
            // (281.15, 12.36, -67.16), and the stone's local offset is (0, 4.23, 0) — the constant
            // to the decimal, which is what confirmed this rather than merely fitting it.
            //
            // The shrine was the visible symptom; the defect is in this spawn path, so any prefab
            // with a component that caches a transform in Awake was affected the same way.
            //
            // Creating the clone inactive and activating it after the transform is set means every
            // Awake sees the final position. The prefab's active state is restored in a finally:
            // it is shared mutable state around a call that can throw, which is P0-6's lesson.
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

            // Activated here, before the component lookups below: GetComponentInChildren without
            // the includeInactive flag does not see components on an inactive hierarchy, so the
            // shady-guy and microwave rarity assignments would silently stop working otherwise.
            // Awake still runs after the transform is final, which is the whole point.
            //
            // The desert-grave chain is exempt because HandleSpawn deliberately leaves those
            // inactive to be revealed later by the grave sequence.
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

        private bool UpdateObject(SpawnedObjectInCrypt toUpdate)
        {
            var allPieces = RsgController.Instance.allPieces;
            var position = toUpdate.Position;

            if (toUpdate.IsCryptLeave)
            {
                spawnedObjectManagerService.SetSpawnedObject(toUpdate.NetplayId, RsgController.Instance.rsgEnd.gameObject);
                DynamicData.For(RsgController.Instance.rsgEnd.gameObject).Set("netplayId", toUpdate.NetplayId);
                return true;
            }

            foreach (var piece in allPieces)
            {
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

        /// <summary>
        /// The desert-grave chain: <see cref="HandleSpawn"/> deliberately deactivates these so the
        /// grave sequence can reveal them later, so <see cref="SpawnObject"/> must not activate
        /// them. Shared with that method rather than duplicating the name test, so the two cannot
        /// drift apart.
        /// </summary>
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
                var spawnedPlayer = playerManagerService.GetNetPlayerByNetplayId(netPlayer.ConnectionId);
                var playerColor = Plugin.Instance.NetPlayersDisplayer.GetPlayerColor(netPlayer.ConnectionId);
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
                        // Host only. Readiness is host-authoritative — that is the whole premise of
                        // ReadinessService — and the client must NOT write its own flag here.
                        //
                        // It used to, and that silently broke the retry: ClientReadyRoutine decides
                        // "the host has acknowledged me" by reading this same record, so a client
                        // that set the flag itself moments earlier could not tell the host's answer
                        // from its own write. The routine then exited on its first wait iteration
                        // having sent nothing at all, and the lobby hung forever with no failsafe on
                        // this path.
                        //
                        // It only surfaced when the host's round-start arrived *after* the routine
                        // started — a race the internet sessions happened to always win and two
                        // instances on one PC lost. The client's flag now becomes true only when the
                        // host's replicated record says so, which is also what makes
                        // IsLobbyReady()'s All(p => p.IsReady) mean something on a client.
                        player.IsReady = true;
                        playerManagerService.UpdatePlayer(player);

                        // The host opens the round and names it. Participants are captured here,
                        // once, rather than counted live on every check — a live count moves under
                        // the barrier, which is the same defect shape as SE-6 next door.
                        var participants = playerManagerService.GetAllPlayers().Select(p => p.ConnectionId).ToList();
                        var stamp = readinessService.OpenRound(participants);

                        // The host is a participant and reports by calling in directly; there is no
                        // message for it to send to itself.
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

                    // Close the round before ResetForNextLevel clears the mirrored flags, so the
                    // barrier cannot report the *previous* level's round as satisfied while the
                    // transition is in flight. The next round is opened, and stamped, when this peer
                    // reaches Ready on the new level.
                    readinessService.CloseRound();

                    playerManagerService.ResetForNextLevel();
                    PrepareForNextLevel();
                    Plugin.Instance.ClearPrefabs();
                    Plugin.Instance.RestoreDeath(false);
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
            // A stage change ends any round in flight — the encounter windows are torn down with
            // the stage, so a report that arrives after this point belongs to nothing.
            encounterService.ClearClosedEncounters();

            // Each stage has its own boss and portal, so the once-per-stage guard reopens here.
            hasHandledBossDefeatedThisStage = false;

            spawnedObjectManagerService.ResetForNextLevel();
            enemyManagerService.ResetForNextLevel();
            projectileManagerService.ResetForNextLevel();
            pickupManagerService.ResetForNextLevel();
            chestManagerService.ResetForNextLevel();
            specificDesertGraves.Clear();
            currentCoffin = null;

            pendingEnemySpawns.Clear();
            enemiesDiedBeforeSpawn.Clear();
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
        /// Host. Announces the open readiness round so clients learn the stamp they must report
        /// against.
        ///
        /// <para>Sent once when the round opens, and again whenever a report arrives naming the
        /// wrong round — which almost always means that peer has not been told about this one yet.
        /// Re-announcing is cheaper than letting the peer burn its retry budget against a stamp it
        /// cannot have.</para>
        ///
        /// <para>The re-announce is a broadcast, as it is in the reference implementation. Targeting
        /// only the peers that owe a report would need a connection-id-to-<c>NetPeer</c> lookup
        /// plumbed through the send path, and that is precisely the mapping Phase 1 of the
        /// Steamworks migration rewrites — not worth building twice. What it does do that the
        /// reference does not is <b>name the outstanding peers in the log</b>, so a lobby that will
        /// not start says who it is waiting for instead of just that it is waiting.</para>
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
        /// Client. Adopts the host's stamp for a new readiness round.
        ///
        /// <para>Arriving here <i>before</i> this peer has reached its own Ready state is normal and
        /// is the point: the stamp is held, and <see cref="ClientReadyRoutine"/> picks it up when it
        /// starts. That ordering is lobby-ready defect D's consequence for readiness —
        /// <c>NetworkHandler.Update</c> returns before <c>Poll()</c> while
        /// <c>IsLoadingNextLevel</c>, so the round start is read off the socket late, in a burst,
        /// often after the local transition has finished. A stamp that is stored rather than acted
        /// on immediately does not care when it arrives.</para>
        /// </summary>
        private void OnReceivedReadinessRoundStarted(ReadinessRoundStarted started)
        {
            if (readinessService.AdoptStamp(started.SessionId, started.RoundId))
            {
                logger.LogInfo($"[readiness] Adopted round {started.RoundId} (session {started.SessionId}).");
            }
        }

        /// <summary>
        /// Lobby-ready defect A. <c>ClientInGameReady</c> was sent exactly once, with no retry: if
        /// the host did not end up holding that single report, the client sat on "waiting for other
        /// player(s)" forever and the run could not start.
        ///
        /// <para><b>The channel being reliable is not the same as the report being held.</b>
        /// <c>ReliableOrdered</c> guarantees LiteNetLib delivers the bytes; it says nothing about
        /// whether the host still believes it afterwards. Two ways it does not, both of them races
        /// this peer cannot observe: the host clears readiness in <c>ResetForNextLevel</c> after
        /// recording an early report (defect B), and the host's replicated record overwrites the
        /// flag (defect C). Retry repairs both, which is why the fix is retry rather than a stronger
        /// channel.</para>
        ///
        /// <para><b>What changed with the round stamp.</b> Retry alone would have re-sent the same
        /// unaddressed report, and an early one is indistinguishable from a current one — so the
        /// host could still settle on a report for the transition it was about to discard. Reporting
        /// against a host-assigned <c>(SessionId, RoundId)</c> means an early report is rejected
        /// rather than banked, and the retry is what then delivers the correct one. Neither half
        /// works without the other; that pairing is the substance of the reference implementation
        /// and it is why these were done as one change.</para>
        ///
        /// <para><b>The acknowledgement is the host's own roster.</b> No ack message: the host
        /// already replicates <c>IsReady</c> and force-sends a full record the instant it changes
        /// (<c>UdpClientService.HasReadinessChanged</c>), so "the host is holding my report" is
        /// observable in state this peer already has. The reference needs an
        /// <c>AllPlayersReadyForSpawn</c> message plus a targeted re-send of it to answer a retrying
        /// client; we get the same convergence from machinery that already exists.</para>
        ///
        /// <para><b>Idempotent by construction.</b> The host's handler is a set insert keyed on
        /// connection id, so N copies and one copy are the same.</para>
        ///
        /// <para><b>Generation-guarded.</b> A transition that starts a new round while an older
        /// routine is mid-wait would otherwise have two coroutines reporting against two stamps.
        /// <c>CoroutineRunner.Stop</c> alone is not enough — the stop and the restart are two
        /// statements, and this routine yields between them.</para>
        ///
        /// <para><b>Bounded, and it says who.</b> Gives up after <see cref="ReadyRetryAttempts"/>
        /// attempts and logs it. The reference logs a bare boolean on its equivalent timeout;
        /// naming the peer is the difference between a line you can act on and one you cannot.</para>
        ///
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

            for (var attempt = 1; attempt <= ReadyRetryAttempts; attempt++)
            {
                if (generation != readyGeneration || currentState != State.Ready)
                {
                    yield break;
                }

                // Nothing to report against yet. The host's round start may still be queued behind
                // a level load — see OnReceivedReadinessRoundStarted — so this waits rather than
                // sending an unaddressable report the host would only reject.
                if (readinessService.HasStamp)
                {
                    IGameNetworkMessage message = new ClientReadyStamped
                    {
                        ConnectionId = connectionId,
                        SessionId = readinessService.SessionId,
                        RoundId = readinessService.RoundId,
                    };

                    udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);

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

                // Hand-accumulated against unscaledDeltaTime rather than WaitForSecondsRealtime.
                // WaitForSeconds would not advance at timeScale 0, which the lobby-ready screen can
                // be, silently turning the retry into a no-op — and WaitForSecondsRealtime is used
                // nowhere else in this plugin, so whether BepInEx's IL2CPP coroutine driver handles
                // it is unverified. `yield return null` is the form this codebase already relies on
                // in twelve places.
                var waited = 0f;
                while (waited < ReadyRetrySeconds)
                {
                    yield return null;
                    waited += Time.unscaledDeltaTime;

                    if (generation != readyGeneration || currentState != State.Ready)
                    {
                        yield break;
                    }

                    // Read fresh each time: the record is replaced wholesale by the host's full
                    // player broadcast, so a reference captured earlier can be stale. Checked inside
                    // the wait, not only after it — acknowledgement usually arrives well within the
                    // interval, and stopping promptly keeps a redundant re-report off the wire.
                    var acknowledged = playerManagerService.GetPlayer(connectionId);
                    if (acknowledged != null && acknowledged.IsReady && readinessService.HasStamp)
                    {
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
                            if (!SpawnObject(toSpawn))
                            {
                                unspawnedYet.Add(toSpawn);
                            }
                        }
                        canSpawn = toSpawns.Count > 0;
                    }

                    foreach (var item in unspawnedYet) //Add back to retry later
                    {
                        logger.LogWarning($"Retrying spawn for object: {item.PrefabName}");
                        toSpawns.Add(item);
                    }

                    var canUpdate = toUpdate.Count > 0;
                    var unupdatedYet = new List<SpawnedObjectInCrypt>();
                    while (canUpdate)
                    {
                        if (toUpdate.TryTake(out var toUpd))
                        {
                            var success = UpdateObject(toUpd);
                            if (!success)
                            {
                                unupdatedYet.Add(toUpd);
                            }
                        }
                        canUpdate = toUpdate.Count > 0;
                    }

                    foreach (var item in unupdatedYet) //Add back to retry later
                    {
                        logger.LogWarning($"Retrying update for object in crypt at position: x:{item.Position.QuantizedX} y:{item.Position.QuantizedY} z:{item.Position.QuantizedZ}");
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
        }

        private void OnReceivedSpawnedEnemy(SpawnedEnemy spawnedEnemy)
        {
            if (!HasNetplaySessionStarted())
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

            // FIX 4/6: the reviver block used to sit here, BEFORE the registration at the end of
            // this method. It has moved below that registration — see the comment there.

            enemy.hp = spawnedEnemy.Hp;
            enemy.controlHp = spawnedEnemy.Hp;
            enemy.maxHp = spawnedEnemy.Hp;
            enemy._hp_k__BackingField = spawnedEnemy.Hp;

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
                if (enemy.enemyData.enemyName == EEnemy.GhostGrave1
                || enemy.enemyData.enemyName == EEnemy.GhostGrave2
                || enemy.enemyData.enemyName == EEnemy.GhostGrave3
                || enemy.enemyData.enemyName == EEnemy.GhostGrave4)
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

            DynamicData.For(enemy).Set("targetId", spawnedEnemy.TargetId);

            // FIX 3/6 + 4/6: reviver naming, moved to AFTER registration and isolated.
            //
            // This block used to run before the three lines above, with two unguarded
            // dereferences: GetSpawnedObject() can return null before .GetComponent<>(), and it
            // used `reviver?.` on one line then bare `reviver.` on the next. When a reviver's
            // owner had disconnected, GetFullName() threw, the whole handler aborted here, and
            // the enemy was therefore never given an EnemyInterpolator and never entered the
            // registry — so it stood still while remaining solid and damaging on contact.
            // A 3-player session lost 581 consecutive enemy spawns this way.
            //
            // The naming is cosmetic; registration is not. Ordering and the catch make it
            // impossible for the former to cost the latter again.
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

            // FIX P2-5: the owner is resolved first and handed to the manager, which had no way of
            // knowing it before — the same id already goes out on the message below.
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
                    message = new SpawnedSniperProjectile
                    {
                        Id = netplayId,
                        OwnerId = ownerId,
                        Rotation = Quantizer.Quantize(instance.transform.eulerAngles),
                        Position = Quantizer.Quantize(instance.transform.position),
                        Weapon = (int)instance.weaponBase.weaponData.eWeapon,
                        MuzzlePosition = Quantizer.Quantize(sniperProj.weaponAttack.muzzle.transform.position),
                        MuzzleRotation = Quantizer.Quantize(sniperProj.weaponAttack.muzzle.transform.eulerAngles),
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
                    // GetNetPlayerByNetplayId returns null once the owner has left, and this line
                    // used to dereference it straight into .Inventory. Projectiles from a departing
                    // peer keep arriving for a moment after their NetPlayer is destroyed — Run A
                    // produced about ten of these per disconnect, every one an NRE caught by the
                    // catch below and logged with a full stack. Dropping the projectile is the
                    // correct outcome anyway: there is no owner left to attribute or model it to.
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


                    // FIX P2-5 (remainder): the owner travels with the registration now, so these
                    // can be dropped when that peer leaves. Same id the DynamicData stamp above uses.
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

            IGameNetworkMessage message = new EnemyDamaged
            {
                EnemyId = enemySpawned.Key,
                Damage = damageContainer.damage,
                DamageEffect = (int)damageContainer.damageEffect,
                DamageBlockedByArmor = damageContainer.damageBlockedByArmor,
                DamageSource = DamageSourceHelper.Normalize(damageContainer.damageSource),
                DamageIsCrit = damageContainer.crit,
                DamageProcCoefficient = damageContainer.procCoefficient,
                DamageElement = (int)damageContainer.element,
                DamageFlags = (int)damageContainer.flags,
                DamageKnockback = damageContainer.knockback,
                AttackerId = playerManagerService.GetLocalPlayer().ConnectionId
            };

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                udpClientService.SendToAllClients(message, NetDelivery.Unreliable); //TODO: Can be unreliable i think ?
            }
            else
            {
                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedEnemyDamaged(EnemyDamaged damaged)
        {
            var enemy = enemyManagerService.GetEnemyById(damaged.EnemyId);
            if (enemy == null)
            {
                return; //Might already be dead
            }

            // Normalized because a null source makes RunStats.OnEnemyDamaged throw inside
            // enemy.Damage below, which aborts this handler and silently drops the damage on this
            // peer. See DamageSourceHelper.
            var damageSource = DamageSourceHelper.Normalize(damaged.DamageSource);

            var damageContainer = new DamageContainer(damaged.DamageProcCoefficient, damageSource)
            {
                damage = damaged.Damage,
                damageEffect = (EDamageEffect)damaged.DamageEffect,
                damageBlockedByArmor = damaged.DamageBlockedByArmor,
                crit = damaged.DamageIsCrit,
                element = (EElement)damaged.DamageElement,
                flags = (DcFlags)damaged.DamageFlags,
                knockback = damaged.DamageKnockback,
                damageSource = damageSource
            };

            // Three globals are opened around one game call that can throw, and all three were
            // restored only on the success path. The worst is CAN_DAMAGE_ENEMIES: Enemy.Damage_Prefix
            // blocks all client-side enemy damage unless it is set, so one throw in enemy.Damage
            // leaves it latched true and the client resolves enemy damage locally for the rest of
            // the run — a permanent, silent divergence from a single exception.
            //
            // Same shape as P1-10 (28 CAN_SEND_MESSAGES latches, now Plugin.SuppressOutbound) and
            // P0-6 (one throw latched two statics through 581 enemy spawns). The other two leak
            // into P1-11's stranded position requests and P1-5's kill-attribution counters.
            Plugin.Instance.CAN_DAMAGE_ENEMIES = true;
            playerManagerService.AddGetNetplayerPositionRequest(damaged.AttackerId);
            trackerService.SetCurrentPlayerId(damaged.AttackerId);

            try
            {
                enemy.Damage(damageContainer);
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
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
            }
            else
            {
                if (enemy.IsStageBoss() && !enemy.IsFinalBoss()) //Manually invoke boss defeated event client side
                {
                    OnBossDefeated();
                }

                udpClientService.SendToHost(message, NetDelivery.ReliableOrdered);
            }
        }

        private void OnReceivedEnemyDied(EnemyDied died)
        {
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
                trackerService.SetCurrentPlayerId(died.DiedByOwnerId);

                enemy.EnemyDied(damageContainer);

                trackerService.UnsetCurrentPlayerId();
            }

            var isHost = IsServerMode() ?? false;
            if (!isHost)
            {
                if (playerManagerService.IsLocalConnectionId(died.DiedByOwnerId))
                {
                    foreach (var item in GameManager.Instance.player.inventory.itemInventory.items)
                    {
                        var actualItem = item.Value;
                        actualItem.ProcOnHitEffects(damageContainer);
                    }
                }

                if (enemy.IsStageBoss() && !enemy.IsFinalBoss()) //Manually invoke boss defeated event client side
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
        /// Manually invoke boss defeated event client side
        /// </summary>
        /// <summary>
        /// Manually invoke boss defeated event client side. Idempotent per stage.
        ///
        /// <para>A client that kills the stage boss itself reaches this twice: once from its own
        /// <c>OnEnemyDied</c> before it forwards to the host, and again when the host's
        /// <c>EnemyDied</c> broadcast comes back. Both call sites are deliberate — the local one
        /// keeps the portal responsive without a round trip — so the guard belongs here rather
        /// than at either of them.</para>
        ///
        /// <para>The duplicate was already known, but as a symptom rather than a cause: the
        /// <c>arrowDict.Clear()</c> below carries the comment "Prevent sometimes double add for
        /// portal arrow". That cleared the duplicated minimap arrow and left the rest of the
        /// handler running twice, including <c>A_BossDefeated.Invoke</c>, which is a game event.
        /// The <c>Clear()</c> stays — it is harmless and something else may rely on it — but it is
        /// no longer what stops the double-add.</para>
        ///
        /// <para>Reset in <see cref="PrepareForNextLevel"/> and <see cref="Reset"/>, since each
        /// stage has its own boss and portal and per-run state must not cross a session (SE-2).</para>
        /// </summary>
        private void OnBossDefeated()
        {
            if (hasHandledBossDefeatedThisStage)
            {
                return;
            }
            hasHandledBossDefeatedThisStage = true;

            logger.LogInfo("Boss defeated, activating portal.");
            var cam = GameManager.Instance.player.minimapCamera.GetComponent<MinimapCamera>();
            cam.arrowDict.Clear(); //Prevent sometimes double add for portal arrow

            var bossSpawner = spawnedObjectManagerService.GetSpecific<InteractableBossSpawner>();
            if (bossSpawner != null)
            {
                bossSpawner.portal.SetActive(true);
            }
            InteractableBossSpawner.A_BossDefeated?.Invoke(true);
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
            // FIX: the flag opens a host-only game path so this peer can apply someone else's
            // state without re-broadcasting it. Restored in a finally because a throw in the game
            // call would otherwise latch it for the rest of the run. Same defect as P1-10 and P0-6.
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
            // FIX: the flag opens a host-only game path so this peer can apply someone else's
            // state without re-broadcasting it. Restored in a finally because a throw in the game
            // call would otherwise latch it for the rest of the run. Same defect as P1-10 and P0-6.
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
                logger.LogWarning($"Pickup {pickupId} not found in PickupManagerService when processing OnPickupApplied. Deleting");
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
                logger.LogWarning($"Pickup {applied.PickupId} for owner {applied.OwnerId} not found in PickupManagerService when processing OnReceivedPickupApplied.");
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
                logger.LogWarning($"Pickup {player.PickupId} not found in PickupManagerService when processing OnReceivedPickupFollowingPlayer by player {player.PlayerId}.");
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
                logger.LogWarning($"Pickup {pickupId} not found in PickupManagerService when processing HandleWantToStartFollowingPickup.");
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
            // FIX: the flag opens a host-only game path so this peer can apply someone else's
            // state without re-broadcasting it. Restored in a finally because a throw in the game
            // call would otherwise latch it for the rest of the run. Same defect as P1-10 and P0-6.
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

                // try/finally: AddWeapon and RefreshConstantAttack are game code running with
                // CAN_SEND_MESSAGES latched off and two game statics nulled out. A throw used to
                // leave all three that way for the rest of the process — the mod would go silent
                // on the wire and the game would stop firing weapon/stat callbacks. Same shape as
                // P0-6, where one throw latched two statics and broke 581 enemy spawns.
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
                    // Same shape as P1-9: a game static nulled out around game code that can throw.
                    // AddTome throwing used to leave A_TomeUpgrade null for the rest of the
                    // process, killing tome-upgrade handling in every later run, singleplayer
                    // included. Unlike the inventory pair this one saves into a local, so a
                    // finally is the whole fix.
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
                TransitionToState(GameEvent.FinalPortalOpened);
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

        private void OnReceivedInteractableUsed(InteractableUsed used)
        {
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
                var finalPortal = Il2CppFindHelper.FindAllGameObjects().FirstOrDefault(go => go.GetComponent<InteractablePortalFinal>() != null).GetComponent<InteractablePortalFinal>(); //Find the componnt somehow instead of whole gameobject search
                if (finalPortal != null)
                {
                    using (Plugin.SuppressOutbound())
                    {
                        finalPortal.Interact();
                        TransitionToState(GameEvent.FinalPortalOpened);
                    }
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

            if (interactableObj == null)
            {
                if (IsSharedExperienceEnabled())
                {
                    MyTime.Pause();
                    ScreenTextHelper.Show("Waiting for other player(s) choices...", new Vector2(0, -350));
                    RewardFinished();
                }
                else
                {
                    logger.LogWarning("Interactable object not found in SpawnedObjectManagerService when processing OnReceivedInteractableUsed.");
                }

                return;
            }

            using (Plugin.SuppressOutbound())
            {
                switch (used.Action)
                {
                    case InteractableAction.Destroy:
                        //var chest = interactableObj.GetComponent<InteractableChest>();
                        GameObject.DestroyImmediate(interactableObj);
                        break;
                    case InteractableAction.Used:
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

                            // Opt out of the encounter exactly where InteractableMicrowave.Interact()
                            // would refuse to open it, so this peer still releases the barrier.
                            //
                            // The gold half of MicrowaveHelper.CanStartCooking is the fix for the
                            // observed soft lock: Interact() silently returns false when the player
                            // cannot pay, no encounter window opened, RewardFinished() was never
                            // reached, and both players stalled until the 20s failsafe. CanInteract()
                            // does not check gold — CONFIRMED by decompiling
                            // InteractableMicrowave$$CanInteract, see MicrowaveHelper.
                            //
                            // This is the same opt-out the chest branch below already does with
                            // InteractableChest.CanAfford(); the microwave has no CanAfford().
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
        }

        /// <summary>
        /// FIX P2-3: shared by shrine / pylon / lamp, which had three byte-identical copies of this
        /// apart from the message type, the dictionary and a noun in the log line. Resolves
        /// upstream's "pylon and lamp should be refactored to use the same logic" TODO.
        ///
        /// <para>Carries the P0-1 and P2-2 fixes in one place: the occupancy check happens BEFORE
        /// the write (the old order overwrote the current charger with the rejected one, so the
        /// later stop removed nothing and the object stayed locked forever), it uses
        /// <c>TryGetValue</c> rather than an O(n) LINQ scan, and the <c>GetAllPlayers()</c> call
        /// that used to sit here was never read.</para>
        ///
        /// <para>Kept <c>private</c> deliberately — this takes internal charging state, and putting
        /// it on <c>ISynchronizationService</c> would leak that through the API surface.</para>
        /// </summary>
        /// <param name="message">Built by the caller, because each message type names its id
        /// property differently.</param>
        /// <param name="label">Noun for the log lines: "shrine", "pylon", "lamp".</param>
        /// <returns>True when the caller should let the vanilla charge start run locally.</returns>
        /// <param name="replayOnEveryPeer">
        /// Broadcast the start even when this object is already being charged, so every peer runs
        /// its own <c>OnTriggerEnter</c>. Presentation state (the charging flag, the mesh renderer,
        /// the audio) is per-peer and the game only ever sets it from that trigger — a peer that
        /// never receives the message keeps whatever visual state it had.
        ///
        /// <para><b>Only safe where the game's trigger is idempotent.</b> Decompiled
        /// <c>ChargeShrine$$OnTriggerEnter</c> opens with
        /// <c>if (rewardGiven || charging) return;</c>, so a redundant replay there is a no-op.
        /// The pylon and lamp paths pass <c>false</c> because their triggers have not been
        /// decompiled — <b>UNVERIFIED</b>, and re-triggering a non-idempotent one would be worse
        /// than the visual bug this fixes.</para>
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
                logger.LogInfo($"Another player is already charging this {label}. Preventing re trigger.");

                // The set used to be discarded here, so a second charger was never recorded and
                // their later stop hit the "No one is charging this X; ignoring stop" branch.
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
        /// FIX P2-3: the stop counterpart of <see cref="HandleChargingStart"/>. See that method for
        /// why this is private and why the caller builds the message.
        ///
        /// <para>Carries the P0-2 and P2-2 fixes: the key is guarded (the old unguarded indexer
        /// threw <c>KeyNotFoundException</c> when a stop arrived with no recorded start — a packet
        /// reorder, a late join, or a player disconnecting mid-charge), and the dead
        /// <c>GetAllPlayers()</c> call is gone.</para>
        /// </summary>
        /// <returns>True when the caller should let the vanilla charge stop run locally, i.e. this
        /// was the last charger.</returns>
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
                logger.LogInfo($"No one is charging this {label}; ignoring stop.");
                return false;
            }

            chargers.Remove(playerManagerService.GetLocalPlayer().ConnectionId);

            if (chargers.Count > 0)
            {
                logger.LogInfo($"Another player is still charging this {label}. Preventing stop trigger.");
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
                    // Used to return outright. The host is right not to re-run its own trigger,
                    // but the early return also skipped the SendToAllClients below — so a peer
                    // that joined a shrine someone else had already started never received the
                    // message, never ran OnTriggerEnter, and never set its own charging flag or
                    // re-enabled its mesh renderer. The reporting peer was also never recorded,
                    // which is what produced the unbalanced "No one is charging this shrine;
                    // ignoring stop" lines in the host log.
                    if (!chargers.Contains(shrine.PlayerChargingId))
                    {
                        chargers.Add(shrine.PlayerChargingId);
                    }

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

                chargers.Remove(shrine.PlayerChargingId);

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

            // FIX: the flag opens a host-only game path so this peer can apply someone else's
            // state without re-broadcasting it. Restored in a finally because a throw in the game
            // call would otherwise latch it for the rest of the run. Same defect as P1-10 and P0-6.
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


            // FIX P1-4 / P1-6: was two GetAllPlayersAlive() calls on consecutive lines — two
            // Player[] allocations plus four iterators — and the same unguarded shape P1-6 fixed
            // in ReTargetEnemies: Random.Range(0, 0) returns 0 and ElementAt(0) throws on an empty
            // set. GetRandomPlayerAliveConnectionId does the pick once and returns null instead.
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

            // FIX: the flag opens a host-only game path so this peer can apply someone else's
            // state without re-broadcasting it. Restored in a finally because a throw in the game
            // call would otherwise latch it for the rest of the run. Same defect as P1-10 and P0-6.
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
            udpClientService.GameOver();
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

                // FIX 5/6: apply locally too — see OnReceivedPlayerDisconnected for why. All three
                // ReTargetEnemies call sites shared this gap; the host's enemy.target was never
                // updated, only the networked targetId.
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

            // Declared outside the scope: the rest of this method uses it. The suppression only
            // needs to cover the Instantiate, which is what trips the spawn patches.
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
                udpClientService.SendToAllClients(message, NetDelivery.ReliableOrdered);
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
        }

        private void OnReceivedPlayerDisconnected(PlayerDisconnected disconnected)
        {
            // FIX: this used to look the player up and return early when the record was gone.
            //
            // Two independent paths remove a player — this message, and the rendezvous server's
            // ClientDisconnected over the websocket (WebsocketClientService.HandleClientDisconnected
            // → PlayerManagerService.RemovePlayer) — and they race on every peer, host included.
            // Whichever arrived second used to skip *everything* below: the projectile and UI
            // cleanup on a client, and on a host the retarget, which is what stops enemies holding
            // the departed player's destroyed Rigidbody (P2-1). Losing that race silently
            // reinstated the exact bug the retarget exists to prevent.
            //
            // Only the notification needs the player record; every other step needs the connection
            // id alone. So the record is now optional, and the notification — cosmetic — runs last
            // in its own try, per the P0-6 / P1-6 / P1-7 lesson that a throw in the least important
            // statement must not take the most important one with it. AudioManager.Instance and
            // the localisation lookup are both reachable throw sites.
            var disconnectedPeer = playerManagerService.GetPlayer(disconnected.ConnectionId);

            playerManagerService.Disconnect(disconnected.ConnectionId);
            projectileManagerService.RemoveProjectilesByOwnerId(disconnected.ConnectionId);

            // A departed peer must stop being a participant, or the readiness round waits on a
            // report that can never arrive — the same shape as SE-6 on the encounter barrier, where
            // a live participant count includes peers that cannot report. The reference
            // handles this with a transport-level member-removed event; we already have a single
            // disconnect funnel, so it hangs off that instead.
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
                    // Not an error on its own — it means the websocket path got here first. It is
                    // logged because it also means this peer showed no disconnect notification.
                    logger.LogWarning($"Disconnected player {disconnected.ConnectionId} was already removed from PlayerManagerService (websocket ClientDisconnected won the race); cleanup ran, notification skipped.");
                }
                else
                {
                    Plugin.StartNotification(
                        ("MegabonkTogether", "PlayerDisconnected"),
                        ("MegabonkTogether", "PlayerDisconnected_Description"),
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

        /// <summary>
        /// Host-side half of a disconnect: close a blocked encounter if it can be closed, then move
        /// every enemy off the departed player and tell the clients. Split out of
        /// <see cref="OnReceivedPlayerDisconnected"/> so the retarget no longer sits behind an early
        /// return that a lost race can trip.
        /// </summary>
        private void RetargetAfterDisconnect(uint disconnectedConnectionId)
        {
            if (IsSharedExperienceEnabled() && encounterService.IsClosable()) //Making sure to unblock people if somemone leave and we can close
            {
                // The third of the three release sites, and the reason ReleaseBarrier exists: a
                // departure can satisfy the barrier, and this release has to name the same round as
                // the other two or the peers will drop it as stale.
                ReleaseBarrier();
            }

            var allPlayersAliveIdWithout = playerManagerService.GetAllPlayersAlive().Where(p => p.ConnectionId != disconnectedConnectionId).Select(p => p.ConnectionId).ToList();
            var updated = enemyManagerService.ReTargetEnemies(disconnectedConnectionId, allPlayersAliveIdWithout);

            // FIX 5/6: apply the retarget locally too, not just broadcast it.
            //
            // ReTargetEnemies only rewrites the network "targetId" in DynamicData. The physics
            // target — enemy.target, a Rigidbody — is set exclusively by ApplyRetargetedEnemies,
            // which until now ran only on RECEIVERS of this message. The host does not receive
            // its own broadcast, so its enemies kept pointing at the departed player's destroyed
            // Rigidbody. Megabonk's own movement code then read target.transform every frame,
            // which is what drove the dangling-transform fallback to ~144 hits/second after any
            // disconnect (P2-1) — the sampled caller was consistently "no MegabonkTogether
            // frames (called from game code)", i.e. we had handed the game a dead reference.
            //
            // Side effect worth naming: those host enemies were silently falling back to the
            // local player's transform, so they chased the host while every client believed they
            // targeted someone else.
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
            // FIX: the flag opens a host-only game path so this peer can apply someone else's
            // state without re-broadcasting it. Restored in a finally because a throw in the game
            // call would otherwise latch it for the rest of the run. Same defect as P1-10 and P0-6.
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
            // FIX: the flag opens a host-only game path so this peer can apply someone else's
            // state without re-broadcasting it. Restored in a finally because a throw in the game
            // call would otherwise latch it for the rest of the run. Same defect as P1-10 and P0-6.
            Plugin.Instance.CAN_START_STOP_STORMS = true;
            try
            {
                var desertEvent = Plugin.Instance.GetMapEventsDesert();
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
            // FIX: the flag opens a host-only game path so this peer can apply someone else's
            // state without re-broadcasting it. Restored in a finally because a throw in the game
            // call would otherwise latch it for the rest of the run. Same defect as P1-10 and P0-6.
            Plugin.Instance.CAN_START_STOP_STORMS = true;
            try
            {
                Plugin.Instance.GetMapEventsDesert().StopStorm();
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
            IGameNetworkMessage message = new HatChanged
            {
                OwnerId = playerManagerService.GetLocalPlayer().ConnectionId,
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
                GameManager.Instance.player.transform.position = position;
                GameManager.Instance.player.inventory.playerHealth.hp = GameManager.Instance.player.inventory.playerHealth.maxHp;
                var localPlayer = playerManagerService.GetLocalPlayer();
                localPlayer.Hp = localPlayer.MaxHp;
                playerManagerService.UpdatePlayer(localPlayer);
                Plugin.Instance.CameraSwitcher.ResetToLocalPlayer();
                GameManager.Instance.player.playerRenderer.gameObject.SetActive(true);
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
                GameManager.Instance.player.transform.position = Quantizer.Dequantize(respawned.Position);
                GameManager.Instance.player.inventory.playerHealth.hp = GameManager.Instance.player.inventory.playerHealth.maxHp;

                var localPlayer = playerManagerService.GetLocalPlayer();
                localPlayer.Hp = localPlayer.MaxHp;
                playerManagerService.UpdatePlayer(localPlayer);

                Plugin.Instance.CameraSwitcher.ResetToLocalPlayer();

                GameManager.Instance.player.playerRenderer.gameObject.SetActive(true);
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

        private void OnReceivedAddXp(AddXp xp)
        {
            using (Plugin.SuppressOutbound())
            {
                var playerXp = GameManager.Instance.player.inventory.playerXp;
                playerXp.xp = xp.Xp;
                playerXp.leftOverXp = xp.LeftOverXp;
                playerXp.AddXp(0);
            }
        }

        public void RewardFinished()
        {
            // The failsafe clock starts the moment this peer reports, not when it renders the
            // "waiting" text: every caller of this method is about to block on the barrier, and
            // several of them (the opt-out paths in OnReceivedInteractableUsed) never touch the
            // encounter window at all.
            encounterService.BeginWaiting();

            var isHost = IsServerMode() ?? false;
            if (isHost)
            {
                // SE-5: the host owns the barrier session, and this is the earliest point at which
                // any barrier message can exist, so minting here is unconditionally early enough.
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

        /// <summary>
        /// Host only. Broadcasts the release for the round that is currently open and applies it
        /// locally. Extracted because there were three copies of this — <see cref="RewardFinished"/>,
        /// <see cref="ForceCloseEncounter"/> and the <c>EncounterClosed</c> handler in
        /// <c>UdpClientService</c> — and a stamp that is assembled differently in any of them is a
        /// stamp that does not identify a round.
        /// </summary>
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

            // Logged on the SUCCESS path, not only on rejection.
            //
            // The first session on this build could not confirm the barrier worked at all: the only
            // barrier line in either log was one failsafe fire, because every healthy path here was
            // silent and the stamped messages are too low-rate to escape the [bw] report's
            // "(N more types)" bucket. A round-identity mechanism whose correct operation leaves no
            // trace cannot be verified in play, only suspected — and "absence of a log line is not
            // absence of the event" is a lesson this project has already paid for twice.
            //
            // One line per barrier round, which is a handful per stage. Nowhere near a hot path.
            logger.LogInfo($"[barrier] Released round {roundId} (session {sessionId}) to all clients.");

            // Applied through the same gate the clients use, so the host advances its own round
            // counter by exactly the rule it just broadcast rather than by a second code path.
            if (encounterService.TryApplyRelease(sessionId, roundId))
            {
                OnCloseEncounter();
            }
        }

        /// <summary>
        /// SE-5 / OB-4. A release now names the round it releases, and a repeat for a round already
        /// applied is dropped here rather than closing whatever encounter window happens to be open
        /// — which is precisely the reported failure: the failsafe fired, a second release was
        /// generated for a finished round, and it shut a fresh chest window instantly.
        /// </summary>
        private void OnReceivedCloseEncounterStamped(CloseEncounterStamped close)
        {
            if (!encounterService.TryApplyRelease(close.SessionId, close.RoundId))
            {
                logger.LogInfo(
                    $"[barrier] Ignoring a stale release (session {close.SessionId}, round {close.RoundId}); " +
                    $"this peer is on session {encounterService.SessionId}, round {encounterService.RoundId}.");
                return;
            }

            // Paired with the rejection line above so the two are greppable together: a healthy
            // session shows one applied line per round and no stale ones, and that distinction is
            // the whole point of the stamp.
            logger.LogInfo($"[barrier] Applied release for round {close.RoundId} (session {close.SessionId}).");

            encounterService.Close();
            OnCloseEncounter();
        }

        /// <summary>
        /// Retained for peers on a build without union tag 70. Unstamped, so it cannot be attributed
        /// to a round and carries every SE-5 hazard; nothing in this build sends it.
        /// </summary>
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
            // Guarded: this runs from a network callback, so the UI can be mid-teardown (returning
            // to the menu, changing stage). An NRE here used to abandon the release entirely,
            // leaving every peer paused behind a barrier that had already been satisfied.
            var encounterWindows = UiManager.Instance == null ? null : UiManager.Instance.encounterWindows;

            if (encounterWindows != null && encounterWindows.encounterInProgress)
            {
                encounterWindows.RewardFinished();
            }
            else
            {
                encounterService.ClearClosedEncounters();
                MyTime.Unpause();
            }
            //EncounterWindows.A_WindowClosed.Invoke();
        }

        /// <summary>
        /// Failsafe release for a barrier that will never complete on its own — the answer to
        /// upstream #88's "add a 60 second failsafe that closes the menus for both players".
        ///
        /// <para>A client also re-sends its own <c>EncounterClosed</c>: the likeliest reason it is
        /// still waiting is that the host never counted its report (a round-attribution mistake,
        /// not packet loss — the channel is reliable), and re-reporting is what lets the host's
        /// barrier complete for everyone else rather than only unsticking this peer.</para>
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
                    // Stamped like any other release, so the peers that already applied this round
                    // drop it instead of closing a window they have since opened — OB-4, which was
                    // this failsafe firing and taking a live chest window with it.
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

        private void OnReceivedChangeGold(GoldChanged changed)
        {
            // SE-11: ignore our own gain coming back.
            //
            // Gold is shared as a DELTA (upstream 4.0.3 — gains shared, losses local), and a delta
            // applied twice is a permanent divergence, not the no-op it would be under an absolute
            // value. The host excludes the sender when relaying, but that exclusion runs through
            // SendToAllClientsExcept, whose relay branch falls back to an EMPTY filter list on a
            // lookup miss — UNVERIFIED, and open work in its own right (`RelayEnvelope.ToFilters`).
            // A cheap owner check makes the delta model safe regardless of how that resolves.
            var localPlayer = playerManagerService.GetLocalPlayer();
            if (localPlayer != null && changed.OwnerId == localPlayer.ConnectionId)
            {
                return;
            }

            // Runs from a network callback, and gold changes with every coin — an unguarded deref
            // here during teardown or a stage change is a repeating NullReferenceException, which
            // is what upstream #76 reported alongside the freeze.
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

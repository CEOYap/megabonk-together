using OmniBonk.Common.Messages;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Common.Models;
using OmniBonk.Scripts;
using System;
using System.Collections.Generic;

namespace OmniBonk.Services
{
    public static class EventManager
    {
        private static event Action<SpawnedObject> SpawnedObjectsEvents;
        private static event Action<SpawnedEnemy> SpawnedEnemyEvents;
        private static event Action<EnemyMaxHpScaled> EnemyMaxHpScaledEvents;
        private static event Action<PlayerStatsChanged> PlayerStatsChangedEvents;
        private static event Action<StateDigest> StateDigestEvents;
        private static event Action<ChargeShrineCompleted> ChargeShrineCompletedEvents;
        private static event Action<LobbySettings> LobbySettingsEvents;
        private static event Action<EndlessNextRoom> EndlessNextRoomEvents;
        private static event Action<PlayerUpdate> PlayerUpdatesEvents;
        private static event Action<SelectedCharacter> SelectedCharacterEvents;
        private static event Action<IEnumerable<EnemyModel>> EnemiesUpdateEvents;
        private static event Action<EnemyDied> EnemyDiedEvents;
        private static event Action<AbstractSpawnedProjectile> SpawnedProjectileEvents;
        private static event Action<ProjectileDone> ProjectileDoneEvents;
        private static event Action<SpawnedPickupOrb> SpawnedPickupOrbEvents;
        private static event Action<SpawnedPickup> SpawnedPickupEvents;
        private static event Action<PickupApplied> PickupAppliedEvents;
        private static event Action<PickupFollowingPlayer> PickupFollowingPlayerEvents;
        private static event Action<SpawnedChest> SpawnedChestEvents;
        private static event Action<ChestOpened> ChestOpenedEvents;
        private static event Action<WeaponAdded> WeaponAddedEvents;
        private static event Action<InteractableUsed> InteractableUsedEvents;
        private static event Action<StartingChargingShrine> StartingChargingShrineEvents;
        private static event Action<StoppingChargingShrine> StoppingChargingShrineEvents;
        private static event Action<EnemyExploder> EnemyExploderEvents;
        private static event Action<EnemyDamaged> EnemyDamagedEvents;
        private static event Action<EnemyDamagedBatch> EnemyDamagedBatchEvents;
        private static event Action<ItemProjectilesSpawned> ItemProjectilesSpawnedEvents;
        private static event Action<MineExploded> MineExplodedEvents;
        private static event Action<FlaskExploded> FlaskExplodedEvents;
        private static event Action<SpawnedEnemySpecialAttack> SpawnedEnemySpecialAttackEvents;
        private static event Action<StartingChargingPylon> StartingChargingPylonEvents;
        private static event Action<StoppingChargingPylon> StoppingChargingPylonEvents;
        private static event Action<FinalBossOrbSpawned> FinalBossOrbSpawnedEvents;
        private static event Action<IEnumerable<BossOrbModel>> FinalBossOrbsUpdateEvents;
        private static event Action<FinalBossOrbDestroyed> FinalBossOrbDestroyedEvents;
        private static event Action<StartedSwarmEvent> StartedSwarmEventEvents;
        private static event Action<GameOver> GameOverEvents;
        private static event Action<PlayerDied> PlayerDiedEvents;
        private static event Action<RetargetedEnemies> RetargetedEnemiesEvents;
        private static event Action<RunStarted> RunStartedEvents;
        private static event Action<PlayerDisconnected> PlayerDisconnectedEvents;
        private static event Action<IEnumerable<Projectile>> ProjectilesUpdateEvents;
        private static event Action<TomeAdded> TomeAddedEvents;
        private static event Action<LightningStrike> LightningStrikeEvents;
        private static event Action<TornadoesSpawned> TornadoesSpawnedEvents;
        private static event Action<StormStarted> StormStartedEvents;
        private static event Action<StormStopped> StormStoppedEvents;
        private static event Action<TumbleWeedSpawned> TumbleWeedSpawnedEvents;
        private static event Action<IEnumerable<TumbleWeedModel>> TumbleWeedsUpdateEvents;
        private static event Action<TumbleWeedDespawned> TumbleWeedDespawnedEvents;
        private static event Action<InteractableCharacterFightEnemySpawned> InteractableCharacterFightEnemySpawnedEvents;
        private static event Action<WantToStartFollowingPickup> WantToStartFollowingPickupEvents;
        private static event Action<ItemAdded> ItemAddedEvents;
        private static event Action<ItemRemoved> ItemRemovedEvents;
        private static event Action<WeaponToggled> WeaponToggledEvents;
        private static event Action GameStartedEvents;
        private static event Action PortalOpenedEvents;
        private static event Action<SpawnedObjectInCrypt> SpawnedObjectInCryptEvents;
        private static event Action<StartingChargingLamp> StartingChargingLampEvents;
        private static event Action<StoppingChargingLamp> StoppingChargingLampEvents;
        private static event Action<TimerStarted> TimerStartedEvents;
        private static event Action<HatChanged> HatChangedEvents;
        private static event Action<SpawnedReviver> SpawnedReviverEvents;
        private static event Action<PlayerRespawned> PlayerRespawnedEvents;
        private static event Action<AddXp> AddXpEvents;
        private static event Action<CloseEncounter> CloseEncounterEvents;
        private static event Action<CloseEncounterStamped> CloseEncounterStampedEvents;
        private static event Action ReleaseBarrierEvents;
        private static event Action<ReadinessRoundStarted> ReadinessRoundStartedEvents;
        private static event Action ReadinessRoundReAskEvents;
        private static event Action LobbyStartRequestedEvents;
        private static event Action<LobbyReadyChanged> LobbyReadyChangedEvents;
        private static event Action<LobbyReadyState> LobbyReadyStateEvents;
        private static event Action<LobbyRoster> LobbyRosterEvents;
        private static event Action<ProtocolHello> ProtocolHelloEvents;
        private static event Action<GoldChanged> GoldChangedEvents;

        public static void OnSpawnedObject(SpawnedObject spawnedObject)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SpawnedObjectsEvents?.Invoke(spawnedObject);
            });
        }

        public static void SubscribeSpawnedObjectsEvents(Action<SpawnedObject> action)
        {
            SpawnedObjectsEvents += action;
        }

        public static void OnPlayerUpdate(PlayerUpdate playerUpdate)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                PlayerUpdatesEvents?.Invoke(playerUpdate);
            });
        }

        public static void SubscribePlayerUpdatesEvents(Action<PlayerUpdate> action)
        {
            PlayerUpdatesEvents += action;
        }

        public static void OnSpawnedEnemy(SpawnedEnemy spawnedEnemy)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SpawnedEnemyEvents?.Invoke(spawnedEnemy);
            });
        }

        public static void SubscribeSpawnedEnemyEvents(Action<SpawnedEnemy> action)
        {
            SpawnedEnemyEvents += action;
        }

        /// <summary>OB-9. An enemy's HP was rescaled after its spawn message had already been sent.</summary>
        public static void OnEnemyMaxHpScaled(EnemyMaxHpScaled enemyMaxHpScaled)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                EnemyMaxHpScaledEvents?.Invoke(enemyMaxHpScaled);
            });
        }

        public static void SubscribeEnemyMaxHpScaledEvents(Action<EnemyMaxHpScaled> action)
        {
            EnemyMaxHpScaledEvents += action;
        }

        /// <summary>OB-1. A peer's global stat block changed.</summary>
        public static void OnPlayerStatsChanged(PlayerStatsChanged playerStatsChanged)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                PlayerStatsChangedEvents?.Invoke(playerStatsChanged);
            });
        }

        public static void SubscribePlayerStatsChangedEvents(Action<PlayerStatsChanged> action)
        {
            PlayerStatsChangedEvents += action;
        }

        /// <summary>Desync detector. The host's periodic view of the run.</summary>
        public static void OnStateDigest(StateDigest stateDigest)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                StateDigestEvents?.Invoke(stateDigest);
            });
        }

        public static void SubscribeStateDigestEvents(Action<StateDigest> action)
        {
            StateDigestEvents += action;
        }

        /// <summary>Endless Run: the host's room for the next stage change.</summary>
        public static void OnEndlessNextRoom(EndlessNextRoom message)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                EndlessNextRoomEvents?.Invoke(message);
            });
        }

        public static void SubscribeEndlessNextRoomEvents(Action<EndlessNextRoom> action)
        {
            EndlessNextRoomEvents += action;
        }

        /// <summary>The host's lobby settings for this session.</summary>
        public static void OnLobbySettings(LobbySettings message)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                LobbySettingsEvents?.Invoke(message);
            });
        }

        public static void SubscribeLobbySettingsEvents(Action<LobbySettings> action)
        {
            LobbySettingsEvents += action;
        }

        /// <summary>OB-6. The host completed a charge shrine.</summary>
        public static void OnChargeShrineCompleted(ChargeShrineCompleted message)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ChargeShrineCompletedEvents?.Invoke(message);
            });
        }

        public static void SubscribeChargeShrineCompletedEvents(Action<ChargeShrineCompleted> action)
        {
            ChargeShrineCompletedEvents += action;
        }

        public static void SubscribeSelectedCharacterEvents(Action<SelectedCharacter> action)
        {
            SelectedCharacterEvents += action;
        }

        public static void OnSelectedCharacter(SelectedCharacter selectedCharacter)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SelectedCharacterEvents?.Invoke(selectedCharacter);
            });
        }

        public static void SubscribeEnemiesUpdateEvents(Action<IEnumerable<EnemyModel>> action)
        {
            EnemiesUpdateEvents += action;
        }

        public static void OnEnemiesUpdate(IEnumerable<EnemyModel> enemies)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                EnemiesUpdateEvents?.Invoke(enemies);
            });
        }

        public static void SubscribeEnemyDiedEvents(Action<EnemyDied> action)
        {
            EnemyDiedEvents += action;
        }

        public static void OnEnemyDied(EnemyDied enemyDied)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                EnemyDiedEvents?.Invoke(enemyDied);
            });
        }

        public static void SubscribeSpawnedProjectileEvents(Action<AbstractSpawnedProjectile> action)
        {
            SpawnedProjectileEvents += action;
        }
        public static void OnSpawnedProjectile(AbstractSpawnedProjectile spawnedProjectile)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SpawnedProjectileEvents?.Invoke(spawnedProjectile);
            });
        }

        public static void SubscribeProjectileDoneEvents(Action<ProjectileDone> action)
        {
            ProjectileDoneEvents += action;
        }

        public static void OnProjectileDone(ProjectileDone projectileDone)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ProjectileDoneEvents?.Invoke(projectileDone);
            });
        }

        public static void SubscribeSpawnedPickupOrbEvents(Action<SpawnedPickupOrb> action)
        {
            SpawnedPickupOrbEvents += action;
        }

        public static void OnSpawnedPickupOrb(SpawnedPickupOrb spawnedPickup)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SpawnedPickupOrbEvents?.Invoke(spawnedPickup);
            });
        }

        public static void SubscribeSpawnedPickupEvents(Action<SpawnedPickup> action)
        {
            SpawnedPickupEvents += action;
        }

        public static void OnSpawnedPickup(SpawnedPickup spawnedPickup)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SpawnedPickupEvents?.Invoke(spawnedPickup);
            });
        }


        public static void SubscribePickupAppliedEvents(Action<PickupApplied> action)
        {
            PickupAppliedEvents += action;
        }

        public static void OnPickupApplied(PickupApplied pickupApplied)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                PickupAppliedEvents?.Invoke(pickupApplied);
            });
        }

        public static void SubscribePickupFollowingPlayerEvents(Action<PickupFollowingPlayer> action)
        {
            PickupFollowingPlayerEvents += action;
        }
        public static void OnPickupFollowingPlayer(PickupFollowingPlayer pickupFollowingPlayer)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                PickupFollowingPlayerEvents?.Invoke(pickupFollowingPlayer);
            });
        }

        public static void SubscribeSpawnedChestEvents(Action<SpawnedChest> action)
        {
            SpawnedChestEvents += action;
        }
        public static void OnSpawnedChest(SpawnedChest spawnedChest)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SpawnedChestEvents?.Invoke(spawnedChest);
            });
        }

        public static void SubscribeChestOpenedEvents(Action<ChestOpened> action)
        {
            ChestOpenedEvents += action;
        }

        public static void OnChestOpened(ChestOpened chestOpened)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ChestOpenedEvents?.Invoke(chestOpened);
            });
        }

        public static void SubscribeWeaponAddedEvents(Action<WeaponAdded> action)
        {
            WeaponAddedEvents += action;
        }
        public static void OnWeaponAdded(WeaponAdded weaponAdded)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                WeaponAddedEvents?.Invoke(weaponAdded);
            });
        }

        public static void SubscribeInteractableUsedEvents(Action<InteractableUsed> action)
        {
            InteractableUsedEvents += action;
        }

        public static void OnInteractableUsed(InteractableUsed interactableUsed)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                InteractableUsedEvents?.Invoke(interactableUsed);
            });
        }

        public static void SubscribeStartingChargingShrineEvents(Action<StartingChargingShrine> action)
        {
            StartingChargingShrineEvents += action;
        }
        public static void OnStartingChargingShrine(StartingChargingShrine startingChargingShrine)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                StartingChargingShrineEvents?.Invoke(startingChargingShrine);
            });
        }

        public static void SubscribeStoppingChargingShrineEvents(Action<StoppingChargingShrine> action)
        {
            StoppingChargingShrineEvents += action;
        }

        public static void OnStoppingChargingShrine(StoppingChargingShrine stoppingChargingShrine)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                StoppingChargingShrineEvents?.Invoke(stoppingChargingShrine);
            });
        }

        public static void SubscribeEnemyExploderEvents(Action<EnemyExploder> action)
        {
            EnemyExploderEvents += action;
        }
        public static void OnEnemyExploder(EnemyExploder enemyExploder)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                EnemyExploderEvents?.Invoke(enemyExploder);
            });
        }

        public static void SubscribeEnemyDamagedEvents(Action<EnemyDamaged> action)
        {
            EnemyDamagedEvents += action;
        }

        public static void OnEnemyDamaged(EnemyDamaged enemyDamaged)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                EnemyDamagedEvents?.Invoke(enemyDamaged);
            });
        }

        public static void SubscribeEnemyDamagedBatchEvents(Action<EnemyDamagedBatch> action)
        {
            EnemyDamagedBatchEvents += action;
        }

        public static void OnEnemyDamagedBatch(EnemyDamagedBatch enemyDamagedBatch)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                EnemyDamagedBatchEvents?.Invoke(enemyDamagedBatch);
            });
        }

        /// <summary>Both sides. Item projectiles another machine spawned; see <see cref="ItemProjectilesSpawned"/>.</summary>
        public static void SubscribeItemProjectilesSpawnedEvents(Action<ItemProjectilesSpawned> action)
        {
            ItemProjectilesSpawnedEvents += action;
        }

        public static void OnItemProjectilesSpawned(ItemProjectilesSpawned spawned)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ItemProjectilesSpawnedEvents?.Invoke(spawned);
            });
        }

        /// <summary>Clients. A host mine exploded; see <see cref="MineExploded"/>.</summary>
        public static void SubscribeMineExplodedEvents(Action<MineExploded> action)
        {
            MineExplodedEvents += action;
        }

        public static void OnMineExploded(MineExploded exploded)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                MineExplodedEvents?.Invoke(exploded);
            });
        }

        /// <summary>Clients. A host Poison Flask exploded; see <see cref="FlaskExploded"/>.</summary>
        public static void SubscribeFlaskExplodedEvents(Action<FlaskExploded> action)
        {
            FlaskExplodedEvents += action;
        }

        public static void OnFlaskExploded(FlaskExploded exploded)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                FlaskExplodedEvents?.Invoke(exploded);
            });
        }

        public static void SubscribeSpawnedEnemySpecialAttackEvents(Action<SpawnedEnemySpecialAttack> action)
        {
            SpawnedEnemySpecialAttackEvents += action;
        }

        public static void OnSpawnedEnemySpecialAttack(SpawnedEnemySpecialAttack spawnedEnemySpecialAttack)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SpawnedEnemySpecialAttackEvents?.Invoke(spawnedEnemySpecialAttack);
            });
        }

        public static void SubscribeStartingChargingPylonEvents(Action<StartingChargingPylon> action)
        {
            StartingChargingPylonEvents += action;
        }

        public static void OnStartingChargingPylon(StartingChargingPylon startingChargingPylon)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                StartingChargingPylonEvents?.Invoke(startingChargingPylon);
            });
        }

        public static void SubscribeStoppingChargingPylonEvents(Action<StoppingChargingPylon> action)
        {
            StoppingChargingPylonEvents += action;
        }

        public static void OnStoppingChargingPylon(StoppingChargingPylon stoppingChargingPylon)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                StoppingChargingPylonEvents?.Invoke(stoppingChargingPylon);
            });
        }

        public static void SubscribeFinalBossOrbSpawnedEvents(Action<FinalBossOrbSpawned> action)
        {
            FinalBossOrbSpawnedEvents += action;
        }

        public static void OnFinalBossOrbSpawned(FinalBossOrbSpawned finalBossOrbSpawned)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                FinalBossOrbSpawnedEvents?.Invoke(finalBossOrbSpawned);
            });
        }

        public static void SubscribeFinalBossOrbsUpdateEvents(Action<IEnumerable<BossOrbModel>> action)
        {
            FinalBossOrbsUpdateEvents += action;
        }

        public static void OnFinalBossOrbsUpdate(IEnumerable<BossOrbModel> bossOrbs)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                FinalBossOrbsUpdateEvents?.Invoke(bossOrbs);
            });
        }

        public static void SubscribeFinalBossOrbDestroyedEvents(Action<FinalBossOrbDestroyed> action)
        {
            FinalBossOrbDestroyedEvents += action;
        }

        public static void OnFinalBossOrbDestroyed(FinalBossOrbDestroyed finalBossOrbDestroyed)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                FinalBossOrbDestroyedEvents?.Invoke(finalBossOrbDestroyed);
            });
        }

        public static void SubscribeStartedSwarmEventEvents(Action<StartedSwarmEvent> action)
        {
            StartedSwarmEventEvents += action;
        }
        public static void OnStartedSwarmEvent(StartedSwarmEvent startedSwarmEvent)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                StartedSwarmEventEvents?.Invoke(startedSwarmEvent);
            });
        }

        public static void SubscribeGameOverEvents(Action<GameOver> action)
        {
            GameOverEvents += action;
        }

        public static void OnGameOver(GameOver gameOver)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                GameOverEvents?.Invoke(gameOver);
            });
        }

        public static void SubscribePlayerDiedEvents(Action<PlayerDied> action)
        {
            PlayerDiedEvents += action;
        }

        public static void OnPlayerDied(PlayerDied playerDied)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                PlayerDiedEvents?.Invoke(playerDied);
            });
        }

        public static void SubscribeRetargetedEnemiesEvents(Action<RetargetedEnemies> action)
        {
            RetargetedEnemiesEvents += action;
        }
        public static void OnRetargetedEnemies(RetargetedEnemies retargetedEnemies)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                RetargetedEnemiesEvents?.Invoke(retargetedEnemies);
            });
        }

        public static void SubscribeRunStartedEvents(Action<RunStarted> action)
        {
            RunStartedEvents += action;
        }
        public static void OnRunStarted(RunStarted runStarted)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                RunStartedEvents?.Invoke(runStarted);
            });
        }

        public static void SubscribePlayerDisconnectedEvents(Action<PlayerDisconnected> action)
        {
            PlayerDisconnectedEvents += action;
        }

        public static void OnPlayerDisconnected(PlayerDisconnected playerDisconnected)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                PlayerDisconnectedEvents?.Invoke(playerDisconnected);
            });
        }

        public static void SubscribeProjectilesUpdateEvents(Action<IEnumerable<Projectile>> action)
        {
            ProjectilesUpdateEvents += action;
        }

        public static void OnProjectilesUpdate(IEnumerable<Projectile> projectiles)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ProjectilesUpdateEvents?.Invoke(projectiles);
            });
        }

        public static void SubscribeTomeAddedEvents(Action<TomeAdded> action)
        {
            TomeAddedEvents += action;
        }

        public static void OnTomeAdded(TomeAdded tomeAdded)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                TomeAddedEvents?.Invoke(tomeAdded);
            });
        }

        public static void SubscribeLightningStrikeEvents(Action<LightningStrike> action)
        {
            LightningStrikeEvents += action;
        }

        public static void OnLightningStrike(LightningStrike lightningStrike)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                LightningStrikeEvents?.Invoke(lightningStrike);
            });
        }

        public static void SubscribeTornadoesSpawnedEvents(Action<TornadoesSpawned> action)
        {
            TornadoesSpawnedEvents += action;
        }

        public static void OnTornadoesSpawned(TornadoesSpawned tornadoesSpawned)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                TornadoesSpawnedEvents?.Invoke(tornadoesSpawned);
            });
        }

        public static void SubscribeStormStartedEvents(Action<StormStarted> action)
        {
            StormStartedEvents += action;
        }

        public static void OnStormStarted(StormStarted stormStarted)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                StormStartedEvents?.Invoke(stormStarted);
            });
        }

        public static void SubscribeStormStoppedEvents(Action<StormStopped> action)
        {
            StormStoppedEvents += action;
        }

        public static void OnStormStopped(StormStopped stormStopped)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                StormStoppedEvents?.Invoke(stormStopped);
            });
        }

        public static void SubscribeTumbleWeedSpawnedEvents(Action<TumbleWeedSpawned> action)
        {
            TumbleWeedSpawnedEvents += action;
        }

        public static void OnTumbleWeedSpawned(TumbleWeedSpawned tumbleWeedSpawned)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                TumbleWeedSpawnedEvents?.Invoke(tumbleWeedSpawned);
            });
        }

        public static void SubscribeTumbleWeedsUpdateEvents(Action<IEnumerable<TumbleWeedModel>> action)
        {
            TumbleWeedsUpdateEvents += action;
        }

        public static void OnTumbleWeedsUpdate(IEnumerable<TumbleWeedModel> tumbleWeeds)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                TumbleWeedsUpdateEvents?.Invoke(tumbleWeeds);
            });
        }

        public static void SubscribeTumbleWeedDespawnedEvents(Action<TumbleWeedDespawned> action)
        {
            TumbleWeedDespawnedEvents += action;
        }

        public static void OnTumbleWeedDespawned(TumbleWeedDespawned tumbleWeedDespawned)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                TumbleWeedDespawnedEvents?.Invoke(tumbleWeedDespawned);
            });
        }

        public static void SubscribeInteractableCharacterFightEnemySpawnedEvents(Action<InteractableCharacterFightEnemySpawned> action)
        {
            InteractableCharacterFightEnemySpawnedEvents += action;
        }

        public static void OnInteractableCharacterFightEnemySpawned(InteractableCharacterFightEnemySpawned interactableCharacterFightEnemySpawned)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                InteractableCharacterFightEnemySpawnedEvents?.Invoke(interactableCharacterFightEnemySpawned);
            });
        }

        public static void SubscribeWantToStartFollowingPickupEvents(Action<WantToStartFollowingPickup> action)
        {
            WantToStartFollowingPickupEvents += action;
        }

        public static void OnWantToStartFollowingPickup(WantToStartFollowingPickup wantToStartFollowingPickup)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                WantToStartFollowingPickupEvents?.Invoke(wantToStartFollowingPickup);
            });
        }

        public static void SubscribeItemAddedEvents(Action<ItemAdded> action)
        {
            ItemAddedEvents += action;
        }

        public static void OnItemAdded(ItemAdded itemAdded)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ItemAddedEvents?.Invoke(itemAdded);
            });
        }

        public static void SubscribeItemRemovedEvents(Action<ItemRemoved> action)
        {
            ItemRemovedEvents += action;
        }

        public static void OnItemRemoved(ItemRemoved itemRemoved)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ItemRemovedEvents?.Invoke(itemRemoved);
            });
        }

        public static void SubscribeWeaponToggledEvents(Action<WeaponToggled> action)
        {
            WeaponToggledEvents += action;
        }

        public static void OnWeaponToggled(WeaponToggled weaponToggled)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                WeaponToggledEvents?.Invoke(weaponToggled);
            });
        }

        public static void SubscribeGameStartedEvents(Action action)
        {
            GameStartedEvents += action;
        }
        public static void OnGameStarted()
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                GameStartedEvents?.Invoke();
            });
        }

        public static void SubscribePortalOpenedEvents(Action action)
        {
            PortalOpenedEvents += action;
        }

        public static void OnPortalOpened()
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                PortalOpenedEvents?.Invoke();
            });
        }

        public static void SubscribeSpawnedObjectInCryptEvents(Action<SpawnedObjectInCrypt> action)
        {
            SpawnedObjectInCryptEvents += action;
        }

        public static void OnSpawnedObjectInCrypt(SpawnedObjectInCrypt spawnedObjectInCrypt)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SpawnedObjectInCryptEvents?.Invoke(spawnedObjectInCrypt);
            });
        }

        public static void SubscribeStartingChargingLampEvents(Action<StartingChargingLamp> action)
        {
            StartingChargingLampEvents += action;
        }

        public static void OnStartingChargingLamp(StartingChargingLamp startingChargingLamp)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                StartingChargingLampEvents?.Invoke(startingChargingLamp);
            });
        }

        public static void SubscribeStoppingChargingLampEvents(Action<StoppingChargingLamp> action)
        {
            StoppingChargingLampEvents += action;
        }

        public static void OnStoppingChargingLamp(StoppingChargingLamp stoppingChargingLamp)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                StoppingChargingLampEvents?.Invoke(stoppingChargingLamp);
            });
        }

        public static void SubscribeTimerStartedEvents(Action<TimerStarted> action)
        {
            TimerStartedEvents += action;
        }

        public static void OnTimerStarted(TimerStarted timerStarted)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                TimerStartedEvents?.Invoke(timerStarted);
            });
        }

        public static void SubscribeHatChangedEvents(Action<HatChanged> action)
        {
            HatChangedEvents += action;
        }

        public static void OnHatChanged(HatChanged hatChanged)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                HatChangedEvents?.Invoke(hatChanged);
            });
        }

        public static void SubscribeSpawnedReviverEvents(Action<SpawnedReviver> action)
        {
            SpawnedReviverEvents += action;
        }

        public static void OnSpawnedReviver(SpawnedReviver spawnedReviver)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                SpawnedReviverEvents?.Invoke(spawnedReviver);
            });
        }

        public static void SubscribePlayerRespawnedEvents(Action<PlayerRespawned> action)
        {
            PlayerRespawnedEvents += action;
        }

        public static void OnPlayerRespawned(PlayerRespawned playerRespawned)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                PlayerRespawnedEvents?.Invoke(playerRespawned);
            });
        }

        public static void SubscribeAddXpEvents(Action<AddXp> action)
        {
            AddXpEvents += action;
        }

        public static void OnAddXp(AddXp addXp)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                AddXpEvents?.Invoke(addXp);
            });
        }

        public static void SubscribeCloseEncounterEvents(Action<CloseEncounter> action)
        {
            CloseEncounterEvents += action;
        }

        public static void OnCloseEncounter(CloseEncounter closeEncounter)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                CloseEncounterEvents?.Invoke(closeEncounter);
            });
        }

        public static void SubscribeCloseEncounterStampedEvents(Action<CloseEncounterStamped> action)
        {
            CloseEncounterStampedEvents += action;
        }

        public static void OnCloseEncounterStamped(CloseEncounterStamped closeEncounter)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                CloseEncounterStampedEvents?.Invoke(closeEncounter);
            });
        }

        /// <summary>Host-side. No payload: only <c>SynchronizationService.ReleaseBarrier</c> builds the round stamp.</summary>
        public static void SubscribeReleaseBarrierEvents(Action action)
        {
            ReleaseBarrierEvents += action;
        }

        public static void OnReleaseBarrier()
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ReleaseBarrierEvents?.Invoke();
            });
        }

        public static void SubscribeReadinessRoundStartedEvents(Action<ReadinessRoundStarted> action)
        {
            ReadinessRoundStartedEvents += action;
        }

        public static void OnReadinessRoundStarted(ReadinessRoundStarted started)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ReadinessRoundStartedEvents?.Invoke(started);
            });
        }

        /// <summary>Host-side re-announce; payload-free like <see cref="OnReleaseBarrier"/>.</summary>
        public static void SubscribeReadinessRoundReAskEvents(Action action)
        {
            ReadinessRoundReAskEvents += action;
        }

        public static void OnReadinessRoundReAsk()
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ReadinessRoundReAskEvents?.Invoke();
            });
        }

        /// <summary>Host ended the lobby; clients advance without re-deciding.</summary>
        public static void SubscribeLobbyStartRequestedEvents(Action action)
        {
            LobbyStartRequestedEvents += action;
        }

        public static void UnsubscribeLobbyStartRequestedEvents(Action action)
        {
            LobbyStartRequestedEvents -= action;
        }

        public static void OnLobbyStartRequested()
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                LobbyStartRequestedEvents?.Invoke();
            });
        }

        /// <summary>Host-side. Routed through here to avoid a transport/lobby-service DI cycle (it deadlocked).</summary>
        public static void SubscribeLobbyReadyChangedEvents(Action<LobbyReadyChanged> action)
        {
            LobbyReadyChangedEvents += action;
        }

        public static void OnLobbyReadyChanged(LobbyReadyChanged changed)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                LobbyReadyChangedEvents?.Invoke(changed);
            });
        }

        /// <summary>Client-side. The host's authoritative lobby-ready set.</summary>
        public static void SubscribeLobbyRosterEvents(Action<LobbyRoster> action)
        {
            LobbyRosterEvents += action;
        }

        public static void OnLobbyRoster(LobbyRoster roster)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                LobbyRosterEvents?.Invoke(roster);
            });
        }

        /// <summary>Both sides. A peer's wire protocol version; see <see cref="ProtocolHello"/>.</summary>
        public static void SubscribeProtocolHelloEvents(Action<ProtocolHello> action)
        {
            ProtocolHelloEvents += action;
        }

        public static void OnProtocolHello(ProtocolHello hello)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                ProtocolHelloEvents?.Invoke(hello);
            });
        }

        public static void SubscribeLobbyReadyStateEvents(Action<LobbyReadyState> action)
        {
            LobbyReadyStateEvents += action;
        }

        public static void OnLobbyReadyState(LobbyReadyState state)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                LobbyReadyStateEvents?.Invoke(state);
            });
        }

        public static void SubscribeGoldChangedEvents(Action<GoldChanged> action)
        {
            GoldChangedEvents += action;
        }
        public static void OnGoldChanged(GoldChanged goldChanged)
        {
            MainThreadDispatcher.Enqueue(() =>
            {
                GoldChangedEvents?.Invoke(goldChanged);
            });
        }
    }
}

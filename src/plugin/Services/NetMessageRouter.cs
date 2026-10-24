using BepInEx.Logging;
using OmniBonk.Common.Messages;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Extensions;
using OmniBonk.Helpers;

namespace OmniBonk.Services
{
    /// <summary>
    /// Transport-neutral receive path: applies messages, republishes via <see cref="EventManager"/>,
    /// and on the host forwards them. Peer-scoped messages stay in the transport.
    /// <para>Host and client arms are separate on purpose, so a forward cannot leak into the client arm.</para>
    /// </summary>
    public interface INetMessageRouter
    {
        /// <summary>Returns false for an unknown type.</summary>
        bool Route(IGameNetworkMessage message, bool isHost);
    }

    internal class NetMessageRouter(
        IPlayerManagerService playerManagerService,
        IReadinessService readinessService,
        IEncounterService encounterService,
        ManualLogSource logger) : INetMessageRouter
    {
        /// <summary>Resolved lazily: injecting it would be a DI cycle (this deadlocked startup once).</summary>
        private INetTransport transport;

        private INetTransport Transport =>
            transport ??= Plugin.Services.GetService(typeof(INetTransport)) as INetTransport;

        public bool Route(IGameNetworkMessage message, bool isHost) =>
            isHost ? RouteOnHost(message) : RouteOnClient(message);

        // ------------------------------------------------------------------ client

        private bool RouteOnClient(IGameNetworkMessage message)
        {
            switch (message)
            {
                case LobbyUpdates lobbyUpdate:
                    OnLobbyUpdate(lobbyUpdate);
                    break;
                case PlayersStateUpdate playersStateUpdate:
                    OnPlayersStateUpdate(playersStateUpdate);
                    break;
                case ProjectilesUpdate projectilesUpdate:
                    EventManager.OnProjectilesUpdate(projectilesUpdate.Projectiles);
                    break;
                case SpawnedObject spawnedObject:
                    EventManager.OnSpawnedObject(spawnedObject);
                    break;
                case SpawnedEnemy spawnedEnemy:
                    EventManager.OnSpawnedEnemy(spawnedEnemy);
                    break;
                case EnemyMaxHpScaled enemyMaxHpScaled:
                    EventManager.OnEnemyMaxHpScaled(enemyMaxHpScaled);
                    break;
                case PlayerStatsChanged playerStatsChanged:
                    EventManager.OnPlayerStatsChanged(playerStatsChanged);
                    break;
                case StateDigest stateDigest:
                    EventManager.OnStateDigest(stateDigest);
                    break;
                case LobbySettings lobbySettings:
                    EventManager.OnLobbySettings(lobbySettings);
                    break;
                case EndlessNextRoom endlessNextRoom:
                    EventManager.OnEndlessNextRoom(endlessNextRoom);
                    break;
                case ChargeShrineCompleted chargeShrineCompleted:
                    EventManager.OnChargeShrineCompleted(chargeShrineCompleted);
                    break;
                case AbstractSpawnedProjectile spawnedProjectile:
                    EventManager.OnSpawnedProjectile(spawnedProjectile);
                    break;
                case SelectedCharacter selectedCharacter:
                    EventManager.OnSelectedCharacter(selectedCharacter);
                    break;
                case EnemyDied enemyDied:
                    EventManager.OnEnemyDied(enemyDied);
                    break;
                case ProjectileDone projectileDone:
                    EventManager.OnProjectileDone(projectileDone);
                    break;
                case SpawnedPickupOrb spawnedPickup:
                    EventManager.OnSpawnedPickupOrb(spawnedPickup);
                    break;
                case SpawnedPickup spawnedPickupItem:
                    EventManager.OnSpawnedPickup(spawnedPickupItem);
                    break;
                case PickupFollowingPlayer pickupFollowingPlayer:
                    EventManager.OnPickupFollowingPlayer(pickupFollowingPlayer);
                    break;
                case PickupApplied pickupApplied:
                    EventManager.OnPickupApplied(pickupApplied);
                    break;
                case SpawnedChest spawnedChest:
                    EventManager.OnSpawnedChest(spawnedChest);
                    break;
                case ChestOpened chestOpened:
                    EventManager.OnChestOpened(chestOpened);
                    break;
                case WeaponAdded weaponAdded:
                    EventManager.OnWeaponAdded(weaponAdded);
                    break;
                case InteractableUsed interactableUsed:
                    EventManager.OnInteractableUsed(interactableUsed);
                    break;
                case StartingChargingShrine startingChargingShrine:
                    EventManager.OnStartingChargingShrine(startingChargingShrine);
                    break;
                case StoppingChargingShrine stoppingChargingShrine:
                    EventManager.OnStoppingChargingShrine(stoppingChargingShrine);
                    break;
                case EnemyExploder enemyExploder:
                    EventManager.OnEnemyExploder(enemyExploder);
                    break;
                case EnemyDamaged enemyDamaged:
                    EventManager.OnEnemyDamaged(enemyDamaged);
                    break;
                case EnemyDamagedBatch enemyDamagedBatch:
                    EventManager.OnEnemyDamagedBatch(enemyDamagedBatch);
                    break;
                case ItemProjectilesSpawned itemProjectilesSpawned:
                    EventManager.OnItemProjectilesSpawned(itemProjectilesSpawned);
                    break;
                case MineExploded mineExploded:
                    EventManager.OnMineExploded(mineExploded);
                    break;
                case FlaskExploded flaskExploded:
                    EventManager.OnFlaskExploded(flaskExploded);
                    break;
                case EnemyHitEffects enemyHitEffects:
                    EventManager.OnEnemyHitEffects(enemyHitEffects);
                    break;
                case ItemVisualEffects itemVisualEffects:
                    EventManager.OnItemVisualEffects(itemVisualEffects);
                    break;
                case SpawnedEnemySpecialAttack spawnedEnemySpecialAttack:
                    EventManager.OnSpawnedEnemySpecialAttack(spawnedEnemySpecialAttack);
                    break;
                case StartingChargingPylon startingChargingPylon:
                    EventManager.OnStartingChargingPylon(startingChargingPylon);
                    break;
                case StoppingChargingPylon stoppingChargingPylon:
                    EventManager.OnStoppingChargingPylon(stoppingChargingPylon);
                    break;
                case FinalBossOrbSpawned finalBossOrbSpawned:
                    EventManager.OnFinalBossOrbSpawned(finalBossOrbSpawned);
                    break;
                case FinalBossOrbDestroyed finalBossOrbDestroyed:
                    EventManager.OnFinalBossOrbDestroyed(finalBossOrbDestroyed);
                    break;
                case StartedSwarmEvent startedSwarmEvent:
                    EventManager.OnStartedSwarmEvent(startedSwarmEvent);
                    break;
                case GameOver gameOver:
                    EventManager.OnGameOver(gameOver);
                    break;
                case RetargetedEnemies retargetedEnemies:
                    EventManager.OnRetargetedEnemies(retargetedEnemies);
                    break;
                case RunStarted runStarted:
                    EventManager.OnRunStarted(runStarted);
                    break;
                case TomeAdded tomeAdded:
                    EventManager.OnTomeAdded(tomeAdded);
                    break;
                case LightningStrike lightningStrike:
                    EventManager.OnLightningStrike(lightningStrike);
                    break;
                case TornadoesSpawned tornadoesSpawned:
                    EventManager.OnTornadoesSpawned(tornadoesSpawned);
                    break;
                case StormStarted stormStarted:
                    EventManager.OnStormStarted(stormStarted);
                    break;
                case StormStopped stormStopped:
                    EventManager.OnStormStopped(stormStopped);
                    break;
                case TumbleWeedSpawned tumbleWeedSpawned:
                    EventManager.OnTumbleWeedSpawned(tumbleWeedSpawned);
                    break;
                case TumbleWeedsUpdate tumbleWeedsUpdate:
                    EventManager.OnTumbleWeedsUpdate(tumbleWeedsUpdate.TumbleWeeds);
                    break;
                case TumbleWeedDespawned tumbleWeedDespawned:
                    EventManager.OnTumbleWeedDespawned(tumbleWeedDespawned);
                    break;
                case ItemAdded itemAdded:
                    EventManager.OnItemAdded(itemAdded);
                    break;
                case ItemRemoved itemRemoved:
                    EventManager.OnItemRemoved(itemRemoved);
                    break;
                case WeaponToggled weaponToggled:
                    EventManager.OnWeaponToggled(weaponToggled);
                    break;
                case SpawnedObjectInCrypt spawnedObjectInCrypt:
                    EventManager.OnSpawnedObjectInCrypt(spawnedObjectInCrypt);
                    break;
                case StartingChargingLamp startingChargingLamp:
                    EventManager.OnStartingChargingLamp(startingChargingLamp);
                    break;
                case StoppingChargingLamp stoppingChargingLamp:
                    EventManager.OnStoppingChargingLamp(stoppingChargingLamp);
                    break;
                case TimerStarted timerStarted:
                    EventManager.OnTimerStarted(timerStarted);
                    break;
                case HatChanged hatChanged:
                    EventManager.OnHatChanged(hatChanged);
                    break;
                case SpawnedReviver spawnedReviver:
                    EventManager.OnSpawnedReviver(spawnedReviver);
                    break;
                case PlayerRespawned playerRespawned:
                    EventManager.OnPlayerRespawned(playerRespawned);
                    break;
                case PlayerDied playerDied:
                    EventManager.OnPlayerDied(playerDied);
                    break;
                case AddXp addXp:
                    EventManager.OnAddXp(addXp);
                    break;
                case CloseEncounterStamped closeEncounterStamped:
                    EventManager.OnCloseEncounterStamped(closeEncounterStamped);
                    break;
                case ReadinessRoundStarted readinessRoundStarted:
                    EventManager.OnReadinessRoundStarted(readinessRoundStarted);
                    break;
                case LobbyReadyState lobbyReadyState:
                    EventManager.OnLobbyReadyState(lobbyReadyState);
                    break;
                case LobbyRoster lobbyRoster:
                    EventManager.OnLobbyRoster(lobbyRoster);
                    break;
                case ProtocolHello protocolHello:
                    EventManager.OnProtocolHello(protocolHello);
                    break;
                case LobbyStartRequested:
                    EventManager.OnLobbyStartRequested();
                    break;
                case CloseEncounter closeEncounter:
                    EventManager.OnCloseEncounter(closeEncounter);
                    break;
                case GoldChanged goldChanged:
                    EventManager.OnGoldChanged(goldChanged);
                    break;
                default:
                    return false;
            }

            return true;
        }

        // ------------------------------------------------------------------ host

        private bool RouteOnHost(IGameNetworkMessage message)
        {
            switch (message)
            {
                case LobbyReadyChanged lobbyReadyChanged:
                    EventManager.OnLobbyReadyChanged(lobbyReadyChanged);
                    break;
                case ItemProjectilesSpawned itemProjectilesSpawned:
                    EventManager.OnItemProjectilesSpawned(itemProjectilesSpawned);
                    break;
                case ItemVisualEffects itemVisualEffects:
                    EventManager.OnItemVisualEffects(itemVisualEffects);
                    break;
                case ProtocolHello protocolHello:
                    EventManager.OnProtocolHello(protocolHello);
                    break;
                case ClientReadyStamped clientReadyStamped:
                    OnClientReadyStamped(clientReadyStamped);
                    break;
                case ClientInGameReady clientInGameReady:
                    OnClientInGameReady(clientInGameReady);
                    break;
                case PlayerUpdate playerUpdate:
                    OnPlayerUpdate(playerUpdate);
                    break;
                case AbstractSpawnedProjectile spawnedProjectile:
                    EventManager.OnSpawnedProjectile(spawnedProjectile);
                    Transport.SendToAllClientsExcept(spawnedProjectile.OwnerId, spawnedProjectile);
                    break;
                case ProjectileDone projectileDone:
                    EventManager.OnProjectileDone(projectileDone);
                    Transport.SendToAllClientsExcept(projectileDone.SenderConnectionId, projectileDone);
                    break;
                case EnemyDied enemyDied:
                    EventManager.OnEnemyDied(enemyDied);
                    // OB-51: to every client. DiedByOwnerId is the killer, not the sender; the echo is harmless.
                    Transport.SendToAllClients(enemyDied, NetDelivery.ReliableOrdered);
                    break;
                case PickupApplied pickupApplied:
                    EventManager.OnPickupApplied(pickupApplied);
                    Transport.SendToAllClientsExcept(pickupApplied.OwnerId, pickupApplied);
                    break;
                case PickupFollowingPlayer pickupFollowingPlayer:
                    EventManager.OnPickupFollowingPlayer(pickupFollowingPlayer);
                    Transport.SendToAllClientsExcept(pickupFollowingPlayer.PlayerId, pickupFollowingPlayer);
                    break;
                case ChestOpened chestOpened:
                    EventManager.OnChestOpened(chestOpened);
                    Transport.SendToAllClientsExcept(chestOpened.OwnerId, chestOpened);
                    break;
                case WeaponAdded weaponAdded:
                    EventManager.OnWeaponAdded(weaponAdded);
                    Transport.SendToAllClientsExcept(weaponAdded.OwnerId, weaponAdded);
                    break;
                // OB-1. Applied here and relayed on, so a third peer learns this player's stats too.
                case PlayerStatsChanged playerStatsChanged:
                    EventManager.OnPlayerStatsChanged(playerStatsChanged);
                    Transport.SendToAllClientsExcept(playerStatsChanged.OwnerId, playerStatsChanged);
                    break;
                case InteractableUsed interactableUsed:
                    EventManager.OnInteractableUsed(interactableUsed);
                    Transport.SendToAllClientsExcept(interactableUsed.OwnerId, interactableUsed);
                    break;
                case StartingChargingShrine startingChargingShrine:
                    EventManager.OnStartingChargingShrine(startingChargingShrine);
                    break;
                case StoppingChargingShrine stoppingChargingShrine:
                    EventManager.OnStoppingChargingShrine(stoppingChargingShrine);
                    break;
                case EnemyExploder enemyExploder:
                    EventManager.OnEnemyExploder(enemyExploder);
                    Transport.SendToAllClientsExcept(enemyExploder.SenderId, enemyExploder);
                    break;
                case EnemyDamaged enemyDamaged:
                    // Not forwarded here: Damage_Postfix already forwards; a second forward doubled hits.
                    EventManager.OnEnemyDamaged(enemyDamaged);
                    break;
                case StartingChargingPylon startingChargingPylon:
                    EventManager.OnStartingChargingPylon(startingChargingPylon);
                    Transport.SendToAllClientsExcept(startingChargingPylon.PlayerChargingId, startingChargingPylon);
                    break;
                case StoppingChargingPylon stoppingChargingPylon:
                    EventManager.OnStoppingChargingPylon(stoppingChargingPylon);
                    Transport.SendToAllClientsExcept(stoppingChargingPylon.PlayerChargingId, stoppingChargingPylon);
                    break;
                case FinalBossOrbDestroyed finalBossOrbDestroyed:
                    EventManager.OnFinalBossOrbDestroyed(finalBossOrbDestroyed);
                    Transport.SendToAllClientsExcept(finalBossOrbDestroyed.SenderId, finalBossOrbDestroyed);
                    break;
                case PlayerDied playerDied:
                    EventManager.OnPlayerDied(playerDied);
                    break;
                case TomeAdded tomeAdded:
                    EventManager.OnTomeAdded(tomeAdded);
                    Transport.SendToAllClientsExcept(tomeAdded.OwnerId, tomeAdded);
                    break;
                case InteractableCharacterFightEnemySpawned interactableCharacterFightEnemySpawned:
                    EventManager.OnInteractableCharacterFightEnemySpawned(interactableCharacterFightEnemySpawned);
                    break;
                case WantToStartFollowingPickup wantToStartFollowingPickup:
                    EventManager.OnWantToStartFollowingPickup(wantToStartFollowingPickup);
                    break;
                case ItemAdded itemAdded:
                    EventManager.OnItemAdded(itemAdded);
                    Transport.SendToAllClientsExcept(itemAdded.OwnerId, itemAdded);
                    break;
                case ItemRemoved itemRemoved:
                    EventManager.OnItemRemoved(itemRemoved);
                    Transport.SendToAllClientsExcept(itemRemoved.OwnerId, itemRemoved);
                    break;
                case WeaponToggled weaponToggled:
                    EventManager.OnWeaponToggled(weaponToggled);
                    Transport.SendToAllClientsExcept(weaponToggled.OwnerId, weaponToggled);
                    break;
                case StartingChargingLamp startingChargingLamp:
                    EventManager.OnStartingChargingLamp(startingChargingLamp);
                    Transport.SendToAllClientsExcept(startingChargingLamp.PlayerChargingId, startingChargingLamp);
                    break;
                case StoppingChargingLamp stoppingChargingLamp:
                    EventManager.OnStoppingChargingLamp(stoppingChargingLamp);
                    Transport.SendToAllClientsExcept(stoppingChargingLamp.PlayerChargingId, stoppingChargingLamp);
                    break;
                case TimerStarted timerStarted:
                    EventManager.OnTimerStarted(timerStarted);
                    Transport.SendToAllClientsExcept(timerStarted.SenderId, timerStarted);
                    break;
                case HatChanged hatChanged:
                    EventManager.OnHatChanged(hatChanged);
                    Transport.SendToAllClientsExcept(hatChanged.OwnerId, hatChanged);
                    break;
                case AddXp addXp:
                    EventManager.OnAddXp(addXp);
                    Transport.SendToAllClientsExcept(addXp.OwnerId, addXp);
                    break;
                // OB-6. A client whose bar filled first; the host decides and broadcasts.
                case ChargeShrineCompleted chargeShrineCompleted:
                    EventManager.OnChargeShrineCompleted(chargeShrineCompleted);
                    break;
                case EncounterClosedStamped encounterClosedStamped:
                    OnEncounterClosedStamped(encounterClosedStamped);
                    break;
                case EncounterClosed encounterClosed:
                    OnEncounterClosed(encounterClosed);
                    break;
                case GoldChanged goldChanged:
                    EventManager.OnGoldChanged(goldChanged);
                    Transport.SendToAllClientsExcept(goldChanged.OwnerId, goldChanged);
                    break;
                default:
                    return false;
            }

            return true;
        }

        // ------------------------------------------------------------------ host handlers

        private void OnClientReadyStamped(ClientReadyStamped clientReadyStamped)
        {
            var stampedReadyId = clientReadyStamped.ConnectionId;
            var stampedPlayer = playerManagerService.GetPlayer(stampedReadyId);
            if (stampedPlayer == null)
            {
                Plugin.Log.LogWarning($"[readiness] Report from unknown connection {stampedReadyId}.");
                return;
            }

            // Lobby-ready defect B: reject reports for another round; safe only because the client retries.
            if (!readinessService.TryMarkReady(stampedReadyId, clientReadyStamped.SessionId, clientReadyStamped.RoundId))
            {
                logger.LogInfo(
                    $"[readiness] Dropped a report from {stampedReadyId} for session " +
                    $"{clientReadyStamped.SessionId} round {clientReadyStamped.RoundId}; host has " +
                    $"session {readinessService.SessionId} round {readinessService.RoundId} open.");

                // Re-send the stamp; the peer likely has not heard of this round yet.
                EventManager.OnReadinessRoundReAsk();
                return;
            }

            // Derived mirror for UI and broadcast; the barrier holds the truth.
            stampedPlayer.IsReady = true;
            playerManagerService.UpdatePlayer(stampedPlayer);
        }

        private void OnClientInGameReady(ClientInGameReady clientInGameReady)
        {
            // Older build: unstamped, accepted as before.
            var clientReadyId = clientInGameReady.ConnectionId;
            var player = playerManagerService.GetPlayer(clientReadyId);
            if (player == null)
            {
                Plugin.Log.LogWarning($"Received ClientReady from unknown player with connection ID {clientReadyId}.");
                return;
            }

            Plugin.Log.LogWarning(
                $"[readiness] Unstamped report from {clientReadyId}. That peer is on an older " +
                "build; this report cannot be round-attributed (lobby-ready defect B).");

            readinessService.TryMarkReady(clientReadyId, readinessService.SessionId, readinessService.RoundId);

            player.IsReady = true;
            playerManagerService.UpdatePlayer(player);

            Plugin.Log.LogInfo($"Player {clientReadyId} is ready.");
        }

        private void OnPlayerUpdate(PlayerUpdate playerUpdate)
        {
            var playerUpdateId = playerUpdate.ConnectionId;
            var playerToUpdate = playerManagerService.GetPlayer(playerUpdateId);
            if (playerToUpdate == null)
            {
                Plugin.Log.LogWarning($"Received PlayerUpdate from unknown player with connection ID {playerUpdateId}.");
                return;
            }

            playerToUpdate.Position = Quantizer.Quantize(playerUpdate.Position.ToUnityVector3());
            playerToUpdate.MovementState = playerUpdate.MovementState;
            playerToUpdate.AnimatorState = playerUpdate.AnimatorState;
            playerToUpdate.ConnectionId = playerUpdate.ConnectionId;
            playerToUpdate.Hp = playerUpdate.Hp;
            playerToUpdate.Shield = playerUpdate.Shield;
            playerToUpdate.MaxHp = playerUpdate.MaxHp;
            playerToUpdate.MaxShield = playerUpdate.MaxShield;
            playerToUpdate.Inventory = playerUpdate.Inventory;
            playerToUpdate.Name = playerUpdate.Name;

            playerManagerService.UpdatePlayer(playerToUpdate);

            EventManager.OnPlayerUpdate(playerUpdate);
        }

        private void OnEncounterClosedStamped(EncounterClosedStamped encounterClosedStamped)
        {
            // SE-5: a stale-round report would release someone else's window early.
            if (!encounterService.IsCurrentStamp(encounterClosedStamped.SessionId, encounterClosedStamped.RoundId))
            {
                logger.LogInfo(
                    $"Dropping a stale barrier report from {encounterClosedStamped.OwnerId} " +
                    $"(session {encounterClosedStamped.SessionId}, round {encounterClosedStamped.RoundId}); " +
                    $"host is on session {encounterService.SessionId}, round {encounterService.RoundId}.");
                return;
            }

            encounterService.AddClosedEncounterForPlayer(encounterClosedStamped.OwnerId);

            logger.LogInfo(
                $"[barrier] Report from {encounterClosedStamped.OwnerId} accepted for round " +
                $"{encounterClosedStamped.RoundId}; barrier closable: {encounterService.IsClosable()}.");

            if (encounterService.IsClosable())
            {
                EventManager.OnReleaseBarrier();
            }
        }

        private void OnEncounterClosed(EncounterClosed encounterClosed)
        {
            // Older build: unstamped, accepted as before SE-5.
            Plugin.Log.LogWarning(
                $"Received an unstamped EncounterClosed from {encounterClosed.OwnerId}. That peer is " +
                "on an older build; this report cannot be round-attributed (SE-5).");

            encounterService.AddClosedEncounterForPlayer(encounterClosed.OwnerId);

            if (encounterService.IsClosable())
            {
                EventManager.OnReleaseBarrier();
            }
        }

        // ------------------------------------------------------------------ client handlers

        /// <summary>
        /// Continuous player fields only, mutated in place. <b>Do not</b> turn this into
        /// <c>UpdatePlayer</c>: it would overwrite IsReady/Name/Inventory at 60 Hz (lobby-ready defect C).
        /// </summary>
        private void OnPlayersStateUpdate(PlayersStateUpdate update)
        {
            foreach (var state in update.States)
            {
                var player = playerManagerService.GetPlayer(state.ConnectionId);
                if (player == null)
                {
                    continue;
                }

                player.Position = state.Position;
                player.AnimatorState = state.AnimatorState;
                player.MovementState = state.MovementState;
                player.Hp = state.Hp;
                player.Shield = state.Shield;

                EventManager.OnPlayerUpdate(new PlayerUpdate
                {
                    Position = Quantizer.Dequantize(state.Position).ToNumericsVector3(),
                    MovementState = state.MovementState,
                    AnimatorState = state.AnimatorState,
                    ConnectionId = state.ConnectionId,
                    Hp = state.Hp,
                    // Held values, so MaxHp never reads zero between full records.
                    MaxHp = player.MaxHp,
                    Shield = state.Shield,
                    MaxShield = player.MaxShield,
                    Name = player.Name,
                    Inventory = player.Inventory,
                });
            }
        }

        private void OnLobbyUpdate(LobbyUpdates lobbyUpdate)
        {
            foreach (var player in lobbyUpdate.Players)
            {
                var existingPlayer = playerManagerService.GetPlayer(player.ConnectionId);
                if (existingPlayer == null)
                {
                    continue;
                }

                playerManagerService.UpdatePlayer(player);

                EventManager.OnPlayerUpdate(new PlayerUpdate
                {
                    Position = Quantizer.Dequantize(player.Position).ToNumericsVector3(),
                    MovementState = player.MovementState,
                    AnimatorState = player.AnimatorState,
                    ConnectionId = player.ConnectionId,
                    Hp = player.Hp,
                    MaxHp = player.MaxHp,
                    Shield = player.Shield,
                    MaxShield = player.MaxShield,
                    Name = player.Name,
                    Inventory = player.Inventory,
                });
            }

            // Required: the client's entire enemy and boss-orb stream; losing it fails silently.
            EventManager.OnEnemiesUpdate(lobbyUpdate.Enemies);
            EventManager.OnFinalBossOrbsUpdate(lobbyUpdate.BossOrbs);
        }
    }
}

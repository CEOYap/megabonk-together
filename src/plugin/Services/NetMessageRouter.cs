using BepInEx.Logging;
using MegabonkTogether.Common.Messages;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Extensions;
using MegabonkTogether.Helpers;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Turns a received message into gameplay: applies it to the replicated model where the host
    /// owns that, republishes it through <see cref="EventManager"/>, and — on the host — forwards it
    /// to the other clients.
    ///
    /// <para><b>Why this is not in the transport any more.</b> The receive switch lived in
    /// <c>UdpClientService</c> and was keyed on a LiteNetLib <c>NetPeer.Id</c>, so a second
    /// transport had nowhere to deliver anything: it could receive bytes and deserialize them, and
    /// then had no way to make them mean something. Phase 1 moved the *send* side behind
    /// <see cref="INetTransport"/> and left the receive side where it was; this is the other half.
    /// See <c>docs/steamworks/00-migration-plan.md</c>, Phase 4.</para>
    ///
    /// <para><b>What deliberately did not move, and why that is not a loose end.</b> Eight cases
    /// need transport state that has no transport-neutral meaning — the peer introduction maps,
    /// the relay peer, and tearing a connection down. Those stay in the transport, which tries them
    /// first and calls this for everything else. The split is not "logic here, plumbing there": it
    /// is <b>messages whose handling depends on which peer they arrived on</b> versus messages
    /// whose handling depends only on their contents. The second group is every message but eight,
    /// and only the first group can differ between transports.</para>
    ///
    /// <para><b>Host and client arms are separate on purpose.</b> Roughly a dozen message types
    /// appear in both with different bodies — a client applies a spawn, a host applies it <i>and</i>
    /// forwards it to everyone except the peer that reported it. Collapsing them would mean a
    /// runtime branch inside each case, which is how a forward gets added to the client arm by
    /// accident and every peer starts rebroadcasting.</para>
    /// </summary>
    public interface INetMessageRouter
    {
        /// <summary>
        /// Applies a message. Returns false if this router does not know the type, which is the
        /// caller's cue to log it — the transport has already had its chance at the message.
        /// </summary>
        bool Route(IGameNetworkMessage message, bool isHost);
    }

    internal class NetMessageRouter(
        IPlayerManagerService playerManagerService,
        IReadinessService readinessService,
        IEncounterService encounterService,
        ManualLogSource logger) : INetMessageRouter
    {
        /// <summary>
        /// Resolved on first use rather than injected. The transport implements
        /// <see cref="INetTransport"/> and constructs this router, so taking it as a constructor
        /// parameter is a cycle the container cannot break — the same shape that already deadlocked
        /// startup once and is why <c>LobbyViewService</c> subscribes explicitly after the host is
        /// built. Resolved once and cached; never per message.
        /// </summary>
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
                    Transport.SendToAllClientsExcept(enemyDied.DiedByOwnerId, enemyDied);
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
                    EventManager.OnEnemyDamaged(enemyDamaged);
                    Transport.SendToAllClientsExcept(enemyDamaged.AttackerId, enemyDamaged);
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

            // Lobby-ready defect B. A report that does not name the round this host has open is one
            // that raced ahead of the host's own level transition. Recording it is what used to hang
            // the lobby: ResetForNextLevel then cleared it, and a client that sent exactly once
            // never reported again. Rejecting it is only safe because the client retries — the two
            // halves are one fix.
            if (!readinessService.TryMarkReady(stampedReadyId, clientReadyStamped.SessionId, clientReadyStamped.RoundId))
            {
                logger.LogInfo(
                    $"[readiness] Dropped a report from {stampedReadyId} for session " +
                    $"{clientReadyStamped.SessionId} round {clientReadyStamped.RoundId}; host has " +
                    $"session {readinessService.SessionId} round {readinessService.RoundId} open.");

                // Re-announce rather than stay silent. A mismatch usually means this peer is
                // reporting against a round it has not been told about yet, and the one thing that
                // resolves it is the stamp — which is cheaper to re-send than to let the peer burn
                // its retry budget.
                EventManager.OnReadinessRoundReAsk();
                return;
            }

            // Mirrored onto the replicated record so the existing UI, the 5 Hz full player
            // broadcast, and the client's own acknowledgement check all keep working unchanged. This
            // is now a derived value: the barrier holds the truth, and a clobber of this field costs
            // a re-report, not the round.
            stampedPlayer.IsReady = true;
            playerManagerService.UpdatePlayer(stampedPlayer);
        }

        private void OnClientInGameReady(ClientInGameReady clientInGameReady)
        {
            // Older build on the other end: no round stamp, so the report cannot be attributed and
            // is accepted exactly as it was before, defect B included.
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
            // SE-5, report half. A report that does not name the round this host has open is a
            // leftover from a round already released — counting it toward the current round is what
            // releases someone else's window before they have chosen. Dropped rather than applied;
            // the reporting peer's own failsafe is what recovers it if it really is stuck.
            if (!encounterService.IsCurrentStamp(encounterClosedStamped.SessionId, encounterClosedStamped.RoundId))
            {
                logger.LogInfo(
                    $"Dropping a stale barrier report from {encounterClosedStamped.OwnerId} " +
                    $"(session {encounterClosedStamped.SessionId}, round {encounterClosedStamped.RoundId}); " +
                    $"host is on session {encounterService.SessionId}, round {encounterService.RoundId}.");
                return;
            }

            encounterService.AddClosedEncounterForPlayer(encounterClosedStamped.OwnerId);

            // The accepted counterpart of the "Dropping a stale barrier report" line above. Without
            // it a healthy round is invisible and only failures speak, which is what made the first
            // run of this build unverifiable.
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
            // Older build on the other end: no round identity, so this report cannot be attributed
            // and is accepted as-is, exactly as it was before SE-5.
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
        /// Applies the continuous half of the player stream. Counterpart of
        /// <c>SendPlayersStateUpdate</c>.
        ///
        /// <para><b>This deliberately mutates only the continuous fields</b> rather than replacing
        /// the stored record the way <see cref="OnLobbyUpdate"/> does. That is the point of the
        /// split and it is also a fix: <c>UpdatePlayer</c> overwrites the whole <c>Player</c>, so at
        /// 60 Hz the old single stream was continuously stamping <c>IsReady</c>, <c>Name</c>,
        /// <c>Skin</c> and <c>Inventory</c> back over whatever local code had just set — defect C of
        /// the four lobby-ready barrier defects. Those fields now only ever change when a full
        /// record arrives, so a readiness flag set locally survives until the host actually
        /// contradicts it.</para>
        ///
        /// <para>Mutating in place is safe because <c>GetPlayer</c> hands back the stored instance,
        /// and it avoids the remove/insert churn <c>UpdatePlayer</c> does on a 60 Hz path. <b>Do not
        /// "tidy" this into an UpdatePlayer call</b> — that is precisely the defect above.</para>
        /// </summary>
        private void OnPlayersStateUpdate(PlayersStateUpdate update)
        {
            foreach (var state in update.States)
            {
                var player = playerManagerService.GetPlayer(state.ConnectionId);
                if (player == null)
                {
                    // GetPlayer already reports this, throttled. A state update for a player we do
                    // not know yet is normal for a tick or two around join and disconnect.
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
                    // Maxima and identity ride the full record; carry the values we already hold so
                    // a health bar reading MaxHp off this update does not see a zero between full
                    // records.
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

            // Outside the loop, and not optional: these two carry the entire enemy and boss-orb
            // stream on a client. They are easy to lose when this method is moved, and losing them
            // does not fail — every enemy simply stops moving on every client but the host.
            EventManager.OnEnemiesUpdate(lobbyUpdate.Enemies);
            EventManager.OnFinalBossOrbsUpdate(lobbyUpdate.BossOrbs);
        }
    }
}

// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using Assets.Scripts.Utility;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Actors.Player;
using OmniBonk.Configuration;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Scripts.Bot
{
    /// <summary>
    /// The test bot. Writes only to <see cref="BotInput"/>, never to sync services, so its actions
    /// replicate like a player's. Inert unless <see cref="ModConfig.BotMode"/> is on.
    /// </summary>
    public class BotController : MonoBehaviour
    {
        internal enum BotState { Idle, Joining, InLobby, Following, Detouring, Reviving, Rewarding }

        internal static BotController Instance;

        internal BotState State = BotState.Idle;
        internal int RewardsPicked;
        internal int InteractablesUsed;
        internal int Revives;
        internal int Teleports;

        private ISynchronizationService synchronizationService;
        private IPlayerManagerService playerManagerService;
        private INetplaySessionService netplaySessionService;
        private ILobbyViewService lobbyViewService;
        private float nextJoinCheckTime;
        private float menuSince = -1f;
        private float joinStartTime;

        /// <summary>How long the main menu must have been up before the bot joins.</summary>
        private const float MENU_SETTLE_SECONDS = 3f;

        /// <summary>A join that has not reached the lobby by then is cancelled and retried.</summary>
        private const float JOIN_TIMEOUT_SECONDS = 30f;
        private float nextLobbyActionTime;
        private bool characterChosen;
        private int confirmPresses;

        // Following.
        private float nextOwnerLookupTime;
        private Transform ownerTransform;
        private bool loggedNoOwner;
        private Vector3? steerTarget;
        private float bestTargetDistance = float.MaxValue;
        private float lastProgressTime;

        // Rewarding.
        private BotState stateBeforeReward = BotState.Following;
        private bool actedOnWindow;
        private float rewardStartTime;
        private bool loggedRewardStall;

        /// <summary>Seconds in one reward window before the bot falls back to taking or skipping.</summary>
        private const float REWARD_STALL_SECONDS = 15f;

        // Detouring and reviving.
        private ISpawnedObjectManagerService spawnedObjectManagerService;
        private DetectInteractables detect;
        private BaseInteractable detourTarget;
        private float detourStartTime;
        private Interactables.InteractableReviver coffin;
        private Vector3 coffinPosition;
        private bool pressedCoffin;
        private float nextCoffinLookupTime;
        private bool detoursDisabled;

        private bool announced;
        private float lastStatusTime = -999f;

        /// <summary>The host's avatar position, or null when there is no host avatar.</summary>
        internal Vector3? OwnerPosition { get; private set; }

        public void Awake()
        {
            Instance = this;
            synchronizationService = Plugin.Services.GetRequiredService<ISynchronizationService>();
            playerManagerService = Plugin.Services.GetRequiredService<IPlayerManagerService>();
            netplaySessionService = Plugin.Services.GetRequiredService<INetplaySessionService>();
            lobbyViewService = Plugin.Services.GetRequiredService<ILobbyViewService>();
            spawnedObjectManagerService = Plugin.Services.GetRequiredService<ISpawnedObjectManagerService>();
        }

        public void Update()
        {
            if (!ModConfig.BotMode.Value)
            {
                return;
            }

            if (!announced)
            {
                announced = true;
                // An unfocused player may pause; the FPS cap leaves the PC to the owner's copy.
                Application.runInBackground = true;
                Application.targetFrameRate = 30;
                Plugin.Log.LogInfo("[bot] BotMode on: runInBackground, 30 FPS cap.");
            }

            UpdateSessionState();

            switch (State)
            {
                case BotState.Idle:
                    TryJoin();
                    break;
                case BotState.Joining:
                case BotState.InLobby:
                    TickLobby();
                    break;
                case BotState.Following:
                case BotState.Detouring:
                case BotState.Reviving:
                    if (!TryEnterRewarding())
                    {
                        TickInRun();
                    }

                    break;
                case BotState.Rewarding:
                    TickRewarding();
                    break;
            }

            LogStatus();
        }

        /// <summary>On the main menu, joins the room the host's copy wrote to the room-code file.</summary>
        private void TryJoin()
        {
            if (Time.unscaledTime < nextJoinCheckTime)
            {
                return;
            }

            nextJoinCheckTime = Time.unscaledTime + 1f;

            // Only from a settled main menu: opening "Menu" runs ResetNetworking and kills an early join.
            if (WindowManager.activeWindow == null || WindowManager.activeWindow.name != "Menu")
            {
                menuSince = -1f;
                return;
            }

            if (menuSince < 0f)
            {
                menuSince = Time.unscaledTime;
            }

            if (Time.unscaledTime - menuSince < MENU_SETTLE_SECONDS
                || netplaySessionService.IsBusy
                || synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            // Via JoinLobby so a lobby panel (with its Ready button) exists; it no-ops while a panel is up.
            var playTogether = Plugin.Instance.PlayTogetherButton;
            if (playTogether == null || Modal.LobbyPanel.Current != null)
            {
                return;
            }

            var code = Helpers.BotRoomCodeFile.ReadFresh(System.TimeSpan.FromMinutes(10));
            if (code == null)
            {
                return;
            }

            // The file is kept until the bot reaches the lobby, so a failed join can retry.
            State = BotState.Joining;
            joinStartTime = Time.unscaledTime;
            Plugin.Log.LogInfo($"[bot] joining room {code}.");
            playTogether.JoinLobby(code);
        }

        /// <summary>Gives up on a join that has not reached the lobby, so the bot can try again.</summary>
        private void CheckJoinTimeout()
        {
            if (Time.unscaledTime - joinStartTime < JOIN_TIMEOUT_SECONDS)
            {
                return;
            }

            Plugin.Log.LogWarning($"[bot] join did not reach the lobby in {JOIN_TIMEOUT_SECONDS:F0}s; cancelling and retrying.");
            netplaySessionService.Cancel();

            // A retry waits for no panel, so remove the one the join opened.
            var panel = Modal.LobbyPanel.Current;
            if (panel != null)
            {
                GameObject.Destroy(panel.gameObject);
            }

            ResetRun();
            State = BotState.Idle;
            nextJoinCheckTime = Time.unscaledTime + 2f;
        }

        /// <summary>Derives the bot state from the session flags, so a dropped session returns to <see cref="BotState.Idle"/>.</summary>
        private void UpdateSessionState()
        {
            var initialized = synchronizationService.HasNetplaySessionInitialized();
            var started = synchronizationService.HasNetplaySessionStarted();

            if (!initialized && !netplaySessionService.IsBusy && State != BotState.Idle)
            {
                if (State != BotState.Joining)
                {
                    Plugin.Log.LogInfo("[bot] session ended, back to idle.");
                }

                ResetRun();
                State = BotState.Idle;
                return;
            }

            // Room-code file not deleted here (other bots need it); the host deletes it at StartGame.
            if (State == BotState.Joining && initialized)
            {
                State = BotState.InLobby;
                Plugin.Log.LogInfo("[bot] in the lobby.");
            }
            else if (State == BotState.Idle && initialized)
            {
                // A session joined by hand or invite.
                State = BotState.InLobby;
                Plugin.Log.LogInfo("[bot] adopting a session it did not start; in the lobby.");
            }
            else if (State == BotState.Joining)
            {
                CheckJoinTimeout();
            }

            if (State == BotState.InLobby && started)
            {
                State = BotState.Following;
                Plugin.Log.LogInfo("[bot] run started, following the host.");
            }
        }

        private void ResetRun()
        {
            characterChosen = false;
            confirmPresses = 0;
            detoursDisabled = false;
            BotInput.Clear();
            OwnerPosition = null;
            ownerTransform = null;
            loggedNoOwner = false;
            detect = null;
            EndDetour();
            EndRevive();
            BotTargets.ResetStage();
            DropTarget();
        }

        /// <summary>The bot's own player, or null between stages and on loading screens.</summary>
        private static MyPlayer LocalPlayer()
        {
            var player = GameManager.Instance?.player;
            return player != null && player.playerMovement != null && player.playerMovement.feet != null ? player : null;
        }

        /// <summary>Finds the host's avatar once a second; null when there is none.</summary>
        private void RefreshOwner()
        {
            if (Time.unscaledTime >= nextOwnerLookupTime || ownerTransform == null)
            {
                nextOwnerLookupTime = Time.unscaledTime + 1f;
                var host = playerManagerService.GetHost();
                var netPlayer = host != null ? playerManagerService.GetNetPlayerByNetplayId(host.ConnectionId) : null;
                ownerTransform = netPlayer != null && netPlayer.Model != null ? netPlayer.Model.transform : null;
            }

            OwnerPosition = ownerTransform != null ? ownerTransform.position : null;
        }

        /// <summary>Following, Detouring and Reviving share the stage-transition handling.</summary>
        private void TickInRun()
        {
            var me = LocalPlayer();
            if (me == null)
            {
                // Stage transition or loading screen: hold still, forget targets; new map next.
                BotInput.Move = Vector2.zero;
                BotInput.CancelInteract();
                detect = null;
                EndDetour();
                EndRevive();
                BotTargets.ResetStage();
                return;
            }

            // Paused (MyTime.Pause does not freeze Time.time) or not started: hold and restart the stuck clocks.
            if (!synchronizationService.HasNetplaySessionStarted() || MyTime.paused)
            {
                BotInput.Move = Vector2.zero;
                BotInput.CancelInteract();
                DropTarget();
                detourStartTime += Time.deltaTime;
                return;
            }

            if (detect == null) // Unity's ==: a destroyed component reads as null and is fetched again
            {
                detect = me.GetComponentInChildren<DetectInteractables>();
            }

            switch (State)
            {
                case BotState.Detouring:
                    TickDetour(me);
                    break;
                case BotState.Reviving:
                    TickRevive(me);
                    break;
                default:
                    TickFollow(me);
                    break;
            }
        }

        private void TickFollow(MyPlayer me)
        {
            RefreshOwner();
            if (!OwnerPosition.HasValue)
            {
                BotInput.Move = Vector2.zero;
                DropTarget();
                if (!loggedNoOwner)
                {
                    loggedNoOwner = true;
                    Plugin.Log.LogInfo("[bot] no owner to follow.");
                }

                return;
            }

            loggedNoOwner = false;
            var owner = OwnerPosition.Value;

            // Reviving the host outranks everything but a reward window.
            if (TryStartRevive())
            {
                return;
            }

            var detour = FindDetourSafely(me.transform.position, owner);
            if (detour != null)
            {
                detourTarget = detour;
                detourStartTime = Time.time;
                State = BotState.Detouring;
                DropTarget();
                return;
            }

            var target = BotSteering.PickFollowPoint(owner, me.transform.position, ModConfig.BotFollowDistance.Value, Time.time);
            SteerTo(me, target, owner);
        }

        /// <summary>Detours are optional; a throw disables them for the session rather than stopping movement.</summary>
        private BaseInteractable FindDetourSafely(Vector3 bot, Vector3 owner)
        {
            if (detoursDisabled)
            {
                return null;
            }

            try
            {
                return BotTargets.FindDetour(bot, owner, 3f * ModConfig.BotFollowDistance.Value, detect);
            }
            catch (System.Exception ex)
            {
                detoursDisabled = true;
                Plugin.Log.LogWarning($"[bot] detour search failed, detours off for this session: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>The game's current interactable for the bot's player, by instance id; null when none.</summary>
        internal int? CurrentInteractableId()
        {
            var current = detect != null ? detect.currentInteractable : null;
            return current != null ? current.GetInstanceID() : null;
        }

        /// <summary>True when <paramref name="target"/> is the interactable the game would use on a press.</summary>
        private bool IsCurrentInteractable(BaseInteractable target)
        {
            var current = detect != null ? detect.currentInteractable : null;
            return current != null && target != null && current.GetInstanceID() == target.GetInstanceID();
        }

        private void TickDetour(MyPlayer me)
        {
            RefreshOwner();
            if (!OwnerPosition.HasValue || detourTarget == null || !detourTarget.CanInteract())
            {
                EndDetour();
                return;
            }

            if (TryStartRevive())
            {
                return;
            }

            if (IsCurrentInteractable(detourTarget))
            {
                BotInput.PressInteract(detourTarget.GetInstanceID());
                BotInput.Move = Vector2.zero;
                InteractablesUsed++;
                Plugin.Log.LogInfo($"[bot] used {detourTarget.gameObject.name}.");
                BotTargets.Blacklist(detourTarget);
                EndDetour();
                return;
            }

            if (Time.time - detourStartTime > 2f * ModConfig.BotStuckSeconds.Value)
            {
                Plugin.Log.LogInfo($"[bot] gave up on {detourTarget.gameObject.name}.");
                BotTargets.Blacklist(detourTarget);
                EndDetour();
                return;
            }

            SteerTo(me, detourTarget.transform.position, OwnerPosition.Value);
        }

        private void EndDetour()
        {
            detourTarget = null;
            if (State == BotState.Detouring)
            {
                State = BotState.Following;
            }

            DropTarget();
        }

        /// <summary>Switches to Reviving when the host is dead and their coffin can be used.</summary>
        private bool TryStartRevive()
        {
            var host = playerManagerService.GetHost();
            if (host == null || host.Hp > 0 || Time.unscaledTime < nextCoffinLookupTime)
            {
                return false;
            }

            nextCoffinLookupTime = Time.unscaledTime + 1f;
            var found = BotTargets.FindOwnerCoffin(host.ConnectionId, spawnedObjectManagerService);
            if (found == null)
            {
                return false;
            }

            detourTarget = null;
            coffin = found;
            coffinPosition = found.transform.position;
            pressedCoffin = false;
            State = BotState.Reviving;
            Plugin.Log.LogInfo("[bot] owner is down, heading to their coffin.");
            DropTarget();
            return true;
        }

        /// <summary>Uses the coffin, then fights the revive ghost until the host is alive.</summary>
        private void TickRevive(MyPlayer me)
        {
            RefreshOwner();
            var host = playerManagerService.GetHost();
            if (host == null)
            {
                EndRevive();
                return;
            }

            if (host.Hp > 0)
            {
                if (pressedCoffin)
                {
                    Revives++;
                    Plugin.Log.LogInfo("[bot] revived owner.");
                }

                EndRevive();
                return;
            }

            if (!pressedCoffin && coffin != null && coffin.CanInteract() && IsCurrentInteractable(coffin))
            {
                BotInput.PressInteract(coffin.GetInstanceID());
                BotInput.Move = Vector2.zero;
                pressedCoffin = true;
                Plugin.Log.LogInfo("[bot] used the owner's coffin; waiting for the revive fight.");
            }

            if (!pressedCoffin && (coffin == null || !coffin.CanInteract()))
            {
                // Someone else used it, or it is gone.
                EndRevive();
                return;
            }

            // Chase the ghost; it wanders away from the coffin.
            var target = coffinPosition;
            var ghost = coffin != null ? coffin.SpawnedGhost : null;
            var fightingGhost = pressedCoffin && ghost != null && ghost.hp > 0;
            if (fightingGhost)
            {
                target = ghost.transform.position;
            }

            // Stuck-teleport anchor during the fight: ground under the ghost (it flies through terrain).
            var anchor = fightingGhost ? GroundUnder(target, coffinPosition) : (OwnerPosition ?? coffinPosition);
            SteerTo(me, target, anchor);
        }

        /// <summary>Ground under <paramref name="point"/> if near <paramref name="reference"/>'s height, else the reference.</summary>
        private static Vector3 GroundUnder(Vector3 point, Vector3 reference, float heightBand = 25f)
        {
            const float rayHeight = 80f;

            var origin = new Vector3(point.x, Mathf.Max(point.y, reference.y) + rayHeight, point.z);
            if (Physics.Raycast(origin, Vector3.down, out var hit, rayHeight * 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                && Mathf.Abs(hit.point.y - reference.y) <= heightBand)
            {
                return hit.point + Vector3.up;
            }

            return reference;
        }

        private void EndRevive()
        {
            if (!pressedCoffin)
            {
                BotInput.CancelInteract();
            }

            coffin = null;
            pressedCoffin = false;
            if (State == BotState.Reviving)
            {
                State = BotState.Following;
            }

            DropTarget();
        }

        /// <summary>Writes the stick input toward <paramref name="target"/> and watches for being stuck.</summary>
        private void SteerTo(MyPlayer me, Vector3 target, Vector3 owner)
        {
            var toTarget = target - me.transform.position;
            toTarget.y = 0f;
            var distance = toTarget.magnitude;

            BotInput.Move = distance < 1.5f
                ? Vector2.zero
                : BotSteering.ToInputAxes(toTarget, me.playerMovement.feet);

            CheckStuck(me, target, distance, owner);
        }

        /// <summary>Teleports to the host after <c>BotStuckSeconds</c> without beating the best distance by 1 unit.</summary>
        private void CheckStuck(MyPlayer me, Vector3 target, float distance, Vector3 owner)
        {
            var now = Time.time;
            if (!steerTarget.HasValue || distance < 1.5f)
            {
                steerTarget = target;
                bestTargetDistance = distance;
                lastProgressTime = now;
                return;
            }

            steerTarget = target;
            if (distance < bestTargetDistance - 1f)
            {
                bestTargetDistance = distance;
                lastProgressTime = now;
                return;
            }

            var stuckFor = now - lastProgressTime;
            if (stuckFor < ModConfig.BotStuckSeconds.Value)
            {
                return;
            }

            // OB-53: beside the owner only if there is ground there, else on the owner (never in a wall).
            var destination = GroundUnder(owner + new Vector3(2f, 0f, 0f), owner, 3f);
            if (destination == owner)
            {
                destination = owner + new Vector3(0f, 0.5f, 0f);
            }
            me.transform.position = destination;
            var rb = me.playerMovement.rb;
            if (rb != null)
            {
                rb.position = destination;
                rb.velocity = Vector3.zero;
            }

            Teleports++;
            Plugin.Log.LogInfo($"[bot] stuck {stuckFor:F0}s, teleported to {(owner == target ? "the target" : "owner")}.");
            DropTarget();
        }

        /// <summary>Switches to <see cref="BotState.Rewarding"/> when one of this copy's reward windows opens.</summary>
        private bool TryEnterRewarding()
        {
            if (BotRewards.ActiveWindow() == null)
            {
                return false;
            }

            stateBeforeReward = State;
            BotInput.CancelInteract();
            State = BotState.Rewarding;
            actedOnWindow = false;
            rewardStartTime = Time.unscaledTime;
            loggedRewardStall = false;
            BotInput.Move = Vector2.zero;
            BotRewards.OnWindowOpened();
            return true;
        }

        private void TickRewarding()
        {
            var window = BotRewards.ActiveWindow();
            if (window == null)
            {
                if (actedOnWindow)
                {
                    RewardsPicked++;
                }

                State = stateBeforeReward;
                DropTarget();
                return;
            }

            if (Time.unscaledTime - rewardStartTime > REWARD_STALL_SECONDS)
            {
                if (!loggedRewardStall)
                {
                    loggedRewardStall = true;
                    Plugin.Log.LogWarning($"[bot] reward window {window.gameObject.name} still open after {REWARD_STALL_SECONDS:F0}s; taking or skipping.");
                }

                BotRewards.Fallback(window);
                actedOnWindow = true;
                return;
            }

            if (BotRewards.TryPick(window))
            {
                actedOnWindow = true;
            }
        }

        private void DropTarget()
        {
            steerTarget = null;
            bestTargetDistance = float.MaxValue;
            lastProgressTime = Time.time;
        }

        /// <summary>Readies up in the lobby panel and picks a random usable character, once a second.</summary>
        private void TickLobby()
        {
            if (Time.unscaledTime < nextLobbyActionTime)
            {
                return;
            }

            nextLobbyActionTime = Time.unscaledTime + 1f;

            // The ready button lives on the lobby panel; on a path without one there is nothing to press.
            // Needs the local player: pressing earlier only logs "Cannot toggle readiness".
            if (Modal.LobbyPanel.Current != null
                && playerManagerService.GetLocalPlayer() != null
                && !lobbyViewService.IsLocalPlayerReady)
            {
                lobbyViewService.ToggleLocalReady();
                Plugin.Log.LogInfo("[bot] ready.");
                nextLobbyActionTime = Time.unscaledTime + 2f; // let the toggle round-trip before re-reading it
                return;
            }

            var menu = Helpers.WindowCast.AsCharacterMenu(WindowManager.activeWindow);
            if (menu == null)
            {
                return;
            }

            if (!characterChosen)
            {
                characterChosen = PickRandomCharacter(menu);
                return;
            }

            // The confirm click may land before the menu accepts input; press it again a few times.
            if (confirmPresses < 5 && menu.b_confirm != null && menu.b_confirm.button != null)
            {
                confirmPresses++;
                menu.b_confirm.button.onClick.Invoke();
            }
        }

        /// <summary>
        /// Picks a random <c>canUseCharacter</c> character via the menu's handlers (VA <c>0x18054A290</c> /
        /// <c>0x180549C90</c>); confirming an unusable one only shows a popup.
        /// </summary>
        private static bool PickRandomCharacter(CharacterMenu menu)
        {
            var buttons = menu.characterButtons;
            if (buttons == null || buttons.Count == 0)
            {
                return false;
            }

            var usable = new System.Collections.Generic.List<MyButtonCharacter>();
            foreach (var button in buttons)
            {
                if (button != null && button.canUseCharacter)
                {
                    usable.Add(button);
                }
            }

            if (usable.Count == 0)
            {
                return false;
            }

            var pick = usable[Random.Range(0, usable.Count)];
            menu.OnSelectCharacter(pick);
            menu.OnConfirmCharacter(pick);
            var name = pick.characterData != null ? pick.characterData.name : "?";
            Plugin.Log.LogInfo($"[bot] picked {name}.");
            return true;
        }

        private void LogStatus()
        {
            var now = Time.unscaledTime;
            if (now - lastStatusTime < Tunables.DIAGNOSTIC_REPORT_INTERVAL_SECONDS)
            {
                return;
            }

            lastStatusTime = now;
            var distance = "-";
            var me = GameManager.Instance?.player;
            if (OwnerPosition.HasValue && me != null)
            {
                distance = Vector3.Distance(OwnerPosition.Value, me.transform.position).ToString("F0");
            }

            Plugin.Log.LogInfo(
                $"[bot] {State}; owner {distance}u; rewards {RewardsPicked}, used {InteractablesUsed}, " +
                $"revives {Revives}, teleports {Teleports}.");
        }
    }
}
#endif

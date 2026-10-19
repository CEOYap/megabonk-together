using Assets.Scripts.Utility;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Actors.Player;
using MegabonkTogether.Configuration;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace MegabonkTogether.Scripts.Bot
{
    /// <summary>
    /// The test bot (design: <c>docs/superpowers/specs/2026-10-19-test-bot-design.md</c>). Decides
    /// what the bot wants each frame and writes it to <see cref="BotInput"/>; it never calls sync
    /// services or sends messages, so everything it does reaches the other peer the way a player's
    /// actions do. Does nothing unless <see cref="ModConfig.BotMode"/> is on.
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
                // The owner is always focused on their own window, and an unfocused Unity player may
                // pause; the cap leaves the PC to the owner's copy (design, calls 1 and 2).
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

            if (netplaySessionService.IsBusy || synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            var code = Helpers.BotRoomCodeFile.ReadFresh(System.TimeSpan.FromMinutes(10));
            if (code == null)
            {
                return;
            }

            Helpers.BotRoomCodeFile.Delete();
            State = BotState.Joining;
            Plugin.Log.LogInfo($"[bot] joining room {code}.");
            netplaySessionService.Join(code);
        }

        /// <summary>
        /// Moves between the menu, lobby and run states from the session's own flags, so a dropped
        /// session or a return to the menu always lands the bot back in <see cref="BotState.Idle"/>.
        /// </summary>
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

            if (State == BotState.Joining && initialized)
            {
                State = BotState.InLobby;
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

            // Paused by any encounter window (MyTime.Pause freezes physics but not Time.time), or
            // between Started states (loading, the ready barrier): no input is fed then, so nothing
            // may count as "no progress". Hold still and restart the stuck and give-up clocks.
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

        /// <summary>
        /// Detours are optional; following is not. A throw here once stopped the bot moving at all
        /// (it came before the steering call every frame), so the search is fenced off: the first
        /// failure is logged and detours stay off for the session.
        /// </summary>
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

        /// <summary>
        /// Walks to the coffin and presses interact. Reviving starts a fight (the reviver spawns an
        /// enemy that must die first), so after the press the bot stays at the coffin until the host
        /// is alive again.
        /// </summary>
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

            SteerTo(me, coffinPosition, OwnerPosition ?? coffinPosition);
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

        /// <summary>
        /// Teleports next to the host after <c>BotStuckSeconds</c> without getting 1 unit closer to
        /// the target. The follow point moves, so progress is measured against the best distance so
        /// far, reset whenever the bot arrives. Also catches falling out of the map.
        /// </summary>
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

            var destination = owner + new Vector3(2f, 0.5f, 0f);
            me.transform.position = destination;
            var rb = me.playerMovement.rb;
            if (rb != null)
            {
                rb.position = destination;
                rb.velocity = Vector3.zero;
            }

            Teleports++;
            Plugin.Log.LogInfo($"[bot] stuck {stuckFor:F0}s, teleported to owner.");
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
        /// Selects and confirms a random character through the menu's own handlers
        /// (<c>OnSelectCharacter</c> / <c>OnConfirmCharacter</c>, VA <c>0x18054A290</c> /
        /// <c>0x180549C90</c>), the path a click takes. Confirm on a character that cannot be used
        /// shows a popup and returns, so only <c>canUseCharacter</c> buttons are candidates.
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

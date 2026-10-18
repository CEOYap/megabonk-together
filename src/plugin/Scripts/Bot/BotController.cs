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
            BotInput.Clear();
            OwnerPosition = null;
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
            if (Modal.LobbyPanel.Current != null && !lobbyViewService.IsLocalPlayerReady)
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

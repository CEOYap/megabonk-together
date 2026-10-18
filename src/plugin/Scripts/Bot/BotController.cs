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
        private float nextJoinCheckTime;
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

            if (State == BotState.Idle)
            {
                TryJoin();
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

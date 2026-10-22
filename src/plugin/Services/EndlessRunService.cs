using Assets.Scripts.Game.Other;
using Assets.Scripts.Managers;
using BepInEx.Logging;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Helpers;
using System.Collections.Generic;

namespace OmniBonk.Services
{
    /// <summary>
    /// Endless Run (docs/netplay/18): a shuffled route of rooms with a boss room after every three.
    /// <para>Steered by pointing <c>currentMap</c>/<c>index</c> before the game's own
    /// <c>LoadNextStage</c> (VA 0x180421DF0) or <c>LoadFinalStage</c> (0x180421C00), decompiled.</para>
    /// <para>Only the host rolls the route (System.Random, not Unity's per-run seeded RNG) and sends
    /// each room one change ahead. A client without the room turns Endless off rather than diverge.</para>
    /// <para><b>UNVERIFIED, not run in-game.</b></para>
    /// </summary>
    public interface IEndlessRunService
    {
        bool Active { get; }

        EndlessRoom CurrentRoom { get; }

        /// <summary>Stage changes so far this run; 0 in the starting room.</summary>
        int Transitions { get; }

        /// <summary>Boss loops cleared; identical on every peer. Drives scaling.</summary>
        int CompletedLoops { get; }

        void BeginRun(bool isHost, RunConfig config);

        /// <summary>Only then is an exit an Endless stage change, so a missing room cannot strand a client.</summary>
        bool NextRoomKnown { get; }

        /// <summary>Returns whether the original load may run: false when this redirected to the other load method.</summary>
        bool OnStageLoadRequested(bool finalStageRequested);

        /// <summary>True once per Graveyard room arrival.</summary>
        bool TakeGraveyardArrival(out EndlessRoom room);

        /// <summary>True once per prepared Graveyard arrival, when the room should start.</summary>
        bool TakeGraveyardActivation(out EndlessRoom room);

        void Reset();
    }

    internal class EndlessRunService : IEndlessRunService
    {
        private readonly ILobbySettingsService lobbySettingsService;
        private readonly INetTransport transport;
        private readonly ManualLogSource logger;

        private bool isHost;
        private int transitions;
        private EndlessRoom current;
        private bool redirecting;
        private int graveyardPreparedFor = -1;
        private bool graveyardActivationPending;

        // Host only.
        private int regularCompleted;
        private System.Random random = new();
        private readonly List<EndlessRoom> regularQueue = [];
        private readonly List<EndlessRoom> bossQueue = [];
        private readonly Dictionary<int, (EndlessRoom Room, int RegularCompleted)> planned = [];

        // Client only: rooms the host sent, by stage-change sequence.
        private readonly Dictionary<int, EndlessRoom> received = [];

        public EndlessRunService(ILobbySettingsService lobbySettingsService, INetTransport transport, ManualLogSource logger)
        {
            this.lobbySettingsService = lobbySettingsService;
            this.transport = transport;
            this.logger = logger;
            EventManager.SubscribeEndlessNextRoomEvents(OnReceivedNextRoom);
        }

        public bool Active { get; private set; }

        public EndlessRoom CurrentRoom => current;

        public int Transitions => transitions;

        public int CompletedLoops { get; private set; }

        public bool NextRoomKnown => Active && (isHost || received.ContainsKey(transitions + 1));

        public void BeginRun(bool isHost, RunConfig config)
        {
            Clear();
            this.isHost = isHost;

            if (!lobbySettingsService.EndlessEnabled)
            {
                return;
            }

            // From the config: on the host MapController may not be set yet here.
            var map = config?.mapData ?? MapController.currentMap;
            var index = config?.mapData != null && config.stageData != null
                ? config.mapData.stages.IndexOf(config.stageData)
                : MapController.index;
            var start = map == null ? null : EndlessRooms.FromStage(map.eMap, index, false);
            if (!start.HasValue)
            {
                logger.LogWarning($"[endless] Endless needs a Forest, Desert or Graveyard start (this run: {map?.eMap.ToString() ?? "no map"} stage {index}); off for this run.");
                return;
            }

            Active = true;
            current = start.Value;
            logger.LogInfo($"[endless] Endless Run on; starting room {current} ({(isHost ? "host rolls the route" : "following the host")}).");

            if (isHost)
            {
                PlanAndSend(1);
            }
        }

        public bool OnStageLoadRequested(bool finalStageRequested)
        {
            if (!Active || redirecting)
            {
                return true;
            }

            var sequence = transitions + 1;
            EndlessRoom room;
            var regularAfter = regularCompleted;
            if (isHost)
            {
                if (!planned.Remove(sequence, out var plan))
                {
                    plan = Plan(sequence);
                }

                room = plan.Room;
                regularAfter = plan.RegularCompleted;
            }
            else if (!received.Remove(sequence, out room))
            {
                logger.LogError($"[endless] Stage change {sequence} came before the host's room for it; Endless off for this run.");
                Active = false;
                return true;
            }

            var mapData = DataManager.Instance?.GetMap(EndlessRooms.Map(room));
            var stage = EndlessRooms.Stage(room);
            if (mapData == null || mapData.stages == null || stage >= mapData.stages.Length || mapData.stages[stage] == null)
            {
                logger.LogError($"[endless] Room {room} has no stage data in this build; Endless off for this run.");
                Active = false;
                return true;
            }

            var stageData = mapData.stages[stage];
            var config = MapController.runConfig;
            if (config != null)
            {
                config.mapData = mapData;
                config.stageData = stageData;
            }

            MapController.currentMap = mapData;
            MapController.currentStage = stageData;
            MapController.isFinalBossStage = false;

            if (EndlessRooms.IsBoss(current))
            {
                CompletedLoops++;
                logger.LogInfo($"[endless] Boss loop {CompletedLoops} cleared; enemies now x{System.Math.Pow(Configuration.Tunables.ENDLESS_LOOP_HP_MULTIPLIER, CompletedLoops):0.##} HP, x{System.Math.Pow(Configuration.Tunables.ENDLESS_LOOP_DAMAGE_MULTIPLIER, CompletedLoops):0.##} damage.");
            }

            transitions = sequence;
            current = room;
            regularCompleted = regularAfter;
            logger.LogInfo($"[endless] Stage change {sequence}: {room}.");

            if (isHost)
            {
                PlanAndSend(sequence + 1);
            }

            var finalStage = EndlessRooms.LoadsFinalStage(room);
            MapController.index = finalStage ? stage : stage - 1;

            if (finalStage == finalStageRequested)
            {
                return true;
            }

            // The game asked for the other kind of load; run the one this room needs.
            redirecting = true;
            try
            {
                if (finalStage)
                {
                    MapController.LoadFinalStage();
                }
                else
                {
                    MapController.LoadNextStage();
                }
            }
            finally
            {
                redirecting = false;
            }

            return false;
        }

        private void PlanAndSend(int sequence)
        {
            var plan = Plan(sequence);
            planned[sequence] = plan;
            transport.SendToAllClients(new EndlessNextRoom { Sequence = sequence, Room = (int)plan.Room }, NetDelivery.ReliableOrdered);
        }

        /// <summary>Host: the room after <see cref="current"/>. Three regular rooms, then a boss.</summary>
        private (EndlessRoom Room, int RegularCompleted) Plan(int sequence)
        {
            var leavingBoss = EndlessRooms.IsBoss(current);
            var regularDone = regularCompleted + (leavingBoss ? 0 : 1);
            var bossNext = !leavingBoss && regularDone % EndlessRooms.RegularRoomsPerBoss == 0;

            var queue = bossNext ? bossQueue : regularQueue;
            if (queue.Count == 0)
            {
                EndlessRooms.Refill(queue, bossNext ? EndlessRooms.Bosses : EndlessRooms.Regular, random, current);
            }

            var room = queue[0];
            queue.RemoveAt(0);
            return (room, regularDone);
        }

        private void OnReceivedNextRoom(EndlessNextRoom message)
        {
            if (message == null || !EndlessRooms.IsKnown(message.Room))
            {
                logger.LogWarning($"[endless] Ignored an unknown room {message?.Room} from the host.");
                return;
            }

            received[message.Sequence] = (EndlessRoom)message.Room;
        }

        public bool TakeGraveyardArrival(out EndlessRoom room)
        {
            room = current;
            if (!Active || !EndlessRooms.IsGraveyard(current) || graveyardPreparedFor == transitions)
            {
                return false;
            }

            graveyardPreparedFor = transitions;
            graveyardActivationPending = true;
            return true;
        }

        public bool TakeGraveyardActivation(out EndlessRoom room)
        {
            room = current;
            if (!graveyardActivationPending)
            {
                return false;
            }

            graveyardActivationPending = false;
            return Active;
        }

        public void Reset() => Clear();

        private void Clear()
        {
            Active = false;
            transitions = 0;
            CompletedLoops = 0;
            regularCompleted = 0;
            redirecting = false;
            graveyardPreparedFor = -1;
            graveyardActivationPending = false;
            regularQueue.Clear();
            bossQueue.Clear();
            planned.Clear();
            received.Clear();
            random = new System.Random();
        }
    }
}

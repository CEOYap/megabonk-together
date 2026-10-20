using Assets.Scripts.Game.Other;
using Assets.Scripts.Managers;
using BepInEx.Logging;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Helpers;
using System.Collections.Generic;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Endless Run, Phase 2 (docs/netplay/18): after a run's first stage, every stage change goes
    /// to a room on a shuffled route of Forest, Desert and Graveyard rooms, with a boss room (Ent,
    /// Pharaoh or the Ghost King) after every three regular rooms. It never ends by itself; only death ends the run.
    ///
    /// <para><b>How a stage change is steered</b> (decompiled, build 21750826).
    /// <c>MapController.LoadNextStage</c> (0x180421DF0) keeps the live inventory, increments
    /// <c>index</c>, takes <c>currentMap.stages[index]</c> and loads the map scene.
    /// <c>LoadFinalStage</c> (0x180421C00) does the same with <c>isFinalBossStage</c> set, loading
    /// that map's final boss. So pointing <c>currentMap</c> at the room's map and <c>index</c> one
    /// below its stage, then letting the game's own load run, moves the run there with the
    /// inventory intact. Every exit funnels into one of those two calls.</para>
    ///
    /// <para><b>Multiplayer.</b> Every peer runs its own stage load (each presses the same portal),
    /// so every peer must steer it to the same room. Only the host rolls the route, with
    /// <see cref="System.Random"/>, not Unity's generator, which is seeded per run and shared by map
    /// generation. It sends each room one stage change ahead (<c>EndlessNextRoom</c>). A client
    /// that reaches a stage change with no room for it logs an error and turns Endless off for the
    /// run, rather than loading something the host did not.</para>
    ///
    /// <para><b>UNVERIFIED, not run in-game.</b></para>
    /// </summary>
    public interface IEndlessRunService
    {
        bool Active { get; }

        /// <summary>The room this peer is in, or is loading.</summary>
        EndlessRoom CurrentRoom { get; }

        /// <summary>Stage changes so far this run; 0 in the starting room.</summary>
        int Transitions { get; }

        /// <summary>A run has started on this peer, with this config.</summary>
        void BeginRun(bool isHost, RunConfig config);

        /// <summary>
        /// The next stage change's room is known here: always on the host, and on a client once the
        /// host's room has arrived. An exit only becomes an Endless stage change when this holds;
        /// otherwise it stays the game's own, so a missing room cannot strand a client.
        /// </summary>
        bool NextRoomKnown { get; }

        /// <summary>
        /// The game is about to load a stage. Returns whether the original may run: false when this
        /// redirected to the other load method itself.
        /// </summary>
        bool OnStageLoadRequested(bool finalStageRequested);

        /// <summary>
        /// Graveyard (Phase 3): true once per arrival in a Graveyard room, at its spawn portal,
        /// with the room to prepare.
        /// </summary>
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

        public bool NextRoomKnown => Active && (isHost || received.ContainsKey(transitions + 1));

        public void BeginRun(bool isHost, RunConfig config)
        {
            Clear();
            this.isHost = isHost;

            if (!lobbySettingsService.EndlessEnabled)
            {
                return;
            }

            // From the config, not MapController: the host reaches here from StartNewMap's postfix,
            // which also runs on the call its own prefix skipped while waiting for the lobby, before
            // any map is set (2026-10-21: "no map stage 0" on the host, so the host ran without
            // Endless while both clients waited for rooms that never came).
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

            // The game asked for the other kind of load (a portal on a stage before a boss room, or
            // a final-boss spawner before a regular one): run the one this room needs instead.
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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// One row of the lobby panel's member list, in the terms the panel draws rather than the terms
    /// the network layer stores.
    /// </summary>
    public readonly struct LobbyMemberView(uint connectionId, string name, bool isHost, bool isLocal, bool isReady)
    {
        public uint ConnectionId { get; } = connectionId;
        public string Name { get; } = name;
        public bool IsHost { get; } = isHost;
        public bool IsLocal { get; } = isLocal;

        /// <summary>
        /// Lobby readiness — "ready for the host to start" — and deliberately not
        /// <c>Player.IsReady</c>, which answers the different question of whether this peer has
        /// finished loading a level.
        /// </summary>
        public bool IsReady { get; } = isReady;
    }

    /// <summary>
    /// What the lobby panel needs to draw itself, and nothing else.
    ///
    /// <para><b>Why this exists rather than the panel calling <c>IPlayerManagerService</c>.</b>
    /// Steamworks migration Phase 3 replaces where lobby membership comes from: today it is this
    /// mod's own replicated roster keyed by <c>ConnectionId</c>, after Phase 3 it is a Steam lobby's
    /// members keyed by <c>CSteamID</c>, and it gains avatars and an invite button. Binding the
    /// panel to this interface means that phase re-points the data and the panel does not change.
    /// The same seam reasoning as <see cref="INetTransport"/>, applied one layer up.</para>
    ///
    /// <para>Deliberately read-only. Actions that change lobby membership — leaving, joining —
    /// already have owners (<c>IWebsocketClientService</c>, <c>IUdpClientService</c>) and routing
    /// them through a view interface would make it a second, competing front door.</para>
    /// </summary>
    public interface ILobbyViewService
    {
        /// <summary>True once there is a lobby to show.</summary>
        bool IsInLobby { get; }

        /// <summary>The share code for this lobby, or empty when there is none.</summary>
        string LobbyCode { get; }

        /// <summary>True when the local player owns this lobby.</summary>
        bool IsLocalPlayerHost { get; }

        /// <summary>
        /// Members, host first then join order. Allocates a list per call, so call it on a refresh
        /// tick rather than per frame — see <c>LobbyPanel</c>.
        /// </summary>
        IReadOnlyList<LobbyMemberView> GetMembers();

        /// <summary>The local player's own lobby-ready state.</summary>
        bool IsLocalPlayerReady { get; }

        /// <summary>
        /// True when every member is ready. What the host's Start button is gated on, computed from
        /// the one set rather than from a second local tally.
        /// </summary>
        bool AreAllMembersReady { get; }

        /// <summary>
        /// Toggles the local player's lobby readiness and publishes it. On a client this sends to
        /// the host; on the host it updates the authoritative set and broadcasts.
        /// </summary>
        void ToggleLocalReady();

        /// <summary>Drops all lobby-ready state. Called when a lobby ends.</summary>
        void ResetReadyState();

        /// <summary>
        /// Whether there is a Steam lobby to invite friends to. False for a client, false without
        /// Steam, and false until the Steam lobby has finished being created — so the button that
        /// binds to this hides rather than failing when pressed.
        /// </summary>
        bool CanInvite { get; }

        /// <summary>
        /// Opens Steam's invite dialog for this lobby. No-op unless <see cref="CanInvite"/>.
        ///
        /// <para>Everything after the click belongs to Steam: the friend list, the invite, and the
        /// accept. What comes back to us is a <c>+connect_lobby</c> launch argument if the friend's
        /// game was closed — see <c>Helpers/LaunchArguments.cs</c>.</para>
        /// </summary>
        void InviteFriends();

        /// <summary>
        /// Host only. Ends the lobby and tells every peer to advance to character selection.
        /// No-op unless <see cref="AreAllMembersReady"/>.
        /// </summary>
        void RequestStart();

        /// <summary>Hooks up the lobby message handlers. Called once from <c>Plugin.Load</c>.</summary>
        void SubscribeToLobbyMessages();
    }

    internal class LobbyViewService(
        IPlayerManagerService playerManagerService,
        INetTransport netTransport,
        ISteamLobbyService steamLobbyService,
        ISteamService steamService,
        SteamPersonaService steamPersonaService) : ILobbyViewService
    {
        // A Steam-backed roster and readiness lived here and have been removed. Both ends decided
        // independently whether to use it, by comparing the Steam lobby's member count against the
        // replicated roster's — and those two sets are populated by different mechanisms at
        // different times, so the ends could disagree. A client on the Steam path wrote its
        // readiness to member data and sent nothing; a host on the message path read a set nobody
        // had written. The press went somewhere the host never looked, and neither side could tell.
        //
        // The fix is not a better guard. Any rule derived separately on each machine can disagree,
        // and readiness is a correctness property — the Start gate is built on it. Readiness stays
        // on the messages until the Steam lobby *is* the session and there is only one membership
        // set to consult, which is Phase 4's business.
        //
        // Kept from that work, because neither depends on the switch: both players still join one
        // Steam lobby, and Steam persona names still reach the panel — the name is adopted onto
        // ModConfig.PlayerName, so the message path shows it too.

        /// <summary>
        /// Invite is answered here rather than in the panel so the panel never learns that Steam
        /// exists. That was the point of putting a view model in front of it: the lobby panel's
        /// design note said Phase 3 should be able to add invite "without touching the panel", and
        /// the panel's side of this is one button bound to two members.
        /// </summary>
        public bool CanInvite =>
            IsLocalPlayerHost && steamLobbyService.State == SteamLobbyState.InLobby;

        public void InviteFriends()
        {
            if (!CanInvite)
            {
                return;
            }

            steamLobbyService.OpenInviteOverlay();
        }

        /// <summary>
        /// The authoritative lobby-ready set on the host, and the host's last broadcast as mirrored
        /// on a client. Keyed by game connection id.
        ///
        /// <para>Held here rather than on <c>Player</c> for two reasons. <c>Player</c> is serialized
        /// inside <c>LobbyUpdates</c> (union tag 0) and MemoryPack is positional, so widening it
        /// would silently corrupt sessions between builds. And <c>Player.IsReady</c> already means
        /// something else — level-load readiness, owned by <c>ReadinessService</c> — which is the
        /// ambiguity that produced the lobby hang.</para>
        /// </summary>
        private readonly ConcurrentDictionary<uint, bool> readyByConnectionId = [];

        /// <summary>
        /// The local player's own toggle, shown immediately and held until the host's broadcast
        /// agrees with it. Null when nothing is in flight.
        ///
        /// <para><b>Why optimistic here, when the level-load barrier deliberately is not.</b> That
        /// barrier's retry loop used a self-written flag as proof the host had acknowledged it,
        /// which is how a client could exit having sent nothing and hang the lobby. Nothing here
        /// reads this value to make a decision — <c>Start</c> is host-only and the host checks its
        /// own set — so showing the press immediately costs correctness nothing and saves a
        /// round-trip of the button looking broken.</para>
        ///
        /// <para>It is <i>held</i> rather than merely written because <see cref="ApplyHostState"/>
        /// replaces the whole set: a broadcast the host generated before it processed this toggle
        /// would otherwise flip the label back, then forward again a moment later.</para>
        /// </summary>
        private bool? pendingLocalReady;

        /// <summary>
        /// When the pending toggle was made. <c>Environment.TickCount64</c> rather than
        /// <c>Time.unscaledTime</c>: this is set on the main thread but examined from
        /// <see cref="ApplyHostState"/>, which runs on the LiteNetLib receive thread, and touching a
        /// Unity API off the main thread is a hard crash with no managed stack.
        /// </summary>
        private long pendingSinceTicks;

        /// <summary>
        /// How long an unconfirmed toggle keeps overriding the host. Past this the host wins and the
        /// disagreement is logged: a local value that never yields would be a silent divergence,
        /// which is the failure mode this project keeps paying for.
        /// </summary>
        private const long PendingReadyTimeoutMs = 3000;

        /// <summary>
        /// Subscribes to the lobby messages rather than being called into by the transport.
        ///
        /// <para><b>This is why the plugin hung on launch.</b> The first version had
        /// <c>UdpClientService</c> take <c>ILobbyViewService</c> and call the handlers directly from
        /// its receive switch — a cycle, because this service needs <c>INetTransport</c> and that
        /// resolves to the same <c>UdpClientService</c>. The container normally detects a circular
        /// dependency and throws, but the <c>INetTransport</c> registration is a factory lambda,
        /// which is opaque to its call-site graph; instead of an exception it deadlocked on the
        /// singleton lock, so the game never reached the main menu and nothing was logged at all.
        /// </para>
        ///
        /// <para>Publishing through <c>EventManager</c> is the house convention for exactly this
        /// reason — the transport stays unaware of whatever consumes its messages. It also moves
        /// these handlers onto the main thread, since the dispatcher marshals them.</para>
        /// </summary>
        public void SubscribeToLobbyMessages()
        {
            EventManager.SubscribeLobbyReadyChangedEvents(OnLobbyReadyChanged);
            EventManager.SubscribeLobbyReadyStateEvents(OnLobbyReadyState);
        }

        private void OnLobbyReadyChanged(Common.Messages.GameNetworkMessages.LobbyReadyChanged changed) =>
            ApplyClientReady(changed.ConnectionId, changed.IsReady);

        private void OnLobbyReadyState(Common.Messages.GameNetworkMessages.LobbyReadyState state) =>
            ApplyHostState(state.Entries);

        public bool IsInLobby =>
            Plugin.Instance?.Mode != null
            && !string.IsNullOrEmpty(Plugin.Instance.Mode.RoomCode);

        public string LobbyCode => Plugin.Instance?.Mode?.RoomCode ?? string.Empty;

        public bool IsLocalPlayerHost =>
            Plugin.Instance?.Mode?.Role == Common.Models.Role.Host;

        /// <summary>
        /// The name the local player will appear under, from config rather than the roster —
        /// the roster entry does not exist yet at the point this matters.
        /// </summary>
        private static string LocalPlayerName =>
            string.IsNullOrWhiteSpace(Configuration.ModConfig.PlayerName?.Value)
                ? "Player"
                : Configuration.ModConfig.PlayerName.Value;

        public IReadOnlyList<LobbyMemberView> GetMembers()
        {
            var local = playerManagerService.GetLocalPlayer();
            var localId = local?.ConnectionId;

            // Host first, then by connection id so the order is stable between refreshes. An
            // unstable order makes rows appear to swap places while people are reading them.
            var members = playerManagerService.GetAllPlayers()
                .OrderByDescending(p => p.IsHost)
                .ThenBy(p => p.ConnectionId)
                .Select(p => new LobbyMemberView(
                    p.ConnectionId,
                    string.IsNullOrWhiteSpace(p.Name) ? "Player" : p.Name,
                    p.IsHost,
                    localId.HasValue && p.ConnectionId == localId.Value,
                    IsReady(p.ConnectionId)))
                .ToList();

            // A host alone in a fresh lobby is in no roster, so the list would be empty and the
            // panel would show a lobby with nobody in it — including the person looking at it.
            //
            // The roster is only ever filled from the matchmaker's peer list
            // (UdpClientService/WebsocketClientService both call AddPlayer while walking it), and
            // you are not a peer of yourself. Rather than change what the roster means — it is
            // shared with the whole netcode layer — the missing row is synthesized here, in the
            // view that needs it.
            if (IsInLobby && !members.Any(m => m.IsLocal))
            {
                members.Insert(0, new LobbyMemberView(
                    localId ?? 0u,
                    LocalPlayerName,
                    IsLocalPlayerHost,
                    isLocal: true,
                    IsLocalPlayerReady));
            }

            return members;
        }

        public bool IsLocalPlayerReady
        {
            get
            {
                var local = playerManagerService.GetLocalPlayer();
                return local != null && IsReady(local.ConnectionId);
            }
        }

        public bool AreAllMembersReady
        {
            get
            {
                var members = playerManagerService.GetAllPlayers().ToList();

                // An empty lobby is not "everyone is ready" — it is a lobby nobody has joined, and
                // treating it as satisfied would let a host start alone. ReadinessService takes the
                // opposite view for its own barrier, deliberately: there, an empty participant set
                // means a round with nobody to wait for, and refusing to complete it would hang.
                // Here it means nobody has arrived yet.
                return members.Count > 0 && members.All(p => IsReady(p.ConnectionId));
            }
        }

        public void ToggleLocalReady()
        {
            var local = playerManagerService.GetLocalPlayer();
            if (local == null)
            {
                Plugin.Log.LogWarning("[lobby] Cannot toggle readiness: no local player.");
                return;
            }

            var next = !IsReady(local.ConnectionId);

            if (netTransport.IsHost() == true)
            {
                // The host owns the set, so it applies its own toggle directly and republishes.
                // There is no message for it to send to itself, and nothing to reconcile.
                readyByConnectionId[local.ConnectionId] = next;
                BroadcastReadyState();
                return;
            }

            // Shown immediately, then reconciled against the host's broadcast. See
            // pendingLocalReady for why an optimistic value is safe here and deliberately is not on
            // the level-load barrier.
            pendingLocalReady = next;
            pendingSinceTicks = Environment.TickCount64;

            netTransport.SendToHost(
                new Common.Messages.GameNetworkMessages.LobbyReadyChanged
                {
                    ConnectionId = local.ConnectionId,
                    IsReady = next,
                },
                NetDelivery.ReliableOrdered);
        }

        public void ResetReadyState()
        {
            readyByConnectionId.Clear();
            pendingLocalReady = null;
        }

        public void RequestStart()
        {
            if (netTransport.IsHost() != true)
            {
                Plugin.Log.LogWarning("[lobby] Only the host can start the lobby.");
                return;
            }

            // Re-checked here and not only on the button's enabled state. The button reflects a
            // snapshot taken up to half a second ago, and somebody can un-ready inside that window;
            // the authority for "may we start" has to be the set, not the pixel.
            if (!AreAllMembersReady)
            {
                Plugin.Log.LogInfo("[lobby] Start ignored: not every member is ready.");
                return;
            }

            Plugin.Log.LogInfo("[lobby] Host started the lobby; advancing every peer to character selection.");

            netTransport.SendToAllClients(
                new Common.Messages.GameNetworkMessages.LobbyStartRequested(),
                NetDelivery.ReliableOrdered);

            // The host advances through the same event the clients do, so there is one path to
            // character selection rather than a host-shaped one and a client-shaped one.
            EventManager.OnLobbyStartRequested();
        }

        /// <summary>Host only. Applies a client's toggle and republishes the whole set.</summary>
        private void ApplyClientReady(uint connectionId, bool isReady)
        {
            if (netTransport.IsHost() != true)
            {
                return;
            }

            readyByConnectionId[connectionId] = isReady;
            Plugin.Log.LogInfo($"[lobby] {connectionId} is {(isReady ? "ready" : "not ready")}.");

            BroadcastReadyState();
        }

        /// <summary>
        /// Client only. Replaces the mirrored set with the host's, then reconciles any toggle still
        /// in flight.
        ///
        /// <para>Runs on the LiteNetLib receive thread, which is why the set is a
        /// <see cref="ConcurrentDictionary{TKey,TValue}"/> — the panel reads it from the main thread
        /// on its refresh tick, and a plain Dictionary mutated from two threads is a data race that
        /// shows up as a corrupted read long after the fact.</para>
        /// </summary>
        private void ApplyHostState(IEnumerable<Common.Messages.GameNetworkMessages.LobbyReadyEntry> entries)
        {
            readyByConnectionId.Clear();

            foreach (var entry in entries)
            {
                readyByConnectionId[entry.ConnectionId] = entry.IsReady;
            }

            ReconcilePendingReady();
        }

        /// <summary>
        /// Drops the pending toggle once the host agrees with it, or once it has waited too long.
        /// Without the timeout an unconfirmed toggle would override the host indefinitely and the
        /// two would disagree with nothing to say so.
        /// </summary>
        private void ReconcilePendingReady()
        {
            if (!pendingLocalReady.HasValue)
            {
                return;
            }

            var local = playerManagerService.GetLocalPlayer();
            if (local == null)
            {
                pendingLocalReady = null;
                return;
            }

            var hostSaysReady = readyByConnectionId.TryGetValue(local.ConnectionId, out var v) && v;

            if (hostSaysReady == pendingLocalReady.Value)
            {
                pendingLocalReady = null;
                return;
            }

            if (Environment.TickCount64 - pendingSinceTicks >= PendingReadyTimeoutMs)
            {
                Plugin.Log.LogWarning(
                    $"[lobby] Readiness toggle to {pendingLocalReady.Value} was not acknowledged within " +
                    $"{PendingReadyTimeoutMs} ms; accepting the host's value of {hostSaysReady}.");
                pendingLocalReady = null;
            }
        }

        /// <summary>
        /// Host only. Sends the whole set. Called on every change rather than on a tick: lobby
        /// readiness changes when somebody presses a button, which is orders of magnitude rarer
        /// than a tick, and a periodic resend would be bandwidth spent on state that is not moving.
        /// </summary>
        private void BroadcastReadyState()
        {
            if (netTransport.IsHost() != true)
            {
                return;
            }

            var message = new Common.Messages.GameNetworkMessages.LobbyReadyState();

            foreach (var player in playerManagerService.GetAllPlayers())
            {
                message.Entries.Add(new Common.Messages.GameNetworkMessages.LobbyReadyEntry
                {
                    ConnectionId = player.ConnectionId,
                    IsReady = IsReady(player.ConnectionId),
                });
            }

            netTransport.SendToAllClients(message, NetDelivery.ReliableOrdered);
        }

        private bool IsReady(uint connectionId)
        {
            // The local player's own unconfirmed toggle wins over the mirrored set, so the button
            // answers to the press rather than to the round-trip.
            if (pendingLocalReady.HasValue)
            {
                var local = playerManagerService.GetLocalPlayer();
                if (local != null && local.ConnectionId == connectionId)
                {
                    // Timed out on the read path as well as on receive, so a toggle that is never
                    // acknowledged still gives up even if no further host broadcast ever arrives to
                    // trigger reconciliation.
                    if (Environment.TickCount64 - pendingSinceTicks < PendingReadyTimeoutMs)
                    {
                        return pendingLocalReady.Value;
                    }

                    pendingLocalReady = null;
                }
            }

            return readyByConnectionId.TryGetValue(connectionId, out var ready) && ready;
        }
    }
}

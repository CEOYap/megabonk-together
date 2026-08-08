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
        /// Host only. Ends the lobby and tells every peer to advance to character selection.
        /// No-op unless <see cref="AreAllMembersReady"/>.
        /// </summary>
        void RequestStart();
    }

    internal class LobbyViewService(
        IPlayerManagerService playerManagerService,
        INetTransport netTransport) : ILobbyViewService
    {
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
        private readonly Dictionary<uint, bool> readyByConnectionId = [];

        public bool IsInLobby =>
            Plugin.Instance?.Mode != null
            && !string.IsNullOrEmpty(Plugin.Instance.Mode.RoomCode);

        public string LobbyCode => Plugin.Instance?.Mode?.RoomCode ?? string.Empty;

        public bool IsLocalPlayerHost =>
            Plugin.Instance?.Mode?.Role == Common.Models.Role.Host;

        public IReadOnlyList<LobbyMemberView> GetMembers()
        {
            var local = playerManagerService.GetLocalPlayer();
            var localId = local?.ConnectionId;

            // Host first, then by connection id so the order is stable between refreshes. An
            // unstable order makes rows appear to swap places while people are reading them.
            return playerManagerService.GetAllPlayers()
                .OrderByDescending(p => p.IsHost)
                .ThenBy(p => p.ConnectionId)
                .Select(p => new LobbyMemberView(
                    p.ConnectionId,
                    string.IsNullOrWhiteSpace(p.Name) ? "Player" : p.Name,
                    p.IsHost,
                    localId.HasValue && p.ConnectionId == localId.Value,
                    IsReady(p.ConnectionId)))
                .ToList();
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
                // There is no message for it to send to itself.
                readyByConnectionId[local.ConnectionId] = next;
                BroadcastReadyState();
                return;
            }

            // A client does NOT apply its own toggle. The host's broadcast is what makes it true,
            // exactly as with the level-load barrier — a peer that writes the flag it is also
            // reading cannot tell its own optimism from the host's answer, which is the bug that
            // hung the lobby.
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
        internal void ApplyClientReady(uint connectionId, bool isReady)
        {
            if (netTransport.IsHost() != true)
            {
                return;
            }

            readyByConnectionId[connectionId] = isReady;
            Plugin.Log.LogInfo($"[lobby] {connectionId} is {(isReady ? "ready" : "not ready")}.");

            BroadcastReadyState();
        }

        /// <summary>Client only. Replaces the mirrored set with the host's.</summary>
        internal void ApplyHostState(IEnumerable<Common.Messages.GameNetworkMessages.LobbyReadyEntry> entries)
        {
            readyByConnectionId.Clear();

            foreach (var entry in entries)
            {
                readyByConnectionId[entry.ConnectionId] = entry.IsReady;
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

        private bool IsReady(uint connectionId) =>
            readyByConnectionId.TryGetValue(connectionId, out var ready) && ready;
    }
}

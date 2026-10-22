using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace OmniBonk.Services
{
    /// <summary>One row of the lobby panel's member list.</summary>
    public readonly struct LobbyMemberView(uint connectionId, string name, bool isHost, bool isLocal, bool isReady, ulong steamId = 0UL, bool isIncompatible = false)
    {
        public uint ConnectionId { get; } = connectionId;
        public string Name { get; } = name;
        public bool IsHost { get; } = isHost;
        public bool IsLocal { get; } = isLocal;

        /// <summary>Steam account for the avatar, or 0 when unknown (always on the matchmaker transport).</summary>
        public ulong SteamId { get; } = steamId;

        /// <summary>Lobby readiness; not <c>Player.IsReady</c>, which means level-load readiness.</summary>
        public bool IsReady { get; } = isReady;

        /// <summary>Different wire protocol, or silent past <c>ProtocolHelloGraceMs</c>. Blocks Start.</summary>
        public bool IsIncompatible { get; } = isIncompatible;
    }

    /// <summary>
    /// Read-only view model for the lobby panel, so the membership source (roster or Steam lobby)
    /// can change without touching the panel.
    /// </summary>
    public interface ILobbyViewService
    {
        bool IsInLobby { get; }

        string LobbyCode { get; }

        bool IsLocalPlayerHost { get; }

        /// <summary>Members, host first. Allocates per call: use on a refresh tick, not per frame.</summary>
        IReadOnlyList<LobbyMemberView> GetMembers();

        bool IsLocalPlayerReady { get; }

        /// <summary>Everyone ready and, on the host, every client on the same protocol. Gates Start.</summary>
        bool AreAllMembersReady { get; }

        /// <summary>Client sends to the host; host updates the authoritative set and broadcasts.</summary>
        void ToggleLocalReady();

        void ResetReadyState();

        /// <summary>Host with a created Steam lobby only; the invite button hides otherwise.</summary>
        bool CanInvite { get; }

        /// <summary>Opens Steam's invite overlay. A closed game returns via <c>+connect_lobby</c>.</summary>
        void InviteFriends();

        /// <summary>Host only; no-op unless <see cref="AreAllMembersReady"/>.</summary>
        void RequestStart();

        /// <summary>Called once from <c>Plugin.Load</c>.</summary>
        void SubscribeToLobbyMessages();
    }

    internal class LobbyViewService(
        IPlayerManagerService playerManagerService,
        INetTransport netTransport,
        ISteamLobbyService steamLobbyService,
        ISteamService steamService,
        SteamPersonaService steamPersonaService) : ILobbyViewService
    {
        // Readiness stays on messages, not Steam member data: each end deciding separately which
        // path to use let them disagree and lose ready presses. Revisit when Steam lobby is the session.

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
        /// Host: authoritative lobby-ready set. Client: mirror of the host's broadcast.
        /// Not on <c>Player</c>: it is serialized positionally in <c>LobbyUpdates</c>, so adding a field breaks the wire.
        /// </summary>
        private readonly ConcurrentDictionary<uint, bool> readyByConnectionId = [];

        /// <summary>
        /// Optimistic local toggle, held until the host agrees (a stale broadcast would flip it back).
        /// Safe because no decision reads it; the host checks its own set.
        /// </summary>
        private bool? pendingLocalReady;

        /// <summary>TickCount64, not Unity time: read from the network receive thread.</summary>
        private long pendingSinceTicks;

        /// <summary>After this the host's value wins and the disagreement is logged.</summary>
        private const long PendingReadyTimeoutMs = 3000;

        /// <summary>
        /// Via <c>EventManager</c>, not direct calls from the transport: that DI cycle deadlocked on
        /// launch with no log. Handlers are marshalled to the main thread.
        /// </summary>
        public void SubscribeToLobbyMessages()
        {
            EventManager.SubscribeLobbyReadyChangedEvents(OnLobbyReadyChanged);
            EventManager.SubscribeLobbyReadyStateEvents(OnLobbyReadyState);
            EventManager.SubscribeLobbyRosterEvents(OnLobbyRoster);
            EventManager.SubscribeProtocolHelloEvents(OnProtocolHello);
        }

        private void OnLobbyReadyChanged(Common.Messages.GameNetworkMessages.LobbyReadyChanged changed) =>
            ApplyClientReady(changed.ConnectionId, changed.IsReady);

        private void OnLobbyReadyState(Common.Messages.GameNetworkMessages.LobbyReadyState state) =>
            ApplyHostState(state.Entries);

        /// <summary>Client: takes the other members' names from the host. Our own stays ours.</summary>
        private void OnLobbyRoster(Common.Messages.GameNetworkMessages.LobbyRoster roster)
        {
            var localId = playerManagerService.GetLocalPlayer()?.ConnectionId;
            foreach (var entry in roster.Entries)
            {
                if (entry.ConnectionId == localId || string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue;
                }

                var player = playerManagerService.GetPlayer(entry.ConnectionId);
                if (player != null)
                {
                    player.Name = entry.Name;
                }
            }
        }

        private const long RosterResendMs = 3000;
        private string lastRosterSignature;
        private long lastRosterSentTicks;

        /// <summary>Host: sends names on change and every few seconds, so late joiners get them.</summary>
        private void BroadcastRosterIfDue(IEnumerable<Common.Models.Player> players)
        {
            if (netTransport.IsHost() != true)
            {
                return;
            }

            var entries = players
                .Select(p => new Common.Messages.GameNetworkMessages.LobbyRosterEntry { ConnectionId = p.ConnectionId, Name = p.Name })
                .ToList();
            var signature = string.Join("|", entries.Select(e => $"{e.ConnectionId}:{e.Name}"));
            var now = Environment.TickCount64;

            if (signature == lastRosterSignature && now - lastRosterSentTicks < RosterResendMs)
            {
                return;
            }

            lastRosterSignature = signature;
            lastRosterSentTicks = now;
            netTransport.SendToAllClients(new Common.Messages.GameNetworkMessages.LobbyRoster { Entries = entries }, NetDelivery.ReliableOrdered);
        }

        // ------------------------------------------------------------------ protocol handshake

        /// <summary>
        /// Silence before a member counts as incompatible (older builds never answer). Generous so a
        /// slow compatible peer is not flagged.
        /// </summary>
        private const long ProtocolHelloGraceMs = 10000;

        private const long ProtocolHelloResendMs = 3000;

        private readonly ConcurrentDictionary<uint, Common.Messages.GameNetworkMessages.ProtocolHello> helloByConnectionId = [];

        /// <summary>Client only. Not keyed: the host's id in its hello can be 0.</summary>
        private volatile Common.Messages.GameNetworkMessages.ProtocolHello hostHello;

        private readonly ConcurrentDictionary<uint, long> firstSeenTicksByConnectionId = [];

        private readonly ConcurrentDictionary<uint, bool> reportedIncompatible = [];

        private long firstHelloSentTicks;
        private long lastHelloSentTicks;

        private Common.Messages.GameNetworkMessages.ProtocolHello OwnHello(uint connectionId) => new()
        {
            ConnectionId = connectionId,
            ProtocolVersion = Common.Protocol.Version,
            ModVersion = MyPluginInfo.PLUGIN_VERSION,
        };

        /// <summary>Client: resends until answered; the host may not have registered us yet.</summary>
        private void SendProtocolHelloIfDue(uint? localId)
        {
            if (netTransport.IsHost() != false || !localId.HasValue)
            {
                return;
            }

            if (hostHello != null)
            {
                return;
            }

            var host = playerManagerService.GetHost();

            var now = Environment.TickCount64;
            if (firstHelloSentTicks == 0)
            {
                firstHelloSentTicks = now;
            }
            else if (now - lastHelloSentTicks < ProtocolHelloResendMs)
            {
                return;
            }

            lastHelloSentTicks = now;
            netTransport.SendToHost(OwnHello(localId.Value), NetDelivery.ReliableOrdered);

            if (now - firstHelloSentTicks >= ProtocolHelloGraceMs)
            {
                ReportIncompatible(host?.ConnectionId ?? 0u, host?.Name ?? "The host", null);
            }
        }

        private void OnProtocolHello(Common.Messages.GameNetworkMessages.ProtocolHello hello)
        {
            var isHost = netTransport.IsHost() == true;
            if (isHost)
            {
                helloByConnectionId[hello.ConnectionId] = hello;
                var local = playerManagerService.GetLocalPlayer();
                netTransport.SendToClient(hello.ConnectionId, OwnHello(local?.ConnectionId ?? 0u));
            }
            else
            {
                hostHello = hello;
            }

            // A client reports under the host's id from its own roster, matching the silent case.
            var reportId = isHost ? hello.ConnectionId : playerManagerService.GetHost()?.ConnectionId ?? 0u;

            if (Common.Protocol.IsCompatible(hello.ProtocolVersion.ToString()))
            {
                if (reportedIncompatible.TryRemove(reportId, out _))
                {
                    Plugin.Log.LogInfo($"[protocol] {reportId} answered late with a matching protocol {hello.ProtocolVersion}.");
                }

                return;
            }

            var name = playerManagerService.GetPlayer(reportId)?.Name ?? (isHost ? "A player" : "The host");
            ReportIncompatible(reportId, name, hello);
        }

        private void ReportIncompatible(uint connectionId, string name, Common.Messages.GameNetworkMessages.ProtocolHello hello)
        {
            if (!reportedIncompatible.TryAdd(connectionId, true))
            {
                return;
            }

            var theirs = hello == null ? "an older mod build (no answer)" : $"version {hello.ModVersion}, protocol {hello.ProtocolVersion}";
            Plugin.Log.LogWarning(
                $"[protocol] {name} ({connectionId}) is on {theirs}; this build is {MyPluginInfo.PLUGIN_VERSION}, " +
                $"protocol {Common.Protocol.Version}. The run cannot start until everyone has the same version.");

            Plugin.StartNotification(
                ("OmniBonk", "VersionMismatch"),
                ("OmniBonk", "VersionMismatch_Description"),
                [string.IsNullOrWhiteSpace(name) ? "Player" : name]);
        }

        /// <summary>A client still inside its grace window is not shown as wrong yet.</summary>
        private bool IsIncompatible(uint connectionId, long now, bool isHost)
        {
            var hello = isHost
                ? helloByConnectionId.TryGetValue(connectionId, out var clientHello) ? clientHello : null
                : hostHello;

            if (hello != null)
            {
                return !Common.Protocol.IsCompatible(hello.ProtocolVersion.ToString());
            }

            // A client times the host from its first hello, not from the roster.
            if (!isHost)
            {
                return firstHelloSentTicks != 0 && now - firstHelloSentTicks >= ProtocolHelloGraceMs;
            }

            if (now - firstSeenTicksByConnectionId.GetOrAdd(connectionId, now) < ProtocolHelloGraceMs)
            {
                return false;
            }

            ReportIncompatible(connectionId, playerManagerService.GetPlayer(connectionId)?.Name ?? "A player", null);
            return true;
        }

        /// <summary>Host only. Requires an actual hello; older builds never send one.</summary>
        private bool AreAllClientsCompatible() =>
            playerManagerService.GetAllPlayersExceptLocal()
                .Where(p => !p.IsHost)
                .All(p => helloByConnectionId.TryGetValue(p.ConnectionId, out var hello)
                          && Common.Protocol.IsCompatible(hello.ProtocolVersion.ToString()));

        public bool IsInLobby =>
            Plugin.Instance?.Mode != null
            && !string.IsNullOrEmpty(Plugin.Instance.Mode.RoomCode);

        public string LobbyCode => Plugin.Instance?.Mode?.RoomCode ?? string.Empty;

        public bool IsLocalPlayerHost =>
            Plugin.Instance?.Mode?.Role == Common.Models.Role.Host;

        /// <summary>From config: the roster entry may not exist yet.</summary>
        private static string LocalPlayerName =>
            string.IsNullOrWhiteSpace(Configuration.ModConfig.PlayerName?.Value)
                ? "Player"
                : Configuration.ModConfig.PlayerName.Value;

        public IReadOnlyList<LobbyMemberView> GetMembers()
        {
            var local = playerManagerService.GetLocalPlayer();
            var localId = local?.ConnectionId;

            // Host first, then connection id, so rows don't swap between refreshes.
            var steamIdsByConnectionId = ResolveSteamIds();

            var roster = playerManagerService.GetAllPlayers().ToList();
            BroadcastRosterIfDue(roster);
            SendProtocolHelloIfDue(localId);

            var now = Environment.TickCount64;
            var isHost = netTransport.IsHost() == true;

            var members = roster
                .OrderByDescending(p => p.IsHost)
                .ThenBy(p => p.ConnectionId)
                .Select(p => new LobbyMemberView(
                    p.ConnectionId,
                    string.IsNullOrWhiteSpace(p.Name) ? "Player" : p.Name,
                    p.IsHost,
                    localId.HasValue && p.ConnectionId == localId.Value,
                    IsReady(p.ConnectionId),
                    steamIdsByConnectionId.TryGetValue(p.ConnectionId, out var steamId) ? steamId : 0UL,

                    // Host judges clients; a client judges only the host.
                    isIncompatible: !(localId.HasValue && p.ConnectionId == localId.Value)
                        && (isHost ? !p.IsHost : p.IsHost)
                        && IsIncompatible(p.ConnectionId, now, isHost)))
                .ToList();

            // A host alone is not in the roster (filled from the peer list), so synthesize its row.
            if (IsInLobby && !members.Any(m => m.IsLocal))
            {
                members.Insert(0, new LobbyMemberView(
                    localId ?? 0u,
                    LocalPlayerName,
                    IsLocalPlayerHost,
                    isLocal: true,
                    IsLocalPlayerReady,

                    // No connection id published yet, so read our own Steam id directly.
                    steamService.IsAvailable ? steamService.LocalSteamId : 0UL));
            }

            return members;
        }

        /// <summary>
        /// Connection id to Steam id from lobby member data. Empty on the matchmaker transport.
        /// Steam calls read a local cache, so per-refresh rebuild is cheap.
        /// </summary>
        private Dictionary<uint, ulong> ResolveSteamIds()
        {
            var map = new Dictionary<uint, ulong>();

            if (!Configuration.ModConfig.UseSteamTransport.Value
                || steamLobbyService.State != SteamLobbyState.InLobby)
            {
                return map;
            }

            foreach (var steamId in steamLobbyService.GetMembers())
            {
                var raw = steamLobbyService.GetMemberData(steamId, SteamLobbyKeys.MemberConnectionId);
                if (!string.IsNullOrEmpty(raw) && uint.TryParse(raw, out var connectionId))
                {
                    map[connectionId] = steamId;
                }
            }

            return map;
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

                // Empty is not ready (unlike ReadinessService's barrier): nobody has joined yet.
                return members.Count > 0
                    && members.All(p => IsReady(p.ConnectionId))
                    && (netTransport.IsHost() != true || AreAllClientsCompatible());
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
                readyByConnectionId[local.ConnectionId] = next;
                BroadcastReadyState();
                return;
            }

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

            // Connection ids can be reused by the next session.
            helloByConnectionId.Clear();
            hostHello = null;
            firstSeenTicksByConnectionId.Clear();
            reportedIncompatible.Clear();
            firstHelloSentTicks = 0;
            lastHelloSentTicks = 0;
        }

        public void RequestStart()
        {
            if (netTransport.IsHost() != true)
            {
                Plugin.Log.LogWarning("[lobby] Only the host can start the lobby.");
                return;
            }

            // Re-check: the button state can be half a second stale.
            if (!AreAllMembersReady)
            {
                Plugin.Log.LogInfo("[lobby] Start ignored: not every member is ready, or a client has not confirmed the same mod version.");
                return;
            }

            Plugin.Log.LogInfo("[lobby] Host started the lobby; advancing every peer to character selection.");

            netTransport.SendToAllClients(
                new Common.Messages.GameNetworkMessages.LobbyStartRequested(),
                NetDelivery.ReliableOrdered);

            // Same path as clients.
            EventManager.OnLobbyStartRequested();
        }

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

        /// <summary>Client only. May run off the main thread, hence the ConcurrentDictionary.</summary>
        private void ApplyHostState(IEnumerable<Common.Messages.GameNetworkMessages.LobbyReadyEntry> entries)
        {
            readyByConnectionId.Clear();

            foreach (var entry in entries)
            {
                readyByConnectionId[entry.ConnectionId] = entry.IsReady;
            }

            ReconcilePendingReady();
        }

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

        /// <summary>Host only. Sent on change, not on a tick.</summary>
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
            if (pendingLocalReady.HasValue)
            {
                var local = playerManagerService.GetLocalPlayer();
                if (local != null && local.ConnectionId == connectionId)
                {
                    // Also time out here, in case no further broadcast arrives.
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

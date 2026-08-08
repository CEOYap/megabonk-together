using System.Collections.Generic;
using System.Linq;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// One row of the lobby panel's member list, in the terms the panel draws rather than the terms
    /// the network layer stores.
    /// </summary>
    public readonly struct LobbyMemberView(uint connectionId, string name, bool isHost, bool isLocal)
    {
        public uint ConnectionId { get; } = connectionId;
        public string Name { get; } = name;
        public bool IsHost { get; } = isHost;
        public bool IsLocal { get; } = isLocal;
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
    }

    internal class LobbyViewService(IPlayerManagerService playerManagerService) : ILobbyViewService
    {
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
                    localId.HasValue && p.ConnectionId == localId.Value))
                .ToList();
        }
    }
}

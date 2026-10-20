using BepInEx.Logging;
using MegabonkTogether.Common.Messages.GameNetworkMessages;
using MegabonkTogether.Configuration;
using MegabonkTogether.Patches;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// The session's lobby settings: the host's choice, applied identically on every peer.
    ///
    /// <para><b>Host.</b> At run start it snapshots its own config (<c>[Lobby]</c>) and sends that
    /// to every client just before <c>RunStarted</c>. The snapshot, not the live config, is what
    /// applies for the rest of the session, so a mid-run config edit cannot split the peers.</para>
    ///
    /// <para><b>Client.</b> Applies whatever the host sent and never reads its own config for
    /// these. Until a message arrives everything is off, the vanilla behaviour.</para>
    /// </summary>
    public interface ILobbySettingsService
    {
        /// <summary>No Projectile Cap is in effect for this session.</summary>
        bool NoProjectileCap { get; }

        /// <summary>Endless Run is on for this session.</summary>
        bool EndlessEnabled { get; }

        /// <summary>Host: snapshot the config and send it to every client.</summary>
        void BroadcastToClients();

        /// <summary>Session over: back to vanilla, and undo what the settings changed.</summary>
        void Reset();
    }

    internal class LobbySettingsService : ILobbySettingsService
    {
        private readonly INetTransport transport;
        private readonly ManualLogSource logger;

        private LobbySettings current = new();

        public LobbySettingsService(INetTransport transport, ManualLogSource logger)
        {
            this.transport = transport;
            this.logger = logger;
            EventManager.SubscribeLobbySettingsEvents(OnReceivedLobbySettings);
        }

        public bool NoProjectileCap => current.NoProjectileCap;

        public bool EndlessEnabled => current.EndlessEnabled;

        public void BroadcastToClients()
        {
            current = new LobbySettings
            {
                NoProjectileCap = ModConfig.NoProjectileCap.Value,
                EndlessEnabled = ModConfig.EndlessRun.Value,
            };

            transport.SendToAllClients(current, NetDelivery.ReliableOrdered);
            logger.LogInfo($"[lobby-settings] Host settings for this session: {Describe(current)}.");
        }

        private void OnReceivedLobbySettings(LobbySettings settings)
        {
            current = settings ?? new LobbySettings();
            logger.LogInfo($"[lobby-settings] Applying the host's settings: {Describe(current)}.");
        }

        public void Reset()
        {
            current = new LobbySettings();
            NoProjectileCapPatches.RestoreAll();
        }

        private static string Describe(LobbySettings settings)
            => $"no projectile cap {(settings.NoProjectileCap ? "ON" : "off")}, endless {(settings.EndlessEnabled ? "ON" : "off")}";
    }
}

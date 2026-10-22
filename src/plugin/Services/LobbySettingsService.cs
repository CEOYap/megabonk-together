using BepInEx.Logging;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Configuration;
using OmniBonk.Patches;

namespace OmniBonk.Services
{
    /// <summary>
    /// The host's <c>[Lobby]</c> config, snapshotted at run start and applied on every peer.
    /// Clients never read their own config for these; all off until the host's message arrives.
    /// </summary>
    public interface ILobbySettingsService
    {
        bool NoProjectileCap { get; }

        bool EndlessEnabled { get; }

        /// <summary>Host only, just before <c>RunStarted</c>.</summary>
        void BroadcastToClients();

        /// <summary>Back to vanilla, undoing what the settings changed.</summary>
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
                SharedExperience = ModConfig.EnabledSharedExperience.Value,
            };

            // At run start, not lobby creation: the toggle lives in the open lobby.
            Plugin.Instance.Mode.EnabledSharedExperience = current.SharedExperience;

            transport.SendToAllClients(current, NetDelivery.ReliableOrdered);
            logger.LogInfo($"[lobby-settings] Host settings for this session: {Describe(current)}.");
        }

        private void OnReceivedLobbySettings(LobbySettings settings)
        {
            current = settings ?? new LobbySettings();
            Plugin.Instance.Mode.EnabledSharedExperience = current.SharedExperience;
            logger.LogInfo($"[lobby-settings] Applying the host's settings: {Describe(current)}.");
        }

        public void Reset()
        {
            current = new LobbySettings();
            NoProjectileCapPatches.RestoreAll();
        }

        private static string Describe(LobbySettings settings)
            => $"no projectile cap {(settings.NoProjectileCap ? "ON" : "off")}, endless {(settings.EndlessEnabled ? "ON" : "off")}, "
               + $"shared experience {(settings.SharedExperience ? "ON" : "off")}";
    }
}

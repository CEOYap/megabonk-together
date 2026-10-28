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

        bool NoEnemyCap { get; }

        bool EndlessEnabled { get; }

        /// <summary>
        /// OB-65: whether a chest opens with no window in this session. The lobby setting turns it on
        /// for everyone. Under Shared XP it alone decides, so every peer agrees and nobody waits at the
        /// barrier for a window another peer skipped; otherwise the player's own game setting also counts.
        /// </summary>
        bool SkipsChestAnimation();

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

        public bool NoEnemyCap => current.NoEnemyCap;

        public bool EndlessEnabled => current.EndlessEnabled;

        public bool SkipsChestAnimation()
        {
            if (current.SkipChestAnimation)
            {
                return true;
            }

            if (current.SharedExperience)
            {
                return false;
            }

            return SaveManager.Instance?.config?.cfGameSettings?.skip_chest_animation == 1;
        }

        public void BroadcastToClients()
        {
            current = new LobbySettings
            {
                NoProjectileCap = ModConfig.NoProjectileCap.Value,
                EndlessEnabled = ModConfig.EndlessRun.Value,
                SharedExperience = ModConfig.EnabledSharedExperience.Value,
                NoEnemyCap = ModConfig.NoEnemyCap.Value,
                SkipChestAnimation = ModConfig.SkipChestAnimation.Value,
            };

            // At run start, not lobby creation: the toggle lives in the open lobby.
            Plugin.Instance.Mode.EnabledSharedExperience = current.SharedExperience;
            Patches.Enemies.EnemyPoolSize.Apply(current.NoEnemyCap);

            transport.SendToAllClients(current, NetDelivery.ReliableOrdered);
            logger.LogInfo($"[lobby-settings] Host settings for this session: {Describe(current)}.");
        }

        private void OnReceivedLobbySettings(LobbySettings settings)
        {
            current = settings ?? new LobbySettings();
            Plugin.Instance.Mode.EnabledSharedExperience = current.SharedExperience;
            Patches.Enemies.EnemyPoolSize.Apply(current.NoEnemyCap);
            logger.LogInfo($"[lobby-settings] Applying the host's settings: {Describe(current)}.");
        }

        public void Reset()
        {
            current = new LobbySettings();
            NoProjectileCapPatches.RestoreAll();
            Patches.Enemies.EnemyPoolSize.Restore();
        }

        private static string Describe(LobbySettings settings)
            => $"no projectile cap {(settings.NoProjectileCap ? "ON" : "off")}, endless {(settings.EndlessEnabled ? "ON" : "off")}, "
               + $"shared experience {(settings.SharedExperience ? "ON" : "off")}, no enemy cap {(settings.NoEnemyCap ? "ON" : "off")}, "
               + $"skip chest animation {(settings.SkipChestAnimation ? "ON" : "off")}";
    }
}

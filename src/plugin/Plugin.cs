using Assets.Scripts.Managers;
using Assets.Scripts.Game.MapGeneration.MapEvents;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Menu.Shop;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using OmniBonk.Common;
using OmniBonk.Common.Models;
using OmniBonk.Configuration;
using OmniBonk.Helpers;
using OmniBonk.Scripts;
using OmniBonk.Scripts.Button;
using OmniBonk.Scripts.Enemies;
using OmniBonk.Scripts.Interactables;
using OmniBonk.Scripts.Modal;
using OmniBonk.Scripts.NetPlayer;
using OmniBonk.Scripts.Snapshot;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace OmniBonk
{
    public enum DistanceToPlayer
    {
        Close,
        Medium,
        Far
    }

    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    // Pre-1.0 GUID: both builds installed would run every patch twice.
    [BepInIncompatibility("MegabonkTogether")]
    public class Plugin : BasePlugin
    {
        public static Plugin Instance = null!;
        public NetworkHandler NetworkHandler = null;
        public Dictionary<ECharacter, RawImage> CharactersIcon = [];
        public NetPlayersDisplayer NetPlayersDisplayer = null;
        public CameraSwitcher CameraSwitcher = null;
        public PlayTogetherButton PlayTogetherButton = null;
        public AchievementPopup AchievementPopup = null;
        public NotificationQueueManager NotificationQueueManager = null;
        private MainMenu MainMenu = null;
        private MapEventsManager mapEventsManager = null;
        private MapEventsDesert mapEventsDesert = null;
        private LoadingModal modal;
        public NetworkMode Mode = new();

        public static IHost Host = null!;
        public static IServiceProvider Services => Host.Services;

        internal static new ManualLogSource Log;
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private CancellationToken cancellationToken;
        public static float PLAYER_FEET_OFFSET_Y = 1.50f;

        public static bool CAN_SPAWN_PICKUPS = false;
        public static bool CAN_SPAWN_CHESTS = false;
        public static bool CAN_SEND_MESSAGES = true;
        public static bool CAN_ENEMY_EXPLODE = false;
        public static bool CAN_ENEMY_USE_SPECIAL_ATTACK = false;
        public bool CAN_SPAWN_TORNADOES = false;
        public bool CAN_START_STOP_STORMS = false;
        public bool CAN_DAMAGE_ENEMIES = false;
        public bool IS_HOST_READY = false;
        public bool IS_MANUAL_INVINCIBLE = false;
        public bool IS_NETPLAYER_ADDING_TOME = false;

        /// <summary>
        /// FIX P1-10: exception-safe scope for <c>CAN_SEND_MESSAGES = false</c>; a skipped restore
        /// silences this peer for the rest of the run. Restores the previous value so scopes nest.
        /// </summary>
        public readonly struct OutboundSuppression : IDisposable
        {
            private readonly bool previous;

            internal OutboundSuppression(bool previous)
            {
                this.previous = previous;
            }

            public void Dispose()
            {
                CAN_SEND_MESSAGES = previous;
            }
        }

        /// <summary>Always use with <c>using</c>.</summary>
        public static OutboundSuppression SuppressOutbound()
        {
            var scope = new OutboundSuppression(CAN_SEND_MESSAGES);
            CAN_SEND_MESSAGES = false;
            return scope;
        }

        public uint? CurrentReviver = null;
        public uint? CurrentReviverOwner = null;

        private Vector3 WorldSize = Vector3.zero;
        public Vector3 OriginalWorldSize = Vector3.zero;
        public bool HasDungeonTimerStarted = false;

        private readonly ConcurrentDictionary<string, GameObject> prefabs = new();

        private Il2CppSystem.Action originalDiedAction = null;
        private Il2CppSystem.Action<WeaponBase> originalWeaponAddedAction = null;
        private Il2CppSystem.Action<EStat> originalStatUpdateAction = null;

        // Separate flags: a saved original can legitimately be null.
        private bool hasPreventedDeath = false;
        private bool hasSavedInventoryActions = false;

        private static int distanceTargetFrame = -1;
        private static Vector3 distanceTarget;

        public override void Load()
        {
            Instance = this;
            cancellationToken = cancellationTokenSource.Token;

            Log = base.Log;
            Log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

            ModConfig.Initialize(Config);

            Helpers.UnityDiagnostics.EnableStackTraces();
            Log.LogInfo($"Player name set to: {ModConfig.PlayerName.Value}");

            // Diagnostic. Caught at the call site: a MissingMethodException is thrown at JIT time,
            // so a try inside Hook() cannot catch it. Fully qualified because "Services." binds to Plugin.Services.
            try
            {
                OmniBonk.Services.UnityExceptionDiagnostics.Hook();
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"[unity-exc] Diagnostic unavailable, continuing without it: {ex.GetType().Name}: {ex.Message}");
            }

            // Reports whether Unity log lines reach LogOutput.log. Same call-site guard as above.
            try
            {
                OmniBonk.Services.LogCaptureDiagnostics.Report();
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"[log-capture] Diagnostic unavailable, continuing without it: {ex.GetType().Name}: {ex.Message}");
            }

            ClassInjector.RegisterTypeInIl2Cpp<NetPlayer>();
            ClassInjector.RegisterTypeInIl2Cpp<CoroutineRunner>();
            ClassInjector.RegisterTypeInIl2Cpp<MainThreadDispatcher>();
            ClassInjector.RegisterTypeInIl2Cpp<NetworkHandler>();
#if DEBUG
            ClassInjector.RegisterTypeInIl2Cpp<Scripts.Bot.BotController>();
#endif
            ClassInjector.RegisterTypeInIl2Cpp<PlayerInterpolator>();
            ClassInjector.RegisterTypeInIl2Cpp<EnemyInterpolator>();
            ClassInjector.RegisterTypeInIl2Cpp<BossOrbInterpolator>();
            ClassInjector.RegisterTypeInIl2Cpp<ProjectileInterpolator>();
            ClassInjector.RegisterTypeInIl2Cpp<TumbleWeedInterpolator>();
            ClassInjector.RegisterTypeInIl2Cpp<NetPlayersDisplayer>();
            ClassInjector.RegisterTypeInIl2Cpp<NetPlayerCard>();
            ClassInjector.RegisterTypeInIl2Cpp<DisplayBar>();
            ClassInjector.RegisterTypeInIl2Cpp<CustomInventoryHud>();
            ClassInjector.RegisterTypeInIl2Cpp<CameraSwitcher>();
            ClassInjector.RegisterTypeInIl2Cpp<PlayTogetherButton>();
            ClassInjector.RegisterTypeInIl2Cpp<CustomButton>();
            ClassInjector.RegisterTypeInIl2Cpp<ModalBase>();
            ClassInjector.RegisterTypeInIl2Cpp<Scripts.Modal.LobbyPanel>();
            ClassInjector.RegisterTypeInIl2Cpp<LoadingModal>();
            ClassInjector.RegisterTypeInIl2Cpp<UpdateAvailableModal>();
            ClassInjector.RegisterTypeInIl2Cpp<ChangelogModal>();
            ClassInjector.RegisterTypeInIl2Cpp<TargetSwitcher>();
            ClassInjector.RegisterTypeInIl2Cpp<TargetSwitcherManager>();
            ClassInjector.RegisterTypeInIl2Cpp<EnemyInterpolatorManager>();
            ClassInjector.RegisterTypeInIl2Cpp<SteamTicker>();

            // Not MonoBehaviours, but Steamworks callback subclasses; still need IL2CPP registration.
            ClassInjector.RegisterTypeInIl2Cpp<Services.SteamCallResult>();
            ClassInjector.RegisterTypeInIl2Cpp<Services.SteamCallback>();
            ClassInjector.RegisterTypeInIl2Cpp<InteractableReviver>();
            ClassInjector.RegisterTypeInIl2Cpp<NotificationQueueManager>();

            var builder = new HostBuilder();

            string contentRoot = System.IO.Directory.GetCurrentDirectory();
            builder.UseContentRoot(contentRoot);

            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Log);
                services.AddSingleton<IWebsocketClientService, WebsocketClientService>();

                services.AddSingleton<IUdpClientService, UdpClientService>();

                // Forwards to the existing singleton (never a second instance/socket). Transport is
                // chosen once at startup so it cannot change mid-session.
                services.AddSingleton<INetTransport>(sp =>
                    Configuration.ModConfig.UseSteamTransport.Value
                        ? sp.GetRequiredService<ISteamNetTransport>()
                        : sp.GetRequiredService<IUdpClientService>());

                services.AddSingleton<INetMessageRouter, NetMessageRouter>();

                services.AddSingleton<IStateBroadcastService, StateBroadcastService>();
                services.AddSingleton<IPlayerManagerService, PlayerManagerService>();
                services.AddSingleton<IEnemyManagerService, EnemyManagerService>();
                services.AddSingleton<IPlayerStatsSyncService, PlayerStatsSyncService>();
                services.AddSingleton<IDesyncDetectorService, DesyncDetectorService>();
                services.AddSingleton<IProjectileManagerService, ProjectileManagerService>();
                services.AddSingleton<ISynchronizationService, SynchronizationService>();
                services.AddSingleton<IPickupManagerService, PickupManagerService>();
                services.AddSingleton<IChestManagerService, ChestManagerService>();
                services.AddSingleton<ISpawnedObjectManagerService, SpawnedObjectManagerService>();
                services.AddSingleton<IFinalBossOrbManagerService, FinalBossOrbManagerService>();
                services.AddSingleton<ILocalizationService, LocalizationService>();
                services.AddSingleton<IGameBalanceService, GameBalanceService>();
                services.AddSingleton<IAutoUpdaterService, AutoUpdaterService>();
                services.AddSingleton<IChangelogService, ChangelogService>();
                services.AddSingleton<IEncounterService, EncounterService>();
                services.AddSingleton<IReadinessService, ReadinessService>();
                services.AddSingleton<ILobbyViewService, LobbyViewService>();
                services.AddSingleton<IUiAssetService, UiAssetService>();
                services.AddSingleton<ITrackerService, TrackerService>();
                services.AddSingleton<ILobbySettingsService, LobbySettingsService>();
                services.AddSingleton<IEndlessRunService, EndlessRunService>();
                services.AddSingleton<ISteamService, SteamService>();
                services.AddSingleton<ISteamLobbyService, SteamLobbyService>();
                services.AddSingleton<ISteamAvatarService, SteamAvatarService>();

                services.AddSingleton<ISteamNetTransport, SteamNetTransport>();
                services.AddSingleton<ISteamNetSessionService, SteamNetSessionService>();
                services.AddSingleton<SteamNetSelfTest>();
                services.AddSingleton<SteamLobbySelfTest>();
                services.AddSingleton<SteamLobbyPresenceService>();
                services.AddSingleton<SteamInviteService>();
                services.AddSingleton<SteamPersonaService>();
                services.AddSingleton<INetplaySessionService, NetplaySessionService>();
            });

            Host = builder.Build();

            // Not from the constructor: that created a DI cycle that deadlocked startup.
            Services.GetRequiredService<ILobbyViewService>().SubscribeToLobbyMessages();


            _ = Services.GetRequiredService<ISynchronizationService>(); // Initialize SynchronizationService
            _ = Host.StartAsync(cancellationToken);
            var autoUpdaterService = Services.GetRequiredService<IAutoUpdaterService>();

            if (ModConfig.CheckForUpdates.Value)
            {
                autoUpdaterService.Initialize();

                Task.Run(async () =>
                {
                    try
                    {
                        var updateAvailable = await autoUpdaterService.CheckAndUpdate();
                        if (updateAvailable && !autoUpdaterService.IsCustomBuild())
                        {
                            Log.LogInfo("An update has been downloaded and will be applied when you quit the game.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.LogError($"Auto-update check failed: {ex.Message}");
                    }
                });
            }
            else
            {
                Log.LogInfo("Auto-update is disabled in configuration.");
            }

            try
            {
                var harmony = new HarmonyLib.Harmony(MyPluginInfo.PLUGIN_GUID);
                harmony.PatchAll();
            }
            catch (Exception ex)
            {
                Log.LogError($"Harmony patching failed: {ex}");
            }

            // Outside the catch on purpose.
            Helpers.EncounterInputGrace.MarkRuntimeReady();

            var go = new GameObject("MainThreadDispatcher");
            GameObject.DontDestroyOnLoad(go);
            go.AddComponent<MainThreadDispatcher>();

            var goNetworkHandler = new GameObject("NetworkHandler");
            GameObject.DontDestroyOnLoad(goNetworkHandler);
            NetworkHandler = goNetworkHandler.AddComponent<NetworkHandler>();
#if DEBUG
            // Test bot: only on a BotMode copy (read once at load; turning it on needs a restart).
            if (Configuration.ModConfig.BotMode.Value)
            {
                goNetworkHandler.AddComponent<Scripts.Bot.BotController>();
            }
#endif

            var goNetPlayersDisplayer = new GameObject("NetPlayersDisplayer");
            GameObject.DontDestroyOnLoad(goNetPlayersDisplayer);
            NetPlayersDisplayer = goNetPlayersDisplayer.AddComponent<NetPlayersDisplayer>();

            var goCameraSwitcher = new GameObject("CameraSwitcher");
            GameObject.DontDestroyOnLoad(goCameraSwitcher);
            CameraSwitcher = goCameraSwitcher.AddComponent<CameraSwitcher>();

            var goNotificationQueueManager = new GameObject("NotificationQueueManager");
            GameObject.DontDestroyOnLoad(goNotificationQueueManager);
            NotificationQueueManager = goNotificationQueueManager.AddComponent<NotificationQueueManager>();

            // PERF 1A: one Update for all TargetSwitchers instead of ~600.
            var goTargetSwitcherManager = new GameObject("TargetSwitcherManager");
            GameObject.DontDestroyOnLoad(goTargetSwitcherManager);
            goTargetSwitcherManager.AddComponent<TargetSwitcherManager>();

            var goEnemyInterpolatorManager = new GameObject("EnemyInterpolatorManager");
            GameObject.DontDestroyOnLoad(goEnemyInterpolatorManager);
            goEnemyInterpolatorManager.AddComponent<EnemyInterpolatorManager>();

            // SteamManager initialises after plugin load, so a ticker retries at 1 Hz until it exists.
            var goSteamTicker = new GameObject("SteamTicker");
            GameObject.DontDestroyOnLoad(goSteamTicker);
            goSteamTicker.AddComponent<SteamTicker>();
        }

        public void AddPrefab(GameObject prefab)
        {
            prefabs.TryAdd(prefab.name, prefab);
        }

        public GameObject GetPrefab(string name)
        {
            if (prefabs.TryGetValue(name.Trim(), out var prefab))
            {
                return prefab;
            }

            Plugin.Log.LogWarning($"Prefab not found: {name}");

            return null;
        }

        public void ClearPrefabs()
        {
            prefabs.Clear();
        }

        public void ClearCharacterIcons()
        {
            CharactersIcon.Clear();
        }

        public void AddCharacterIcons(Il2CppSystem.Collections.Generic.List<MyButtonCharacter> characterButtons)
        {
            if (CharactersIcon.Count > 0)
            {
                Log.LogWarning("Character icons already added");
                return;
            }

            foreach (var button in characterButtons)
            {
                var iconObj = UnityEngine.Object.Instantiate(button.i_icon);
                UnityEngine.Object.DontDestroyOnLoad(iconObj);
                CharactersIcon.Add(button.characterData.eCharacter, iconObj);
            }

        }

        public void PreventDeath()
        {
            if (hasPreventedDeath)
            {
                Log.LogWarning("Death already prevented");
                return;
            }

            // The flag is the state; the saved delegate may be null.
            originalDiedAction = PlayerHealth.A_Died;
            hasPreventedDeath = true;
            PlayerHealth.A_Died = new Action(OnPlayerDied);
        }

        private void OnPlayerDied()
        {
            if (CameraSwitcher == null || CameraSwitcher.IsFollowingTarget)
            {
                return;
            }

            var playerManager = Services.GetService<IPlayerManagerService>();
            var netPlayer = playerManager.GetRandomNetPlayer();

            if (netPlayer != null)
            {
                CameraSwitcher.SwitchToTarget(netPlayer.ConnectionId);
            }
        }

        public void RestoreDeath(bool invokeDeathEvent)
        {
            // Normal on every return to the main menu when no run took place: nothing to restore.
            if (!hasPreventedDeath)
            {
                return;
            }

            if (CameraSwitcher != null)
            {
                CameraSwitcher.ResetToLocalPlayer();
            }

            // Restore first: everything below can throw, and a leaked handler reaches singleplayer.
            var restored = originalDiedAction;
            PlayerHealth.A_Died = restored;
            originalDiedAction = null;
            hasPreventedDeath = false;

            if (!invokeDeathEvent)
            {
                return;
            }

            if (restored == null)
            {
                Log.LogWarning("Cannot invoke the game's death handler: there was none installed when the session started.");
                return;
            }

            // The game's death sequence throws an NRE at Animator.set_speed every session (undiagnosed;
            // docs/netplay/06-session-handoff.md item 3). Contained here so the caller finishes.
            try
            {
                GameManager.Instance.player.playerRenderer.gameObject.SetActive(true);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Could not re-enable the player renderer before the death handler: {ex}");
            }

            // Invoke subscribers individually: a multicast Invoke stops at the thrower and could skip
            // GameManager.OnDied (VA 0x180503950), the whole game-over sequence.
            // Try is around the call: a MissingMethodException from GetInvocationList fires at JIT time.
            try
            {
                InvokeDeathSubscribersIsolated(restored);
            }
            catch (Exception ex)
            {
                Log.LogWarning(
                    $"Could not split the death subscriber list ({ex.GetType().Name}: {ex.Message}); " +
                    "falling back to a single invoke.");

                try
                {
                    restored.Invoke();
                }
                catch (Exception inner)
                {
                    Log.LogWarning($"The game's death handler threw while being restored (invokeDeathEvent): {inner}");
                }
            }
        }

        /// <summary>
        /// Invokes each death subscriber separately. No <c>GameManager.OnDied</c> fallback: it could
        /// run the death animation twice.
        /// </summary>
        private static void InvokeDeathSubscribersIsolated(Il2CppSystem.Action subscribers)
        {
            var invocationList = subscribers.GetInvocationList();

            if (invocationList == null || invocationList.Length == 0)
            {
                subscribers.Invoke();
                return;
            }

            for (var i = 0; i < invocationList.Length; i++)
            {
                // TryCast, never a C# cast, on Il2CppInterop proxies.
                var subscriber = invocationList[i]?.TryCast<Il2CppSystem.Action>();
                if (subscriber == null)
                {
                    continue;
                }

                try
                {
                    subscriber.Invoke();
                }
                catch (Exception ex)
                {
                    Log.LogWarning(
                        $"Death subscriber {i + 1} of {invocationList.Length} threw; continuing with the rest: {ex}");
                }
            }
        }

        public AchievementPopup GetAchievementPopup()
        {
            if (AchievementPopup == null)
            {
                var gameObject = Il2CppFindHelper.FindAllGameObjects()
                    .FirstOrDefault(go => go.GetComponent<AchievementPopup>() != null);
                if (gameObject != null)
                {
                    AchievementPopup = gameObject.GetComponent<AchievementPopup>();

                    if (AchievementPopup != null && NotificationQueueManager != null)
                    {
                        NotificationQueueManager.Initialize();
                    }
                }
            }
            return AchievementPopup;
        }

        public MainMenu GetMainMenu()
        {
            return MainMenu;
        }

        public void SetMainMenu(MainMenu mainMenu)
        {
            MainMenu = mainMenu;
        }

        public static bool StartNotification(
            (string tableReference, string tableEntryReference) localizedName,
            (string tableReference, string tableEntryReference) localizedDescription,
            IEnumerable<string> descriptionArgs,
            RandomSfx sfx = null,
            EItem item = EItem.Key
        )
        {
            if (Instance?.NotificationQueueManager == null)
            {
                Log.LogWarning("NotificationQueueManager is not initialized");
                return false;
            }

            Instance.NotificationQueueManager.EnqueueNotification(
                localizedName,
                localizedDescription,
                descriptionArgs,
                sfx,
                item
            );

            return true;
        }

        public static void GoToMainMenu()
        {
            if (GameManager.Instance == null || GameManager.Instance.player == null)
            {
                var menu = Helpers.WindowCast.AsCharacterMenu(WindowManager.activeWindow);
                if (menu != null)
                {
                    menu.b_back.button.onClick.Invoke();
                }

                if (WindowManager.activeWindow.name.Contains("Maps And Stats"))
                {
                    WindowManager.activeWindow.allButtons.ToArray().FirstOrDefault(b => b.name == "B_Back")?.button.onClick.Invoke();
                    Helpers.WindowCast.AsCharacterMenu(WindowManager.activeWindow)?.b_back.button.onClick.Invoke();
                }
            }
            else
            {
                TransitionUI.Instance.LoadMenu();
            }
        }

        public void ShowModal(string message)
        {
            if (modal != null)
            {
                modal.UpdateMessage(message);
                return;
            }
            modal = LoadingModal.Show(message);
        }

        public void HideModal()
        {
            if (modal != null)
            {
                modal.Close();
                modal = null;
            }
        }

        public static void ShowUpdateAvailableModal()
        {
            var go = new GameObject("UpdateAvailableModal");
            go.AddComponent<UpdateAvailableModal>();
        }

        /// <summary>
        /// Silences the game's weapon/stat callbacks while editing a remote player's inventory.
        /// Always pair with <see cref="RestorePlayerInventoryActions"/> in a <c>finally</c>, or singleplayer breaks too.
        /// </summary>
        public void SavePlayerInventoryActions()
        {
            if (hasSavedInventoryActions)
            {
                Log.LogWarning("Player inventory actions already saved; not overwriting them with the silenced values.");
                return;
            }

            originalWeaponAddedAction = WeaponInventory.A_WeaponAdded;
            originalStatUpdateAction = PlayerStatsNew.A_StatUpdate;
            hasSavedInventoryActions = true;
            WeaponInventory.A_WeaponAdded = null;
            PlayerStatsNew.A_StatUpdate = null;
        }

        public void RestorePlayerInventoryActions()
        {
            // A saved action can legitimately be null; the flag records whether a save happened.
            if (!hasSavedInventoryActions)
            {
                Log.LogWarning("Player inventory actions not saved");
                return;
            }

            WeaponInventory.A_WeaponAdded = originalWeaponAddedAction;
            PlayerStatsNew.A_StatUpdate = originalStatUpdateAction;
            originalWeaponAddedAction = null;
            originalStatUpdateAction = null;
            hasSavedInventoryActions = false;
        }

        /// <summary>The Desert's map events, or null off the Desert or before they exist (storm messages can arrive late).</summary>
        public MapEventsDesert GetMapEventsDesert()
        {
            if (MapController.currentMap == null || MapController.currentMap.eMap != Assets.Scripts._Data.MapsAndStages.EMap.Desert)
            {
                return null;
            }

            if (mapEventsDesert == null)
            {
                mapEventsDesert = GetMapEventsManager()?.mapEvents?.TryCast<MapEventsDesert>();
            }

            return mapEventsDesert;
        }

        private MapEventsManager GetMapEventsManager()
        {
            if (mapEventsManager == null)
            {
                mapEventsManager = Il2CppFindHelper.FindAllGameObjects()
                    .FirstOrDefault(go => go.GetComponent<MapEventsManager>() != null)
                    ?.GetComponent<MapEventsManager>();
            }
            return mapEventsManager;
        }

        public void ClearMapEventsManager()
        {
            mapEventsManager = null;
            mapEventsDesert = null;
        }

        public void SetWorldSize(Vector3 size)
        {
            WorldSize = size;
        }

        public Vector3 GetWorldSize()
        {
            return WorldSize;
        }

        public void ResetWorldSize()
        {
            WorldSize = Vector3.zero;
            OriginalWorldSize = Vector3.zero;
        }

        public static DistanceToPlayer GetDistanceToPlayer(Vector3 position)
        {
            if (Time.frameCount != distanceTargetFrame)
            {
                var player = GameManager.Instance.player;
                if (player == null)
                {
                    return DistanceToPlayer.Far;
                }

                var target = player.transform.position;
                if (player.IsDead())
                {
                    // FIX P2-1: the spectate target may be a destroyed NetPlayer; fall back to the local player.
                    var cameraSwitcher = Instance?.CameraSwitcher;
                    if (cameraSwitcher != null) // Unity's overloaded == also catches a destroyed object
                    {
                        var spectateTarget = cameraSwitcher.GetCurrentTarget();
                        if (spectateTarget != null) // ditto: a destroyed NetPlayer transform reads as null
                        {
                            target = spectateTarget.position;
                        }
                    }
                }

                distanceTarget = target;
                distanceTargetFrame = Time.frameCount;
            }

            var distance = Vector3.Distance(position, distanceTarget);
            if (distance < Tunables.DISTANCE_CLOSE)
            {
                return DistanceToPlayer.Close;
            }
            else if (distance < Tunables.DISTANCE_MEDIUM)
            {
                return DistanceToPlayer.Medium;
            }
            else
            {
                return DistanceToPlayer.Far;
            }
        }

    }
}

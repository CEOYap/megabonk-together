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
using MegabonkTogether.Common;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Configuration;
using MegabonkTogether.Helpers;
using MegabonkTogether.Scripts;
using MegabonkTogether.Scripts.Button;
using MegabonkTogether.Scripts.Enemies;
using MegabonkTogether.Scripts.Interactables;
using MegabonkTogether.Scripts.Modal;
using MegabonkTogether.Scripts.NetPlayer;
using MegabonkTogether.Scripts.Snapshot;
using MegabonkTogether.Services;
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

namespace MegabonkTogether
{
    public enum DistanceToPlayer
    {
        Close,
        Medium,
        Far
    }

    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
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

        internal NetworkMenuTab NetworkTab { get; set; }

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
        /// FIX P1-10: scoped replacement for the bare
        /// `CAN_SEND_MESSAGES = false; …game call…; CAN_SEND_MESSAGES = true;` pattern that every
        /// receive handler used.
        ///
        /// <para>The flag stops this peer echoing back the state it is applying on someone else's
        /// behalf. Written by hand, the restore is skipped whenever the game call between the two
        /// assignments throws — and then <b>this peer never sends anything again for the rest of
        /// the run</b>: a total, unrecoverable desync from one exception. That is not theoretical;
        /// P0-6 is the same failure, where one throw latched two statics for 581 enemy spawns.</para>
        ///
        /// <para>Restores the <i>previous</i> value rather than hard-coding `true`. Identical at
        /// every current call site (all are entered with the flag set), and it is the only version
        /// that stays correct if two suppressed regions ever nest.</para>
        ///
        /// <para>A struct, so `using` costs no allocation on paths that run per received message.</para>
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

        /// <summary>
        /// Suppresses this peer's outbound messages until the returned scope is disposed. Use with
        /// `using`, never bare — the whole point is that the restore survives an exception.
        /// </summary>
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

        // Whether the mod currently has the game's handler swapped out. Kept separate from the
        // saved delegates because a legitimately-null original is indistinguishable from "never
        // saved" otherwise, and that ambiguity used to strand our handlers on the game's statics.
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
            Log.LogInfo($"Player name set to: {ModConfig.PlayerName.Value}");

            ClassInjector.RegisterTypeInIl2Cpp<NetPlayer>();
            ClassInjector.RegisterTypeInIl2Cpp<CoroutineRunner>();
            ClassInjector.RegisterTypeInIl2Cpp<MainThreadDispatcher>();
            ClassInjector.RegisterTypeInIl2Cpp<NetworkHandler>();
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
            ClassInjector.RegisterTypeInIl2Cpp<NetworkMenuTab>();
            ClassInjector.RegisterTypeInIl2Cpp<LoadingModal>();
            ClassInjector.RegisterTypeInIl2Cpp<UpdateAvailableModal>();
            ClassInjector.RegisterTypeInIl2Cpp<ChangelogModal>();
            ClassInjector.RegisterTypeInIl2Cpp<TargetSwitcher>();
            ClassInjector.RegisterTypeInIl2Cpp<TargetSwitcherManager>();
            ClassInjector.RegisterTypeInIl2Cpp<EnemyInterpolatorManager>();
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
                services.AddSingleton<IPlayerManagerService, PlayerManagerService>();
                services.AddSingleton<IEnemyManagerService, EnemyManagerService>();
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
                services.AddSingleton<ITrackerService, TrackerService>();
            });

            Host = builder.Build();


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

            var go = new GameObject("MainThreadDispatcher");
            GameObject.DontDestroyOnLoad(go);
            go.AddComponent<MainThreadDispatcher>();

            var goNetworkHandler = new GameObject("NetworkHandler");
            GameObject.DontDestroyOnLoad(goNetworkHandler);
            NetworkHandler = goNetworkHandler.AddComponent<NetworkHandler>();

            var goNetPlayersDisplayer = new GameObject("NetPlayersDisplayer");
            GameObject.DontDestroyOnLoad(goNetPlayersDisplayer);
            NetPlayersDisplayer = goNetPlayersDisplayer.AddComponent<NetPlayersDisplayer>();

            var goCameraSwitcher = new GameObject("CameraSwitcher");
            GameObject.DontDestroyOnLoad(goCameraSwitcher);
            CameraSwitcher = goCameraSwitcher.AddComponent<CameraSwitcher>();

            var goNotificationQueueManager = new GameObject("NotificationQueueManager");
            GameObject.DontDestroyOnLoad(goNotificationQueueManager);
            NotificationQueueManager = goNotificationQueueManager.AddComponent<NotificationQueueManager>();

            // PERF 1A: ticks every enemy's TargetSwitcher from one Update instead of ~600 injected
            // MonoBehaviour Updates per frame. Its Update no-ops while the registry is empty, so it
            // costs one call per frame in singleplayer.
            var goTargetSwitcherManager = new GameObject("TargetSwitcherManager");
            GameObject.DontDestroyOnLoad(goTargetSwitcherManager);
            goTargetSwitcherManager.AddComponent<TargetSwitcherManager>();

            var goEnemyInterpolatorManager = new GameObject("EnemyInterpolatorManager");
            GameObject.DontDestroyOnLoad(goEnemyInterpolatorManager);
            goEnemyInterpolatorManager.AddComponent<EnemyInterpolatorManager>();
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

            // FIX: `originalDiedAction != null` used to be the "already prevented" sentinel, which
            // conflates "not prevented yet" with "prevented, and the game's handler happened to be
            // null". In that second case RestoreDeath's mirror-image check bailed with "Death not
            // prevented" and left OnPlayerDied installed on the game's static for the rest of the
            // process — i.e. leaking into singleplayer, which is the one thing patches here must
            // never do. The flag is the state; the saved delegate is only data.
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
            if (!hasPreventedDeath)
            {
                Log.LogWarning("Death not prevented");
                return;
            }

            if (CameraSwitcher != null)
            {
                CameraSwitcher.ResetToLocalPlayer();
            }

            // Hand the game its own handler back first and unconditionally. Everything below can
            // throw, and of all the ways this method can fail, leaving our OnPlayerDied on a game
            // static is the only one that follows the player out of the session.
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

            // The game's own death sequence throws a NullReferenceException at Animator.set_speed
            // here, on host and clients, every session — pre-existing, and NOT diagnosed: the
            // stripped interop assemblies have no method bodies, so what that handler touches is
            // unknown without the IL2CPP dump. See open item 3 in docs/netplay/06-session-handoff.md.
            //
            // What is fixed here is the blast radius. The throw used to escape into
            // TransitionToState(GameEvent.GameOver) and land in MainThreadDispatcher's catch, far
            // from the fault and with no indication that the death event was the cause. Contained
            // and logged with its stack, the caller finishes and the next session's log names it.
            try
            {
                GameManager.Instance.player.playerRenderer.gameObject.SetActive(true);
                restored.Invoke();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"The game's death handler threw while being restored (invokeDeathEvent): {ex}");
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
                if (WindowManager.activeWindow is CharacterMenu menu)
                {
                    menu.b_back.button.onClick.Invoke();
                }

                if (WindowManager.activeWindow.name.Contains("Maps And Stats"))
                {
                    WindowManager.activeWindow.allButtons.ToArray().FirstOrDefault(b => b.name == "B_Back")?.button.onClick.Invoke();
                    (WindowManager.activeWindow as CharacterMenu)?.b_back.button.onClick.Invoke();
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
        /// Silences the game's weapon/stat callbacks while the mod builds or mutates a remote
        /// player's inventory, so that work does not look like the local player's.
        ///
        /// <para>Always pair with <see cref="RestorePlayerInventoryActions"/> in a `finally`: these
        /// null out two game-wide statics, and the calls they bracket are game code that can
        /// throw. Skipping the restore kills weapon-added and stat-update handling for the rest of
        /// the process, singleplayer included.</para>
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
            // FIX: this used to return early — before restoring EITHER static — whenever a saved
            // action was null, on the assumption that null meant "never saved". Null is also a
            // perfectly ordinary value for a game event with no subscribers, and in that case the
            // early return left both callbacks nulled out permanently. One flag now records
            // whether a save happened, and both statics are restored together.
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

        public MapEventsDesert GetMapEventsDesert()
        {
            if (mapEventsDesert == null && mapEventsManager == null)
            {
                mapEventsManager = GetMapEventsManager();
                mapEventsDesert = IL2CPP.PointerToValueGeneric<MapEventsDesert>(mapEventsManager.mapEvents.Pointer, false, false);
            }

            return mapEventsDesert;
        }

        private MapEventsManager GetMapEventsManager()
        {
            if (mapEventsManager == null)
            {
                mapEventsManager = Il2CppFindHelper.FindAllGameObjects()
                    .FirstOrDefault(go => go.GetComponent<MapEventsManager>() != null)
                    .GetComponent<MapEventsManager>();
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
                    // FIX P2-1 (second dangling path): while spectating, the camera target is a
                    // remote peer's NetPlayer transform. Nothing tells CameraSwitcher when that
                    // peer disconnects, so after PlayerManagerService.RemovePlayer destroys their
                    // GameObject this read hit a destroyed Transform — landing in the
                    // get_position fallback ~700 times per 5s on each of the two per-frame paths
                    // that call this method (DistanceThrottler.ShouldUpdate per enemy per
                    // FixedUpdate, ProjectileBasePatches.Update_Prefix per projectile per frame).
                    //
                    // Falling back to the local player's position is what that patch fallback was
                    // already substituting, so this changes no distance result — it just stops
                    // dereferencing a destroyed object to get there.
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
            if (distance < 25f)
            {
                return DistanceToPlayer.Close;
            }
            else if (distance < 60f)
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

using Assets.Scripts.Managers;
using HarmonyLib;
using MegabonkTogether.Helpers;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// Endless Run (docs/netplay/18, Phase 2). Detection only; <see cref="EndlessRunService"/>
    /// decides.
    ///
    /// <para><b>Gate.</b> Session initialized and Endless active, not "session started": a portal is
    /// detected in <c>DetectInteractables.TryInteract</c>'s prefix, which moves the session to
    /// <c>LoadingNextLevel</c> before the game's <c>Interact</c> calls the stage load. Endless is
    /// only ever active inside a netplay run, so singleplayer never reaches past the gate.</para>
    ///
    /// <para><b>No ownership check, on purpose.</b> Every peer runs its own stage load; the service
    /// steers each to the host's room and sends nothing from here.</para>
    /// </summary>
    [HarmonyPatch(typeof(MapController))]
    internal static class EndlessStageLoadPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IEndlessRunService endlessRunService = Plugin.Services.GetService<IEndlessRunService>();

        /// <summary>
        /// <c>StartNewMap</c> (0x1804222F0, decompiled) loads the run's first stage through
        /// <c>LoadNextStage</c> itself. That load is the run's start, not a stage change.
        /// </summary>
        private static bool startingRun;

        private static bool InEndless
            => !startingRun && synchronizationService.HasNetplaySessionInitialized() && endlessRunService.Active;

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapController.StartNewMap))]
        public static void StartNewMap_Prefix() => startingRun = true;

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(MapController.StartNewMap))]
        public static void StartNewMap_Finalizer() => startingRun = false;

        // Prefix-false only when the service ran the other load method in this one's place.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapController.LoadNextStage))]
        public static bool LoadNextStage_Prefix()
            => !InEndless || endlessRunService.OnStageLoadRequested(finalStageRequested: false);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapController.LoadFinalStage))]
        public static bool LoadFinalStage_Prefix()
            => !InEndless || endlessRunService.OnStageLoadRequested(finalStageRequested: true);
    }

    /// <summary>
    /// Endless: every regular room gets its portal after its stage boss. The game opens one only
    /// while <c>!IsLastStage() &amp;&amp; index &lt; runConfig.mapTierIndex</c> (OB-46,
    /// <c>CanSpawnPortal</c> 0x1804C5540). Clients ask the same method (OB-46), so host and clients
    /// agree.
    /// </summary>
    [HarmonyPatch(typeof(InteractableBossSpawner))]
    internal static class EndlessPortalPatches
    {
        private static readonly IEndlessRunService endlessRunService = Plugin.Services.GetService<IEndlessRunService>();

        [HarmonyPostfix]
        [HarmonyPatch(nameof(InteractableBossSpawner.CanSpawnPortal))]
        public static void CanSpawnPortal_Postfix(ref bool __result)
        {
            if (endlessRunService.Active && !MapController.isFinalBossStage)
            {
                __result = true;
            }
        }
    }

    /// <summary>
    /// Endless: the portal a final boss leaves goes to the next room. <c>Interact</c>
    /// (0x1804CCA20, decompiled) sets <c>done</c> and starts <c>DoFinishGame</c>, which ends the
    /// run. Prefix-false on purpose while Endless is active: this loads the next stage instead, and
    /// the stage-load patch steers it to the host's room. The session state is set to a normal
    /// stage change, not Endgame, in <c>SynchronizationService</c>.
    /// </summary>
    [HarmonyPatch(typeof(InteractablePortalFinal))]
    internal static class EndlessFinalPortalPatches
    {
        private static readonly IEndlessRunService endlessRunService = Plugin.Services.GetService<IEndlessRunService>();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(InteractablePortalFinal.Interact))]
        public static bool Interact_Prefix(InteractablePortalFinal __instance, ref bool __result)
        {
            if (!endlessRunService.Active)
            {
                return true;
            }

            if (__instance.done)
            {
                __result = false;
                return false;
            }

            __instance.done = true;
            MapController.LoadNextStage();
            __result = true;
            return false;
        }
    }
    /// <summary>
    /// Endless, Phase 3: Graveyard rooms. The arrival is moved into the chosen room at the spawn
    /// portal and started once the run is playing (<see cref="EndlessGraveyard"/>). Every peer runs
    /// this for its own arrival, so no ownership check; nothing is sent from here.
    /// </summary>
    [HarmonyPatch]
    internal static class EndlessGraveyardArrivalPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IEndlessRunService endlessRunService = Plugin.Services.GetService<IEndlessRunService>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SpawnPlayerPortal), nameof(SpawnPlayerPortal.StartPortal))]
        public static void StartPortal_Prefix(SpawnPlayerPortal __instance)
        {
            if (!synchronizationService.HasNetplaySessionInitialized() || !endlessRunService.TakeGraveyardArrival(out var room))
            {
                return;
            }

            EndlessGraveyard.Prepare(__instance, room);
        }

        // Per frame: one property read until an arrival is pending.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameManager), nameof(GameManager.Update))]
        public static void Update_Postfix(GameManager __instance)
        {
            if (!endlessRunService.Active || !__instance.IsPlaying() || !endlessRunService.TakeGraveyardActivation(out var room))
            {
                return;
            }

            EndlessGraveyard.Activate(room);
        }

        /// <summary>
        /// Prefix-false on purpose in the Ghost King room before its fight: the room is entered
        /// without the second crypt's exit that normally readies it, and its Update must not run
        /// ahead of <c>Activate</c>.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GraveyardBossRoom), "Update")]
        public static bool BossRoomUpdate_Prefix(GraveyardBossRoom __instance)
        {
            if (!endlessRunService.Active || endlessRunService.CurrentRoom != EndlessRoom.GhostKing || __instance.isFightingBoss)
            {
                return true;
            }

            return __instance.isBossDefeated;
        }
    }

    /// <summary>
    /// Endless, Phase 3: a Graveyard room's exit is a stage change to the next room, in place of the
    /// teleport to the next area of the same scene. Prefix-false on purpose in those rooms. The
    /// exits are already synced (<c>InteractableUsed</c>, then each peer replays <c>Interact</c>),
    /// so this runs on every peer. Each moves this peer to a normal stage change first, as a portal
    /// would, so the level's state is torn down before the next stage loads.
    /// </summary>
    [HarmonyPatch]
    internal static class EndlessGraveyardExitPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IEndlessRunService endlessRunService = Plugin.Services.GetService<IEndlessRunService>();

        private static bool In(EndlessRoom room)
            => endlessRunService.Active && endlessRunService.CurrentRoom == room && synchronizationService.HasNetplaySessionInitialized();

        private static void LeaveRoom()
        {
            synchronizationService.TransitionToState(GameEvent.PortalOpened);
            MapController.LoadNextStage();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InteractableCryptLeave), nameof(InteractableCryptLeave.Interact))]
        public static bool CryptLeave_Prefix(InteractableCryptLeave __instance, ref bool __result)
        {
            if (!In(EndlessRoom.Crypt1) && !In(EndlessRoom.Crypt2))
            {
                return true;
            }

            __result = !__instance.hasInteracted && __instance.CanInteract();
            if (__result)
            {
                __instance.hasInteracted = true;
                LeaveRoom();
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InteractableCrypt), nameof(InteractableCrypt.Interact))]
        public static bool KeyDoor_Prefix(InteractableCrypt __instance, ref bool __result)
        {
            // Closed: the game's own message about the missing keys.
            if (!In(EndlessRoom.Graveyard) || !__instance.IsOpen())
            {
                return true;
            }

            __result = !__instance.isDone && __instance.CanInteract();
            if (__result)
            {
                __instance.isDone = true;
                LeaveRoom();
            }

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InteractableGhostBossLeave), nameof(InteractableGhostBossLeave.Interact))]
        public static bool GhostBossLeave_Prefix(InteractableGhostBossLeave __instance, ref bool __result)
        {
            if (!In(EndlessRoom.GhostKing))
            {
                return true;
            }

            __result = __instance.isBossDead && !__instance.hasInteracted && __instance.CanInteract();
            if (__result)
            {
                __instance.hasInteracted = true;
                LeaveRoom();
            }

            return false;
        }
    }
}

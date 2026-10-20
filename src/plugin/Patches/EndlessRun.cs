using Assets.Scripts.Managers;
using HarmonyLib;
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
}

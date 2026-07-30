using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace MegabonkTogether.Patches.Unity
{
    /// <summary>
    /// Rate-limited counters for the transform fallbacks below.
    ///
    /// Those fallbacks catch a reference that is destroyed game-side but still held by the mod —
    /// suspected to be a NetPlayer, per the TODO on <see cref="UnityComponentPatches"/>. They are
    /// currently silent, so there is no evidence of how often they fire, or through which
    /// accessor. This records both.
    ///
    /// These sit inside patches on Component.get_transform, Transform.get_position and
    /// Transform.get_rotation — three of the hottest properties in Unity — so a plain
    /// LogWarning per hit would be a per-frame string allocation plus BepInEx disk I/O. Reports
    /// are therefore throttled, and counts are accumulated between reports so the frequency is
    /// visible rather than just the fact.
    ///
    /// Recording only happens on the exceptional branches, never on the common path.
    /// </summary>
    internal static class TransformFallbackDiagnostics
    {
        private const float REPORT_INTERVAL_SECONDS = 5f;

        private static float lastReportTime = -999f;
        private static int danglingTransformHits;
        private static int danglingPositionHits;
        private static int danglingRotationHits;
        private static int missingNetPlayerHits;

        internal static void RecordDanglingTransform() { danglingTransformHits++; MaybeReport(); }
        internal static void RecordDanglingPosition() { danglingPositionHits++; MaybeReport(); }
        internal static void RecordDanglingRotation() { danglingRotationHits++; MaybeReport(); }
        internal static void RecordMissingNetPlayer() { missingNetPlayerHits++; MaybeReport(); }

        /// <summary>
        /// Reports at most once per <see cref="REPORT_INTERVAL_SECONDS"/>, then resets the counts.
        /// Time.unscaledTime is a native call, but it only runs on a fallback hit — if that is
        /// frequent enough for the cost to matter, the counts themselves are the finding.
        /// </summary>
        private static void MaybeReport()
        {
            var now = Time.unscaledTime;
            if (now - lastReportTime < REPORT_INTERVAL_SECONDS)
            {
                return;
            }
            lastReportTime = now;

            Plugin.Log.LogWarning(
                "Transform fallbacks fired in the last ~5s — " +
                $"get_transform: {danglingTransformHits}, " +
                $"get_position: {danglingPositionHits}, " +
                $"get_rotation: {danglingRotationHits}, " +
                $"netplayer-not-found: {missingNetPlayerHits}. " +
                "Dangling hits are a destroyed reference the mod still holds (suspected NetPlayer); " +
                "falling back to the local player.");

            danglingTransformHits = 0;
            danglingPositionHits = 0;
            danglingRotationHits = 0;
            missingNetPlayerHits = 0;
        }

        /// <summary>Clears counters and the throttle so each session starts from zero.</summary>
        internal static void Reset()
        {
            lastReportTime = -999f;
            danglingTransformHits = 0;
            danglingPositionHits = 0;
            danglingRotationHits = 0;
            missingNetPlayerHits = 0;
        }
    }

    [HarmonyPatch(typeof(Component))]
    internal static class UnityComponentPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Intercept Component.transform getter to return the correct transform
        /// Needed for DragonBreath or for special attacks that target other players (Special attack always target the local player, if only they used the enemy.target rigidbody instead ¯\_(ツ)_/¯)
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("get_transform")]
        public static bool get_transform_Prefix(Component __instance, ref Transform __result)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (__instance == null) //TODO: i'm pretty sure its a netplayer dangling reference but how do i even debug this...
            {
                TransformFallbackDiagnostics.RecordDanglingTransform();
                __result = GameManager.Instance.player.transform; //Hack ¯\_(ツ)_/¯
                return false;
            }

            var pendingRequest = playerManagerService.PeakNetplayerPositionRequest();
            if (pendingRequest.HasValue && __instance.name == "Player")
            {
                var netPlayerId = pendingRequest.Value;
                var localPlayerId = playerManagerService.GetLocalPlayer().ConnectionId;
                if (netPlayerId == localPlayerId)
                {
                    return true;
                }

                var netPlayer = playerManagerService.GetNetPlayerByNetplayId(netPlayerId);
                if (netPlayer == null)
                {
                    // Was an unthrottled interpolated LogWarning on a patched get_transform —
                    // a per-frame string allocation. Same throttling as the fallbacks above.
                    TransformFallbackDiagnostics.RecordMissingNetPlayer();
                    return true;
                }
                __result = netPlayer.Model.transform;

                return false;
            }


            return true;
        }
    }

    [HarmonyPatch(typeof(Transform))]
    internal static class TransformPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Intercept Component.transform getter to return the correct transform (Work like above but for Transform component)
        /// Used by LaserBeamGun 
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("get_position")]
        public static bool get_position_Prefix(Transform __instance, ref Vector3 __result)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (__instance == null)
            {
                // Same dangling-reference fallback as UnityComponentPatches.get_transform_Prefix.
                TransformFallbackDiagnostics.RecordDanglingPosition();
                __result = GameManager.Instance.player.transform.position;
                return false;
            }


            var pendingRequest = playerManagerService.PeakNetplayerPositionRequest();
            if (pendingRequest.HasValue && __instance.name == "Hips")
            {
                var netPlayerId = pendingRequest.Value;
                var localPlayerId = playerManagerService.GetLocalPlayer().ConnectionId;
                if (netPlayerId == localPlayerId)
                {
                    return true;
                }

                var netPlayer = playerManagerService.GetNetPlayerByNetplayId(netPlayerId);
                if (netPlayer == null)
                {
                    return true;
                }
                __result = netPlayer.Model.transform.position;
                __result.y += Plugin.PLAYER_FEET_OFFSET_Y;

                return false;
            }


            return true;
        }

        /// <summary>
        /// Intercept Component.transform getter to return the correct transform (Work like above)
        /// Used by ProjectileMelee (Sword) 
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch("get_rotation")]
        public static bool get_rotation_Prefix(Transform __instance, ref Quaternion __result)
        {
            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            if (__instance == null)
            {
                // Same dangling-reference fallback as UnityComponentPatches.get_transform_Prefix.
                TransformFallbackDiagnostics.RecordDanglingRotation();
                __result = GameManager.Instance.player.transform.rotation;
                return false;
            }

            var pendingRequest = playerManagerService.PeakNetplayerPositionRequest();
            if (pendingRequest.HasValue && __instance.name == "Renderer")
            {
                var netPlayerId = pendingRequest.Value;
                var localPlayerId = playerManagerService.GetLocalPlayer().ConnectionId;

                if (netPlayerId == localPlayerId)
                {
                    return true;
                }

                var netPlayer = playerManagerService.GetNetPlayerByNetplayId(netPlayerId);
                if (netPlayer == null)
                {
                    return true;
                }

                __result = netPlayer.Model.transform.rotation;
                return false;
            }

            return true;
        }
    }
}

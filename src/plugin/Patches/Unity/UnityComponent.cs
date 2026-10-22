using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

namespace OmniBonk.Patches.Unity
{
    /// <summary>
    /// Throttled counters for the dangling-transform fallbacks below. These patches are on the
    /// hottest getters in Unity, so nothing is logged per hit; only exceptional branches record.
    /// </summary>
    internal static class TransformFallbackDiagnostics
    {
        private const float REPORT_INTERVAL_SECONDS = 5f;

        private static float lastReportTime = -999f;
        private static int danglingTransformHits;
        private static int danglingPositionHits;
        private static int danglingRotationHits;
        private static int missingNetPlayerHits;

        private static string lastCallerSample = "not sampled";
        private static bool sampledThisWindow;

        internal static void RecordDanglingTransform() { danglingTransformHits++; SampleCaller(); MaybeReport(); }
        internal static void RecordDanglingPosition() { danglingPositionHits++; SampleCaller(); MaybeReport(); }
        internal static void RecordDanglingRotation() { danglingRotationHits++; SampleCaller(); MaybeReport(); }
        internal static void RecordMissingNetPlayer() { missingNetPlayerHits++; MaybeReport(); }

        /// <summary>
        /// One stack trace per window. Native game frames are invisible under IL2CPP, so any
        /// OmniBonk frame means our code holds the dangling reference.
        /// </summary>
        private static void SampleCaller()
        {
            if (sampledThisWindow)
            {
                return;
            }
            sampledThisWindow = true;

            try
            {
                // Skip this method and the Record* wrapper that called it.
                var trace = new System.Diagnostics.StackTrace(2, true);
                var frames = trace.GetFrames();
                if (frames == null || frames.Length == 0)
                {
                    lastCallerSample = "empty stack";
                    return;
                }

                var ours = new System.Text.StringBuilder();
                var count = 0;

                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    var declaring = method?.DeclaringType;
                    if (declaring?.FullName == null
                        || !declaring.FullName.StartsWith("OmniBonk", System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Skip the patches themselves; they are always on the stack.
                    if (declaring == typeof(UnityComponentPatches)
                        || declaring == typeof(TransformPatches)
                        || declaring == typeof(TransformFallbackDiagnostics))
                    {
                        continue;
                    }

                    if (count > 0)
                    {
                        ours.Append(" <- ");
                    }

                    ours.Append(declaring.Name).Append('.').Append(method.Name);

                    var line = frame.GetFileLineNumber();
                    if (line > 0)
                    {
                        ours.Append(':').Append(line);
                    }

                    if (++count >= 4)
                    {
                        break; // four frames is plenty to name the owner
                    }
                }

                lastCallerSample = count == 0
                    ? "no OmniBonk frames (called from game code)"
                    : ours.ToString();
            }
            catch (System.Exception ex)
            {
                lastCallerSample = $"capture failed: {ex.GetType().Name}";
            }
        }

        private static string lastDestroyedTypeSample = "not sampled";
        private static bool sampledTypeThisWindow;
        private static int lastDestroyedIdentity;
        private static int distinctIdentities;
        private static bool seenIdentityAgain;

        /// <summary>
        /// Names the destroyed component. Managed <c>GetType()</c>, not the IL2CPP type: that would
        /// be a native call on a destroyed object (and does not compile against unity-libs).
        /// <para><b>UNVERIFIED:</b> whether the wrapper is the most-derived type or just "Component".</para>
        /// </summary>
        internal static void SampleDestroyedInstanceType(Component instance)
        {
            try
            {
                if (instance is null)
                {
                    if (!sampledTypeThisWindow)
                    {
                        sampledTypeThisWindow = true;
                        lastDestroyedTypeSample = "managed null (not a destroyed object)";
                    }
                    return;
                }

                // Managed identity, safe on a destroyed object (GetInstanceID is not). A repeat
                // proves one object; distinct ids do not prove many (wrappers can differ).
                var identity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance);

                if (identity == lastDestroyedIdentity)
                {
                    seenIdentityAgain = true;
                }
                else if (lastDestroyedIdentity != 0)
                {
                    distinctIdentities++;
                }

                lastDestroyedIdentity = identity;

                // Identity every hit; the string once per window.
                if (sampledTypeThisWindow)
                {
                    return;
                }
                sampledTypeThisWindow = true;

                lastDestroyedTypeSample =
                    $"{instance.GetType().FullName} #{identity}" +
                    (seenIdentityAgain ? " (repeat)" : "") +
                    $", {distinctIdentities + 1} distinct so far";
            }
            catch (System.Exception ex)
            {
                lastDestroyedTypeSample = $"type read failed: {ex.GetType().Name}";
            }
        }

        private static void MaybeReport()
        {
            var now = Time.unscaledTime;
            if (now - lastReportTime < REPORT_INTERVAL_SECONDS)
            {
                return;
            }
            lastReportTime = now;

            Plugin.Log.LogWarning(
                "Transform fallbacks fired in the last ~5s - " +
                $"get_transform: {danglingTransformHits}, " +
                $"get_position: {danglingPositionHits}, " +
                $"get_rotation: {danglingRotationHits}, " +
                $"netplayer-not-found: {missingNetPlayerHits}. " +
                $"Destroyed instance type: {lastDestroyedTypeSample}. " +
                $"Sampled caller: {lastCallerSample}. " +
                "Dangling hits are a destroyed reference the mod still holds; falling back to the " +
                "local player. The type above is the destroyed object itself - measured, unlike " +
                "the NetPlayer this line used to guess at, which the measurement disproved.");

            danglingTransformHits = 0;
            danglingPositionHits = 0;
            danglingRotationHits = 0;
            missingNetPlayerHits = 0;
            sampledThisWindow = false;
            sampledTypeThisWindow = false;
            lastCallerSample = "not sampled";
        }

        internal static void Reset()
        {
            lastReportTime = -999f;
            danglingTransformHits = 0;
            danglingPositionHits = 0;
            danglingRotationHits = 0;
            missingNetPlayerHits = 0;
            sampledThisWindow = false;
            sampledTypeThisWindow = false;
            lastCallerSample = "not sampled";
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

            if (__instance == null)
            {
                TransformFallbackDiagnostics.RecordDanglingTransform();
                TransformFallbackDiagnostics.SampleDestroyedInstanceType(__instance);
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

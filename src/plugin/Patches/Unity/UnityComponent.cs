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

        private static string lastCallerSample = "not sampled";
        private static bool sampledThisWindow;

        internal static void RecordDanglingTransform() { danglingTransformHits++; SampleCaller(); MaybeReport(); }
        internal static void RecordDanglingPosition() { danglingPositionHits++; SampleCaller(); MaybeReport(); }
        internal static void RecordDanglingRotation() { danglingRotationHits++; SampleCaller(); MaybeReport(); }
        internal static void RecordMissingNetPlayer() { missingNetPlayerHits++; MaybeReport(); }

        /// <summary>
        /// Captures **one** managed stack trace per report window to identify who is holding the
        /// dangling reference. A 3-player session showed ~144 of these per second the moment a peer
        /// disconnects, so per-hit capture is out of the question; once per 5s is free.
        ///
        /// <para>The discriminator this gives is exactly the one that matters: under IL2CPP, native
        /// game frames do not appear as managed frames, so <b>if any MegabonkTogether frame shows
        /// up, our own code is dereferencing the destroyed object.</b> An empty result means the
        /// access came from game code and the stale reference was handed to it — a different fix.</para>
        ///
        /// <para>Symbols ship next to the DLL (the csproj xcopies the whole output), so this
        /// resolves file and line, as the P1-7 stack trace in an earlier log did.</para>
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
                        || !declaring.FullName.StartsWith("MegabonkTogether", System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // The patch itself is always on the stack; it is not the caller we want.
                    if (declaring == typeof(UnityComponentPatches) || declaring == typeof(TransformFallbackDiagnostics))
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
                    ? "no MegabonkTogether frames (called from game code)"
                    : ours.ToString();
            }
            catch (System.Exception ex)
            {
                // A diagnostic must never be able to break what it measures.
                lastCallerSample = $"capture failed: {ex.GetType().Name}";
            }
        }

        private static string lastDestroyedTypeSample = "not sampled";
        private static bool sampledTypeThisWindow;

        /// <summary>
        /// Names the destroyed component itself, which the caller sample cannot.
        ///
        /// <para><b>Why this was added.</b> Run A's repeat showed the enemy-targeting fix moving 14
        /// enemies off the departed player while the fallback rate stayed at ~710 per 5s — so enemy
        /// targeting was a holder but not the dominant one, and the stack sampler could only say
        /// "no MegabonkTogether frames", i.e. game code. Which game object is being read was the
        /// missing fact, and guessing at candidates (camera, minimap, follow targets) is exactly the
        /// speculation this project's rules exist to prevent.</para>
        ///
        /// <para>Uses managed <c>GetType()</c> on the wrapper rather than the IL2CPP type. That is
        /// deliberate twice over: <c>GetIl2CppType()</c> does not compile here at all — the csproj
        /// takes UnityEngine.CoreModule from `unity-libs`, whose Component does not derive from
        /// Il2CppObjectBase, the same trap that broke <c>StageData.Pointer</c> in 7be19bc — and it
        /// would be a native call on a destroyed object, which is precisely the hazard being
        /// measured. The managed wrapper survives destruction untouched.</para>
        ///
        /// <para><b>UNVERIFIED:</b> how specific the answer will be. Il2CppInterop usually hands
        /// back the most-derived proxy it knows, in which case this names the component outright;
        /// if Harmony instead passes a wrapper typed as the patched parameter, it degrades to
        /// "Component" and tells us nothing. Worth one run to find out, since the alternative is
        /// guessing at candidates.</para>
        /// </summary>
        internal static void SampleDestroyedInstanceType(Component instance)
        {
            if (sampledTypeThisWindow)
            {
                return;
            }
            sampledTypeThisWindow = true;

            try
            {
                lastDestroyedTypeSample = instance is null
                    ? "managed null (not a destroyed object)"
                    : instance.GetType().FullName;
            }
            catch (System.Exception ex)
            {
                lastDestroyedTypeSample = $"type read failed: {ex.GetType().Name}";
            }
        }

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
                $"Destroyed instance type: {lastDestroyedTypeSample}. " +
                $"Sampled caller: {lastCallerSample}. " +
                "Dangling hits are a destroyed reference the mod still holds (suspected: a " +
                "disconnected peer's NetPlayer); falling back to the local player.");

            danglingTransformHits = 0;
            danglingPositionHits = 0;
            danglingRotationHits = 0;
            missingNetPlayerHits = 0;
            sampledThisWindow = false;
            sampledTypeThisWindow = false;
            lastCallerSample = "not sampled";
        }

        /// <summary>Clears counters and the throttle so each session starts from zero.</summary>
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

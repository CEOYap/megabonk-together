using System;
using System.Runtime.InteropServices;

namespace OmniBonk.Services
{
    /// <summary>
    /// Direct P/Invoke for <c>GetConnectionRealTimeStatus</c>: its interop struct carries an
    /// <c>Il2CppStructArray</c> where native is inline and crashes (docs/steamworks/05-interop-struct-shapes.md).
    /// Safe because <c>ISteamNetworkingSockets</c> has no callbacks, so no second dispatcher is created,
    /// and the struct below is blittable and never crosses Il2CppInterop.
    /// <para>Connection config goes the same way: <c>SetConnectionConfigValueInt32</c> is not in the
    /// Steamworks.NET interop, and <c>ISteamNetworkingUtils</c> has no callbacks either.</para>
    /// </summary>
    internal static class SteamNetNative
    {
        /// <summary>Base name resolves to the module the game already loaded.</summary>
        private const string SteamApi = "steam_api64.dll";

        /// <summary>Must match the game's <c>SteamNetworkingSockets012</c>; a wrong version returns null.</summary>
        [DllImport(SteamApi, EntryPoint = "SteamAPI_SteamNetworkingSockets_SteamAPI_v012", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetSocketsInterface();

        /// <summary>The only <c>SteamNetworkingUtils</c> accessor the game's steam_api64.dll exports.</summary>
        [DllImport(SteamApi, EntryPoint = "SteamAPI_SteamNetworkingUtils_SteamAPI_v004", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetUtilsInterface();

        [DllImport(SteamApi, EntryPoint = "SteamAPI_ISteamNetworkingUtils_SetConnectionConfigValueInt32", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SetConnectionConfigValueInt32(IntPtr self, uint hConn, int eValue, int value);

        [DllImport(SteamApi, EntryPoint = "SteamAPI_ISteamNetworkingUtils_GetConfigValue", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetConfigValue(
            IntPtr self,
            int eValue,
            int eScopeType,
            IntPtr scopeObj,
            ref int pOutDataType,
            ref int pResult,
            ref UIntPtr cbResult);

        // ESteamNetworkingConfigValue / Scope / DataType / GetConfigValueResult, read from the
        // Steamworks.NET interop metadata (enum constants are real values, not stubs).
        internal const int ConfigSendBufferSize = 9;
        internal const int ConfigSendRateMin = 10;
        internal const int ConfigSendRateMax = 11;
        internal const int ScopeGlobal = 1;
        internal const int ScopeConnection = 4;
        private const int DataTypeInt32 = 1;
        private const int GetConfigOk = 1;
        private const int GetConfigOkInherited = 2;

        [DllImport(SteamApi, EntryPoint = "SteamAPI_ISteamNetworkingSockets_GetConnectionRealTimeStatus", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetConnectionRealTimeStatus(
            IntPtr self,
            uint hConn,
            ref SteamNetConnectionRealTimeStatusNative pStatus,
            int nLanes,
            IntPtr pLanes);

        /// <summary>Matches the SDK layout; reserved tail is inline so it stays blittable.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct SteamNetConnectionRealTimeStatusNative
        {
            public int State;
            public int Ping;
            public float ConnectionQualityLocal;
            public float ConnectionQualityRemote;
            public float OutPacketsPerSec;
            public float OutBytesPerSec;
            public float InPacketsPerSec;
            public float InBytesPerSec;
            public int SendRateBytesPerSecond;
            public int PendingUnreliable;
            public int PendingReliable;
            public int SentUnackedReliable;
            public long QueueTime;
            public fixed uint Reserved[16];
        }

        /// <summary>
        /// Link diagnostics for loss testing. Quality confirms the loss rig is active; climbing
        /// pending reliable bytes and queue time show head-of-line blocking.
        /// </summary>
        public readonly struct SteamLinkStatus(
            int pingMs,
            float qualityLocal,
            float qualityRemote,
            int pendingReliableBytes,
            int pendingUnreliableBytes,
            int sentUnackedReliableBytes,
            long queueTimeMicroseconds,
            float outPacketsPerSec,
            float outBytesPerSec,
            int sendRateBytesPerSecond)
        {
            public int PingMs { get; } = pingMs;

            /// <summary>Delivered fraction measured here; 1.0 is clean.</summary>
            public float QualityLocal { get; } = qualityLocal;

            public float QualityRemote { get; } = qualityRemote;

            public int PendingReliableBytes { get; } = pendingReliableBytes;

            public int PendingUnreliableBytes { get; } = pendingUnreliableBytes;

            public int SentUnackedReliableBytes { get; } = sentUnackedReliableBytes;

            public float QueueTimeMs { get; } = queueTimeMicroseconds / 1000f;

            /// <summary>Wire-level, headers included, unlike the <c>[bw]</c> payload counters.</summary>
            public float OutPacketsPerSec { get; } = outPacketsPerSec;

            public float OutBytesPerSec { get; } = outBytesPerSec;

            /// <summary>The rate Steam is pacing this connection to right now.</summary>
            public int SendRateBytesPerSecond { get; } = sendRateBytesPerSecond;
        }

        /// <summary>Latched off after the first failure (per-frame path).</summary>
        private static bool unavailable;

        /// <summary>Separate latch: a failed config call must not silence rtt, or vice versa.</summary>
        private static bool configUnavailable;

        /// <summary>
        /// Sets an int32 connection config value. Never throws; false if Steam refused it or the
        /// export is unusable (then latched, logged once).
        /// </summary>
        public static bool TrySetConnectionConfigInt32(uint connection, int configValue, int value)
        {
            if (configUnavailable || connection == 0U)
            {
                return false;
            }

            try
            {
                var utils = GetUtilsInterface();
                if (utils == IntPtr.Zero)
                {
                    DisableConfig("the networking utils interface was null");
                    return false;
                }

                return SetConnectionConfigValueInt32(utils, connection, configValue, value);
            }
            catch (Exception ex)
            {
                DisableConfig($"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads an int32 config value at <paramref name="scope"/>. With <see cref="ScopeGlobal"/> and
        /// nothing set by us, that is Steam's own default. Never throws.
        /// </summary>
        public static bool TryGetConfigInt32(int configValue, int scope, uint scopeObject, out int value)
        {
            value = 0;

            if (configUnavailable)
            {
                return false;
            }

            try
            {
                var utils = GetUtilsInterface();
                if (utils == IntPtr.Zero)
                {
                    DisableConfig("the networking utils interface was null");
                    return false;
                }

                var dataType = DataTypeInt32;
                var size = (UIntPtr)sizeof(int);
                var result = GetConfigValue(utils, configValue, scope, (IntPtr)scopeObject, ref dataType, ref value, ref size);

                return (result == GetConfigOk || result == GetConfigOkInherited) && dataType == DataTypeInt32;
            }
            catch (Exception ex)
            {
                DisableConfig($"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void DisableConfig(string reason)
        {
            configUnavailable = true;
            Plugin.Log.LogWarning(
                $"[steam-net] Connection config is unavailable ({reason}), so the send rate stays at "
                + "Steam's default. The transport itself is unaffected.");
        }

        public static int TryGetPing(uint connection) =>
            TryGetLinkStatus(connection, out var status) ? status.PingMs : -1;

        /// <summary>Never throws. False is normal for a just-closed connection.</summary>
        public static bool TryGetLinkStatus(uint connection, out SteamLinkStatus status)
        {
            status = default;

            if (unavailable || connection == 0U)
            {
                return false;
            }

            try
            {
                var sockets = GetSocketsInterface();
                if (sockets == IntPtr.Zero)
                {
                    Disable("the sockets interface was null");
                    return false;
                }

                var native = default(SteamNetConnectionRealTimeStatusNative);

                var result = GetConnectionRealTimeStatus(sockets, connection, ref native, 0, IntPtr.Zero);

                // k_EResultOK.
                if (result != 1)
                {
                    return false;
                }

                status = new SteamLinkStatus(
                    native.Ping,
                    native.ConnectionQualityLocal,
                    native.ConnectionQualityRemote,
                    native.PendingReliable,
                    native.PendingUnreliable,
                    native.SentUnackedReliable,
                    native.QueueTime,
                    native.OutPacketsPerSec,
                    native.OutBytesPerSec,
                    native.SendRateBytesPerSecond);

                return true;
            }
            catch (Exception ex)
            {
                Disable($"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static void Disable(string reason)
        {
            unavailable = true;
            Plugin.Log.LogWarning(
                $"[steam-net] Connection status is unavailable ({reason}), so rtt and link quality "
                + "will not be reported. Everything else is unaffected - this is the diagnostic "
                + "read, not the transport.");
        }
    }
}

using System;
using System.Runtime.InteropServices;

namespace OmniBonk.Services
{
    /// <summary>
    /// Direct P/Invoke for <c>GetConnectionRealTimeStatus</c>: its interop struct carries an
    /// <c>Il2CppStructArray</c> where native is inline and crashes (docs/steamworks/05-interop-struct-shapes.md).
    /// Safe because <c>ISteamNetworkingSockets</c> has no callbacks, so no second dispatcher is created,
    /// and the struct below is blittable and never crosses Il2CppInterop.
    /// </summary>
    internal static class SteamNetNative
    {
        /// <summary>Base name resolves to the module the game already loaded.</summary>
        private const string SteamApi = "steam_api64.dll";

        /// <summary>Must match the game's <c>SteamNetworkingSockets012</c>; a wrong version returns null.</summary>
        [DllImport(SteamApi, EntryPoint = "SteamAPI_SteamNetworkingSockets_SteamAPI_v012", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetSocketsInterface();

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
            long queueTimeMicroseconds)
        {
            public int PingMs { get; } = pingMs;

            /// <summary>Delivered fraction measured here; 1.0 is clean.</summary>
            public float QualityLocal { get; } = qualityLocal;

            public float QualityRemote { get; } = qualityRemote;

            public int PendingReliableBytes { get; } = pendingReliableBytes;

            public int PendingUnreliableBytes { get; } = pendingUnreliableBytes;

            public int SentUnackedReliableBytes { get; } = sentUnackedReliableBytes;

            public float QueueTimeMs { get; } = queueTimeMicroseconds / 1000f;
        }

        /// <summary>Latched off after the first failure (per-frame path).</summary>
        private static bool unavailable;

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
                    native.QueueTime);

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

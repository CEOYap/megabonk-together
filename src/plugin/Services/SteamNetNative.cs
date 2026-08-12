using System;
using System.Runtime.InteropServices;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// The one Steamworks call this mod makes without going through the game's interop assembly.
    ///
    /// <para><b>Why P/Invoke here and nowhere else.</b> <c>GetConnectionRealTimeStatus</c> is the
    /// only source of ping, and its interop signature takes
    /// <c>SteamNetConnectionRealTimeStatus_t</c> by reference — a struct whose IL2CPP form carries
    /// an <c>Il2CppStructArray</c> where native has 64 inline bytes. Passing it is the exact shape
    /// that has crashed this project twice, so the transport shipped with
    /// <c>GetLatency</c> returning -1 and the lobby panel's rtt blank. See
    /// <c>docs/steamworks/05-interop-struct-shapes.md</c>.</para>
    ///
    /// <para><b>This does not create a second callback registry</b>, which is the thing that makes
    /// it safe. <c>ISteamNetworkingSockets</c> has no callbacks of its own — connection status
    /// arrives on the shared pipe the game already pumps — so calling one of its methods directly
    /// adds no dispatcher and consumes nobody's results. That is the composition
    /// <see href="../../docs/steamworks/00-migration-plan.md">Gotcha 1 option (c)</see> describes.</para>
    ///
    /// <para><b>The struct below is ours, declared blittable, and never crosses Il2CppInterop.</b>
    /// That is the whole reason this is safe where the interop path is not: the layout is written
    /// out here to match the SDK exactly, including the trailing reserved array as
    /// <c>fixed uint[16]</c> rather than a reference.</para>
    /// </summary>
    internal static class SteamNetNative
    {
        /// <summary>
        /// Resolved by base name, which finds the module the game has already loaded rather than
        /// loading a second one — Windows returns the existing handle for a matching base name, and
        /// Steam is up long before anything here is called.
        /// </summary>
        private const string SteamApi = "steam_api64.dll";

        /// <summary>
        /// The accessor version must match the interface the game's own <c>steam_api64.dll</c>
        /// exposes. That was read out of the binary as <c>SteamNetworkingSockets012</c>; a wrong
        /// version here returns null rather than misbehaving, which <see cref="TryGetPing"/> treats
        /// as "no ping available".
        /// </summary>
        [DllImport(SteamApi, EntryPoint = "SteamAPI_SteamNetworkingSockets_SteamAPI_v012", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetSocketsInterface();

        [DllImport(SteamApi, EntryPoint = "SteamAPI_ISteamNetworkingSockets_GetConnectionRealTimeStatus", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetConnectionRealTimeStatus(
            IntPtr self,
            uint hConn,
            ref SteamNetConnectionRealTimeStatusNative pStatus,
            int nLanes,
            IntPtr pLanes);

        /// <summary>
        /// Our own declaration of <c>SteamNetConnectionRealTimeStatus_t</c>, laid out to match the
        /// SDK. Blittable by construction — the reserved tail is inline, not a reference.
        /// </summary>
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
        /// Latched off after a failure. A missing export or a null interface will not start working
        /// later, and this is called from a per-frame diagnostic path.
        /// </summary>
        private static bool unavailable;

        /// <summary>
        /// Round-trip time in milliseconds for a connection, or -1 when it cannot be read.
        ///
        /// <para>Never throws: this is a diagnostic, and a diagnostic that can take the frame down
        /// is worse than no diagnostic. The first failure disables it permanently.</para>
        /// </summary>
        public static int TryGetPing(uint connection)
        {
            if (unavailable || connection == 0U)
            {
                return -1;
            }

            try
            {
                var sockets = GetSocketsInterface();
                if (sockets == IntPtr.Zero)
                {
                    Disable("the sockets interface was null");
                    return -1;
                }

                var status = default(SteamNetConnectionRealTimeStatusNative);

                // nLanes 0 and a null lane pointer: we do not use connection lanes, and asking for
                // lane status is what would need a second struct.
                var result = GetConnectionRealTimeStatus(sockets, connection, ref status, 0, IntPtr.Zero);

                // k_EResultOK. Anything else — most often a connection that has just closed — is
                // not worth a log on a path that runs per peer per sample.
                return result == 1 ? status.Ping : -1;
            }
            catch (Exception ex)
            {
                Disable($"{ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        private static void Disable(string reason)
        {
            unavailable = true;
            Plugin.Log.LogWarning(
                $"[steam-net] Round-trip time is unavailable ({reason}). Everything else is "
                + "unaffected — this is the diagnostic read, not the transport.");
        }
    }
}

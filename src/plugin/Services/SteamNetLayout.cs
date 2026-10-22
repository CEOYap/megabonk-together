using Steamworks;
using System;
using System.Runtime.InteropServices;

namespace OmniBonk.Services
{
    /// <summary>A connection-status callback, read out of the native payload.</summary>
    internal struct SteamConnectionStatusChange
    {
        /// <summary>The connection this is about. Also the transport's peer handle.</summary>
        public uint Connection;

        public ulong RemoteSteamId;

        public ESteamNetworkingConnectionState State;

        /// <summary>Zero while healthy; otherwise a <c>k_ESteamNetworkingConnectionEnd_*</c> value.</summary>
        public int EndReason;
    }

    /// <summary>
    /// Steamworks marshalling the interop assembly cannot do safely. See
    /// <c>docs/steamworks/05-interop-struct-shapes.md</c>.
    /// <para>A struct with an <c>Il2CppStructArray</c> field has a different IL2CPP layout from native,
    /// and Steam writes past the allocation. <c>SteamNetworkingIdentity</c> is all-inline and safe;
    /// <c>SteamNetConnectionInfo_t</c> is not, so the callback is read with <see cref="Marshal"/> at
    /// SDK offsets (<b>not</b> <c>dump.cs</c>, which has the broken IL2CPP layout).</para>
    /// </summary>
    internal static class SteamNetLayout
    {
        // m_info aligns to 8 (it holds an int64), hence 4 bytes of padding after m_hConn.
        private const int ConnectionOffset = 0;
        private const int InfoOffset = 8;

        // Within SteamNetConnectionInfo_t; the SDK's m__pad1 at +166 corroborates the later offsets.
        private const int IdentityTypeOffset = InfoOffset + 0;
        private const int IdentitySizeOffset = InfoOffset + 4;
        private const int IdentitySteamIdOffset = InfoOffset + 8;
        private const int StateOffset = InfoOffset + 176;
        private const int EndReasonOffset = InfoOffset + 180;

        /// <summary><c>k_ESteamNetworkingIdentityType_SteamID</c>, as a literal sentinel for raw reads.</summary>
        private const int IdentityTypeSteamId = 16;

        /// <summary>Bytes of a SteamID inside the identity union.</summary>
        private const int IdentitySteamIdSize = 8;

        private static bool loggedBadPayload;

        /// <summary>
        /// Builds an identity for a SteamID by writing fields directly, then verifies it with a
        /// <c>GetSteamID64</c> readback, so a layout fault fails here rather than inside <c>ConnectP2P</c>.
        /// </summary>
        public static bool TryMakeSteamIdentity(ulong steamId, out SteamNetworkingIdentity identity)
        {
            identity = new SteamNetworkingIdentity
            {
                m_eType = ESteamNetworkingIdentityType.k_ESteamNetworkingIdentityType_SteamID,
                m_cbSize = IdentitySteamIdSize,

                // m_reserved0/1 are the SteamID's low/high words (little-endian).
                m_reserved0 = (uint)(steamId & 0xFFFFFFFFUL),
                m_reserved1 = (uint)(steamId >> 32),
            };

            var readBack = identity.GetSteamID64();
            if (readBack == steamId)
            {
                return true;
            }

            Plugin.Log.LogError(
                $"[steam-net] SteamNetworkingIdentity did not round-trip: wrote {steamId}, read "
                + $"{readBack}. The struct's layout is not what docs/steamworks/"
                + "05-interop-struct-shapes.md assumes, so no connection will be attempted.");
            return false;
        }

        /// <summary>
        /// Offline layout proof: Steam fills in our identity, checked against the game's SteamID.
        /// False also when Steam declines, which is logged as distinct from a layout fault.
        /// </summary>
        public static bool ProbeIdentityLayout(ulong expectedSteamId)
        {
            SteamNetworkingIdentity identity;
            try
            {
                if (!SteamNetworkingSockets.GetIdentity(out identity))
                {
                    Plugin.Log.LogWarning(
                        "[steam-net] GetIdentity declined, so the identity layout is unproven. "
                        + "This is Steam saying no, not a layout fault.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[steam-net] GetIdentity threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            var reported = identity.GetSteamID64();
            if (reported == expectedSteamId)
            {
                Plugin.Log.LogInfo(
                    $"[steam-net] Identity layout verified: Steam filled in {reported}, which "
                    + "matches the game's own SteamID.");
                return true;
            }

            Plugin.Log.LogError(
                $"[steam-net] Identity layout is wrong: Steam filled in {reported}, the game says "
                + $"{expectedSteamId}. Every ConnectP2P would be pointed at a stranger. See "
                + "docs/steamworks/05-interop-struct-shapes.md.");
            return false;
        }

        /// <summary>
        /// Reads a <c>SteamNetConnectionStatusChangedCallback_t</c> payload. Validated via the identity
        /// type and size; a bad payload is dropped (logged once) rather than acted on.
        /// </summary>
        public static bool TryReadStatusChange(IntPtr payload, out SteamConnectionStatusChange change)
        {
            change = default;

            if (payload == IntPtr.Zero)
            {
                return false;
            }

            var identityType = Marshal.ReadInt32(payload, IdentityTypeOffset);
            var identitySize = Marshal.ReadInt32(payload, IdentitySizeOffset);

            if (identityType != IdentityTypeSteamId || identitySize != IdentitySteamIdSize)
            {
                if (!loggedBadPayload)
                {
                    loggedBadPayload = true;
                    Plugin.Log.LogError(
                        $"[steam-net] A connection-status payload did not look like one: identity "
                        + $"type {identityType} (expected {IdentityTypeSteamId}), size "
                        + $"{identitySize} (expected {IdentitySteamIdSize}). Either the offsets in "
                        + "docs/steamworks/05-interop-struct-shapes.md are wrong for this build, or "
                        + "a peer connected with a non-SteamID identity. Dropping it, and staying "
                        + "quiet about any that follow.");
                }

                return false;
            }

            change = new SteamConnectionStatusChange
            {
                Connection = (uint)Marshal.ReadInt32(payload, ConnectionOffset),
                RemoteSteamId = (ulong)Marshal.ReadInt64(payload, IdentitySteamIdOffset),
                State = (ESteamNetworkingConnectionState)Marshal.ReadInt32(payload, StateOffset),
                EndReason = Marshal.ReadInt32(payload, EndReasonOffset),
            };

            return true;
        }

        /// <summary>Player-facing end reason. 1000–1999 is the app band we assign.</summary>
        public static string DescribeEndReason(int endReason) => endReason switch
        {
            0 => "no reason given",
            SteamNetEndReason.ProtocolMismatch => "the other player is on an incompatible mod version",
            SteamNetEndReason.NotInLobby => "the connection did not come from someone in the lobby",
            SteamNetEndReason.Kicked => "removed by the host",
            SteamNetEndReason.SessionClosed => "the session ended",
            >= 1000 and <= 1999 => $"closed by the mod ({endReason})",
            >= 2000 and <= 2999 => $"the other side hit an error ({endReason})",
            >= 3000 and <= 3999 => "a local network problem - no route to Steam's relays",
            >= 4000 and <= 4999 => "the other player stopped responding",
            _ => $"Steam infrastructure ({endReason})",
        };
    }

    /// <summary>
    /// Our close reasons in Steam's app band (1000+); they reach the peer intact.
    /// <para>Append only: shipped numbers are interpreted by older builds.</para>
    /// </summary>
    internal static class SteamNetEndReason
    {
        public const int ProtocolMismatch = 1001;
        public const int NotInLobby = 1002;
        public const int Kicked = 1003;
        public const int SessionClosed = 1004;
    }
}

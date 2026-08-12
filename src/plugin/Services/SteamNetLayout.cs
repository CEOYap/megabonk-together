using Steamworks;
using System;
using System.Runtime.InteropServices;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// What a connection-status callback said, read out of the native payload.
    /// </summary>
    internal struct SteamConnectionStatusChange
    {
        /// <summary>The connection this is about. Also the transport's peer handle.</summary>
        public uint Connection;

        public ulong RemoteSteamId;

        public ESteamNetworkingConnectionState State;

        /// <summary>
        /// Steam's structured end reason. Zero while the connection is healthy; the
        /// <c>k_ESteamNetworkingConnectionEnd_*</c> ranges otherwise.
        /// </summary>
        public int EndReason;
    }

    /// <summary>
    /// The two pieces of Steamworks marshalling Phase 4 could not take from the interop assembly
    /// directly: constructing a <c>SteamNetworkingIdentity</c>, and reading a
    /// <c>SteamNetConnectionStatusChangedCallback_t</c> payload.
    ///
    /// <para>Which Steamworks structs may cross the boundary by reference, and why this one may
    /// while the status structs may not, is audited in
    /// <c>docs/steamworks/05-interop-struct-shapes.md</c>. The short version: the rule that
    /// matters is not "no struct by reference", it is "no struct whose IL2CPP layout differs from
    /// its native layout" — which means any struct with an <c>Il2CppStructArray</c> field, because
    /// Il2CppInterop puts a pointer where native has inline bytes and Steam then writes past the
    /// end of the allocation. <c>SteamNetworkingIdentity</c> has no such field: its union was
    /// unrolled into thirty-two separate <c>uint</c>s, so all 136 bytes are inline and the layouts
    /// match. <c>SteamNetConnectionInfo_t</c> has three such fields and is unusable.</para>
    ///
    /// <para><b>So the callback payload is read with <see cref="Marshal"/> at fixed offsets</b>,
    /// exactly as <see cref="SteamService"/> already reads <c>SteamRelayNetworkStatus_t</c>. The
    /// offsets come from the SDK's own layout and <b>not</b> from <c>dump.cs</c>, which records the
    /// IL2CPP layout — the broken one for this struct.</para>
    /// </summary>
    internal static class SteamNetLayout
    {
        // SteamNetConnectionStatusChangedCallback_t. m_info is a SteamNetConnectionInfo_t, which
        // contains an int64 and so aligns to 8 — hence the four bytes of padding after m_hConn.
        private const int ConnectionOffset = 0;
        private const int InfoOffset = 8;

        // Within SteamNetConnectionInfo_t. m_identityRemote leads at +0 and is 136 bytes; the SDK's
        // own m__pad1 at +166 is what closes SteamNetworkingIPAddr's 18 bytes up to +168, and is
        // the corroboration that everything after it lands where this says it does.
        private const int IdentityTypeOffset = InfoOffset + 0;
        private const int IdentitySizeOffset = InfoOffset + 4;
        private const int IdentitySteamIdOffset = InfoOffset + 8;
        private const int StateOffset = InfoOffset + 176;
        private const int EndReasonOffset = InfoOffset + 180;

        /// <summary>
        /// <c>k_ESteamNetworkingIdentityType_SteamID</c>. Written as a literal because it is used
        /// as a sentinel against a raw read, and reading it back through the enum would only prove
        /// the enum agrees with itself.
        /// </summary>
        private const int IdentityTypeSteamId = 16;

        /// <summary>Bytes of a SteamID inside the identity union.</summary>
        private const int IdentitySteamIdSize = 8;

        private static bool loggedBadPayload;

        /// <summary>
        /// Builds an identity for a SteamID, and proves it did so before handing it to Steam.
        ///
        /// <para>The fields are written directly rather than through <c>SetSteamID64</c>, and then
        /// <c>GetSteamID64</c> is asked what it sees. That single check covers both things that
        /// could be wrong at once: if the struct's field layout is not what this assumes, or if the
        /// generated accessor does not operate on the struct we hold, the readback disagrees and
        /// the caller finds out here rather than inside <c>ConnectP2P</c>.</para>
        /// </summary>
        public static bool TryMakeSteamIdentity(ulong steamId, out SteamNetworkingIdentity identity)
        {
            identity = new SteamNetworkingIdentity
            {
                m_eType = ESteamNetworkingIdentityType.k_ESteamNetworkingIdentityType_SteamID,
                m_cbSize = IdentitySteamIdSize,

                // The union begins immediately after m_cbSize, so m_reserved0/1 are the SteamID's
                // low and high words. Little-endian, which x86-64 is.
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
        /// Asks Steam to fill in our own identity and checks it against the SteamID the game
        /// already knows.
        ///
        /// <para><b>The cheapest possible proof that the layout is right</b>, and worth running
        /// before the first connection rather than after: it involves no peer, no socket and no
        /// network, so a wrong layout shows up as a mismatched number instead of as a connection
        /// that mysteriously never establishes. Returns false if Steam declined to answer, which is
        /// not the same as a layout fault and is logged differently.</para>
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
        /// Reads a <c>SteamNetConnectionStatusChangedCallback_t</c> out of the raw payload the
        /// game's dispatcher delivered.
        ///
        /// <para><b>Validated, not trusted.</b> The identity's type and size sit in front of every
        /// other offset this reads, so if the base is wrong they will not hold. A payload that
        /// fails is dropped and logged once — a connection that never establishes is a bug report,
        /// while a connection acted on from garbage is a crash somewhere unrelated.</para>
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

        /// <summary>
        /// Turns Steam's structured end reason into something a player can act on. The ranges are
        /// Steam's; the 1000–1999 band is ours to assign and is where a protocol rejection lands.
        /// </summary>
        public static string DescribeEndReason(int endReason) => endReason switch
        {
            0 => "no reason given",
            SteamNetEndReason.ProtocolMismatch => "the other player is on an incompatible mod version",
            SteamNetEndReason.NotInLobby => "the connection did not come from someone in the lobby",
            SteamNetEndReason.Kicked => "removed by the host",
            SteamNetEndReason.SessionClosed => "the session ended",
            >= 1000 and <= 1999 => $"closed by the mod ({endReason})",
            >= 2000 and <= 2999 => $"the other side hit an error ({endReason})",
            >= 3000 and <= 3999 => "a local network problem — no route to Steam's relays",
            >= 4000 and <= 4999 => "the other player stopped responding",
            _ => $"Steam infrastructure ({endReason})",
        };
    }

    /// <summary>
    /// Our own connection-close reasons, in the application band Steam reserves for exactly this
    /// (<c>k_ESteamNetworkingConnectionEnd_App_Min</c> = 1000). They reach the other end intact, so
    /// a rejected peer can be told why instead of being shown "host has disconnected".
    ///
    /// <para>Append only. A number that has shipped is a number a peer on an older build will
    /// interpret its own way.</para>
    /// </summary>
    internal static class SteamNetEndReason
    {
        public const int ProtocolMismatch = 1001;
        public const int NotInLobby = 1002;
        public const int Kicked = 1003;
        public const int SessionClosed = 1004;
    }
}

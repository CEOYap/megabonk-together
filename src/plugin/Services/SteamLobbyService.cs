using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Steam lobbies, polled rather than called back.
    ///
    /// <para><b>Why no <c>CallResult&lt;LobbyCreated_t&gt;</c>.</b> IL2CPP is ahead-of-time
    /// compiled, so a generic exists only for the type arguments the game itself used.
    /// <c>CallResult&lt;T&gt;</c> is compiled for the three leaderboard types and
    /// <c>Callback&lt;T&gt;</c> for three overlay/persona/stats types; no lobby type has a concrete
    /// instantiation. So the asynchronous result is retrieved with
    /// <c>SteamUtils.IsAPICallCompleted</c> and <c>GetAPICallResult</c>, which are not generic and
    /// take an <c>IntPtr</c> — meaning the result lands in a buffer <b>we</b> allocate and read at
    /// offsets taken from <c>dump.cs</c>. No <c>ValueType</c>-derived proxy is involved, which is
    /// the specific thing that crashed Phase 2.</para>
    ///
    /// <para><b>UNVERIFIED, and this is the reason the self-test exists.</b> The game pumps
    /// <c>CallbackDispatcher.RunFrame</c> every frame, and that drains the process's single
    /// manual-dispatch pipe — including the <c>SteamAPICallCompleted_t</c> raised for <i>our</i>
    /// call — then calls <c>FreeLastCallback</c>. If that release also invalidates the result for
    /// <c>GetAPICallResult</c>, we will never observe our own call completing, and no amount of
    /// polling will help. Nobody has measured which way it goes. <c>SteamLobbySelfTest</c> is built
    /// to answer exactly that and to say which failure it saw.</para>
    /// </summary>
    internal class SteamLobbyService : ISteamLobbyService
    {
        #region Callback struct layouts

        // From megabonk-re/build-21750826/dump.cs. Steam packs its callback structs to 8 on x64,
        // and these sizes are the packed sizes, not the sum of the fields.
        //
        //   LobbyCreated_t   k_iCallback 513   m_eResult @0x0 (int), m_ulSteamIDLobby @0x8 (ulong)
        //
        // Read with Marshal at those offsets. Getting a size wrong is not a compile error and not
        // an exception — GetAPICallResult simply refuses, or fills the wrong number of bytes.
        private const int LobbyCreatedCallbackId = 513;
        private const int LobbyCreatedSize = 16;
        private const int LobbyCreatedResultOffset = 0;
        private const int LobbyCreatedLobbyIdOffset = 8;

        #endregion

        /// <summary>
        /// How long to wait for an in-flight call before giving up on it.
        ///
        /// <para>Generous on purpose. A Steam call normally resolves in well under a second, so a
        /// timeout here is not slowness — it is the "the game's pump consumed our result" case,
        /// and the log line that reports it is the finding.</para>
        /// </summary>
        private const float PendingCallTimeoutSeconds = 15f;

        private readonly ISteamService steamService;

        private readonly List<ulong> members = [];

        private SteamAPICall_t pendingCall;
        private bool hasPendingCall;
        private float pendingCallElapsedSeconds;
        private float lastPollTime;

        private string lastError = "";

        public SteamLobbyState State { get; private set; } = SteamLobbyState.None;

        public ulong LobbyId { get; private set; }

        public bool IsOwner { get; private set; }

        public SteamLobbyService(ISteamService steamService)
        {
            this.steamService = steamService;
        }

        public void CreateLobby(int maxMembers)
        {
            if (!steamService.IsAvailable)
            {
                Fail("Steam is not available, so no lobby can be created.");
                return;
            }

            if (hasPendingCall || State == SteamLobbyState.InLobby)
            {
                Fail($"Refusing to create a lobby while {State}.");
                return;
            }

            try
            {
                // Private. This is a real lobby on Valve's infrastructure, not a local object, and
                // a friends-only or public one would advertise the mod's test runs to a friends
                // list. Nothing about the migration needs visibility yet.
                pendingCall = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePrivate, maxMembers);
                hasPendingCall = true;

                // Both, not just the elapsed count. lastPollTime survives from any previous call,
                // and leaving it set would make this call's first poll add every second since that
                // one — timing the new call out immediately.
                pendingCallElapsedSeconds = 0f;
                lastPollTime = 0f;

                State = SteamLobbyState.Pending;
                lastError = "";

                Plugin.Log.LogInfo($"[steam-lobby] CreateLobby requested, max {maxMembers}.");
            }
            catch (Exception ex)
            {
                Fail($"CreateLobby threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void LeaveLobby()
        {
            // Dropped whether or not there is a lobby yet: leaving while a create is still in
            // flight would otherwise leave the call pending, and its result would arrive later and
            // put us back InLobby with a lobby nobody asked for.
            ClearPending();

            if (LobbyId == 0UL)
            {
                State = SteamLobbyState.None;
                return;
            }

            try
            {
                SteamMatchmaking.LeaveLobby(new CSteamID(LobbyId));
                Plugin.Log.LogInfo($"[steam-lobby] Left lobby {LobbyId}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-lobby] LeaveLobby threw: {ex.GetType().Name}: {ex.Message}");
            }

            LobbyId = 0UL;
            IsOwner = false;
            members.Clear();
            State = SteamLobbyState.None;
        }

        public void Poll()
        {
            if (!hasPendingCall || !steamService.IsAvailable)
            {
                return;
            }

            // Measured against unscaled real time rather than accumulated from a delta, so a
            // timeout means fifteen seconds of wall clock however often Poll is driven.
            var now = UnityEngine.Time.unscaledTime;
            pendingCallElapsedSeconds += lastPollTime == 0f ? 0f : now - lastPollTime;
            lastPollTime = now;

            bool completed;
            bool ioFailure;
            try
            {
                completed = SteamUtils.IsAPICallCompleted(pendingCall, out ioFailure);
            }
            catch (Exception ex)
            {
                ClearPending();
                Fail($"IsAPICallCompleted threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (!completed)
            {
                if (pendingCallElapsedSeconds >= PendingCallTimeoutSeconds)
                {
                    ClearPending();
                    Fail(
                        "The CreateLobby result never arrived. The likely cause is the game's own "
                        + "callback pump: it drains the process's single manual-dispatch pipe every "
                        + "frame and frees each message, so our call result may be released before "
                        + "we can read it. If this is what happened, polling cannot be made to work "
                        + "and Phase 3 needs a different mechanism — see docs/steamworks/.");
                }

                return;
            }

            if (ioFailure)
            {
                ClearPending();
                Fail("IsAPICallCompleted reported an IO failure.");
                return;
            }

            ReadLobbyCreatedResult();
        }

        /// <summary>
        /// Reads <c>LobbyCreated_t</c> out of a buffer we own.
        ///
        /// <para>The point of doing it this way is that nothing here crosses the interop boundary
        /// as a struct: <c>GetAPICallResult</c> takes an <c>IntPtr</c> and a byte count, Steam
        /// writes native memory, and the fields come back out with <c>Marshal</c> at offsets from
        /// the dump. Phase 2 crashed reading a field off a <c>ValueType</c>-derived proxy that Steam
        /// had filled through an <c>out</c> parameter; this shape cannot fail that way.</para>
        /// </summary>
        private void ReadLobbyCreatedResult()
        {
            var buffer = Marshal.AllocHGlobal(LobbyCreatedSize);
            try
            {
                bool resultFailure;
                bool got;
                try
                {
                    got = SteamUtils.GetAPICallResult(
                        pendingCall, buffer, LobbyCreatedSize, LobbyCreatedCallbackId, out resultFailure);
                }
                catch (Exception ex)
                {
                    ClearPending();
                    Fail($"GetAPICallResult threw: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                ClearPending();

                if (!got || resultFailure)
                {
                    Fail(
                        $"GetAPICallResult returned {got} with failure flag {resultFailure}. The call "
                        + "completed, so the handle was valid — this is the result itself being "
                        + "unavailable, which is what the game's pump consuming it would look like.");
                    return;
                }

                var result = (EResult)Marshal.ReadInt32(buffer, LobbyCreatedResultOffset);
                var lobbyId = (ulong)Marshal.ReadInt64(buffer, LobbyCreatedLobbyIdOffset);

                if (result != EResult.k_EResultOK)
                {
                    Fail($"Steam refused the lobby: EResult {result}.");
                    return;
                }

                LobbyId = lobbyId;
                State = SteamLobbyState.InLobby;
                lastError = "";
                RefreshMembers();

                Plugin.Log.LogInfo(
                    $"[steam-lobby] Lobby {LobbyId} created, owner {IsOwner}, {members.Count} member(s). "
                    + "Polled call results work on this install.");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public bool SetLobbyData(string key, string value)
        {
            if (State != SteamLobbyState.InLobby)
            {
                return false;
            }

            try
            {
                return SteamMatchmaking.SetLobbyData(new CSteamID(LobbyId), key, value);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-lobby] SetLobbyData('{key}') threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public string GetLobbyData(string key)
        {
            if (State != SteamLobbyState.InLobby)
            {
                return "";
            }

            try
            {
                return SteamMatchmaking.GetLobbyData(new CSteamID(LobbyId), key) ?? "";
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-lobby] GetLobbyData('{key}') threw: {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }

        public bool SetLocalMemberData(string key, string value)
        {
            if (State != SteamLobbyState.InLobby)
            {
                return false;
            }

            try
            {
                // Returns void: Steam cannot refuse a member writing its own row, which is the
                // property that makes host authority over readiness a platform guarantee later.
                SteamMatchmaking.SetLobbyMemberData(new CSteamID(LobbyId), key, value);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-lobby] SetLobbyMemberData('{key}') threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        public string GetMemberData(ulong steamId, string key)
        {
            if (State != SteamLobbyState.InLobby)
            {
                return "";
            }

            try
            {
                return SteamMatchmaking.GetLobbyMemberData(new CSteamID(LobbyId), new CSteamID(steamId), key) ?? "";
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-lobby] GetLobbyMemberData('{key}') threw: {ex.GetType().Name}: {ex.Message}");
                return "";
            }
        }

        public IReadOnlyList<ulong> GetMembers() => members;

        /// <summary>
        /// Rebuilds the member list. At most six entries and only on a lobby change, so it is
        /// rebuilt rather than diffed — the same call this project makes everywhere else at this
        /// scale.
        /// </summary>
        private void RefreshMembers()
        {
            members.Clear();

            if (LobbyId == 0UL)
            {
                return;
            }

            try
            {
                var lobby = new CSteamID(LobbyId);
                var count = SteamMatchmaking.GetNumLobbyMembers(lobby);
                for (var i = 0; i < count; i++)
                {
                    members.Add(SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID);
                }

                IsOwner = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID == steamService.LocalSteamId;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-lobby] Reading the member list threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public string DescribeStatus() =>
            State switch
            {
                SteamLobbyState.InLobby => $"in lobby {LobbyId}, owner {IsOwner}, {members.Count} member(s)",
                SteamLobbyState.Pending => $"waiting on a Steam call ({pendingCallElapsedSeconds:F1}s)",
                SteamLobbyState.Failed => $"failed: {lastError}",
                _ => "not in a lobby",
            };

        private void ClearPending()
        {
            hasPendingCall = false;
            lastPollTime = 0f;
            pendingCallElapsedSeconds = 0f;
        }

        private void Fail(string message)
        {
            lastError = message;
            State = SteamLobbyState.Failed;
            Plugin.Log.LogWarning($"[steam-lobby] {message}");
        }
    }
}

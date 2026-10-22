using OmniBonk.Common;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OmniBonk.Services
{
    /// <summary>
    /// Steam lobbies, polled rather than called back.
    /// <para>IL2CPP has no <c>CallResult&lt;T&gt;</c> instantiation for lobby types, so results are read
    /// via <c>GetAPICallResult</c> into our own buffer at <c>dump.cs</c> offsets (no ValueType proxy,
    /// which crashed before).</para>
    /// <para>Race: the game's <c>CallbackDispatcher.RunFrame</c> can consume our result first
    /// (shows as <c>InvalidHandle</c>). Per-frame polling narrows but does not close the window.</para>
    /// </summary>
    internal class SteamLobbyService : ISteamLobbyService
    {
        #region Callback struct layouts

        // From dump.cs; sizes are pack(8) sizes. A wrong size fails silently in GetAPICallResult.
        //   LobbyCreated_t   k_iCallback 513   m_eResult @0x0 (int), m_ulSteamIDLobby @0x8 (ulong)
        private const int LobbyCreatedCallbackId = 513;
        private const int LobbyCreatedSize = 16;
        private const int LobbyCreatedResultOffset = 0;
        private const int LobbyCreatedLobbyIdOffset = 8;

        //   LobbyEnter_t     k_iCallback 504   m_ulSteamIDLobby @0x0 (ulong)
        //                                      m_rgfChatPermissions @0x8 (uint)
        //                                      m_bLocked @0xC (bool)
        //                                      m_EChatRoomEnterResponse @0x10 (uint)
        private const int LobbyEnterCallbackId = 504;
        private const int LobbyEnterSize = 24;
        private const int LobbyEnterLobbyIdOffset = 0;
        private const int LobbyEnterResponseOffset = 16;

        //   LobbyMatchList_t k_iCallback 510   m_nLobbiesMatching @0x0 (uint)
        // Four bytes, not eight: pack(8) caps alignment, it does not pad.
        private const int LobbyMatchListCallbackId = 510;
        private const int LobbyMatchListSize = 4;
        private const int LobbyMatchListCountOffset = 0;

        /// <summary><c>k_EChatRoomEnterResponseSuccess</c>. Every other value is a refusal.</summary>
        private const uint ChatRoomEnterSuccess = 1;

        #endregion

        private enum PendingCall
        {
            None,
            Create,
            Join,

            /// <summary>A lobby-list search, whose result feeds a join.</summary>
            List,
        }

        /// <summary>Generous: a timeout here means the game's pump consumed our result, not slowness.</summary>
        private const float PendingCallTimeoutSeconds = 15f;

        private readonly ISteamService steamService;

        private readonly List<ulong> members = [];

        private SteamAPICall_t pendingCall;
        private PendingCall pendingCallKind = PendingCall.None;

        /// <summary>Held in a field: unregister needs the same instance, and collection would be a native use-after-free.</summary>
        private SteamCallResult pendingCallResult;
        private bool hasPendingCall;
        private float pendingCallElapsedSeconds;
        private float lastPollTime;

        private string lastError = "";

        private string pendingJoinCode = "";

        private bool joinAfterSearch;

        /// <summary>Restored after a search: a search does not change membership.</summary>
        private SteamLobbyState stateBeforeSearch = SteamLobbyState.None;

        /// <summary>No O/0 or I/l/1 lookalikes.</summary>
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private const int CodeLength = 6;

        public SteamLobbyState State { get; private set; } = SteamLobbyState.None;

        public ulong LobbyId { get; private set; }

        public string LobbyCode { get; private set; } = "";

        public bool IsOwner { get; private set; }

        public ulong OwnerSteamId { get; private set; }

        public ulong LaunchLobbyId { get; }

        public bool HasPendingCall => hasPendingCall;

        public SteamLobbySearchState SearchState { get; private set; } = SteamLobbySearchState.Idle;

        public ulong FoundLobbyId { get; private set; }

        public SteamLobbyService(ISteamService steamService)
        {
            this.steamService = steamService;

            // Pure .NET, safe before Steam is up. Only logged for now.
            LaunchLobbyId = Helpers.LaunchArguments.GetConnectLobbyId();
            if (LaunchLobbyId != 0UL)
            {
                Plugin.Log.LogInfo(
                    $"[steam-lobby] Launched with +connect_lobby {LaunchLobbyId}; a friend invited us. "
                    + "Nothing consumes this yet.");
            }
        }

        public void CreateLobby(int maxMembers)
        {
            if (!steamService.IsAvailable)
            {
                Fail("Steam is not available, so no lobby can be created.");
                return;
            }

            LeaveStaleLobby("creating a new one");

            if (hasPendingCall || State == SteamLobbyState.InLobby)
            {
                Fail($"Refusing to create a lobby while {State}.");
                return;
            }

            try
            {
                // Public: RequestLobbyList only returns public lobbies to non-friends. The code is the access control.
                pendingCall = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, maxMembers);
                pendingCallKind = PendingCall.Create;
                hasPendingCall = true;
                RegisterPendingCallResult();

                // Reset both, or a stale lastPollTime times the new call out immediately.
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

        public void JoinLobby(ulong lobbyId)
        {
            if (!steamService.IsAvailable)
            {
                Fail("Steam is not available, so no lobby can be joined.");
                return;
            }

            if (State == SteamLobbyState.InLobby && LobbyId != lobbyId)
            {
                LeaveStaleLobby($"joining lobby {lobbyId}");
            }

            if (hasPendingCall || State == SteamLobbyState.InLobby)
            {
                Fail($"Refusing to join a lobby while {State}.");
                return;
            }

            if (lobbyId == 0UL)
            {
                Fail("Refusing to join lobby id 0.");
                return;
            }

            try
            {
                pendingCall = SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
                pendingCallKind = PendingCall.Join;
                hasPendingCall = true;
                RegisterPendingCallResult();
                pendingCallElapsedSeconds = 0f;
                lastPollTime = 0f;
                State = SteamLobbyState.Pending;
                lastError = "";

                Plugin.Log.LogInfo($"[steam-lobby] JoinLobby {lobbyId} requested.");
            }
            catch (Exception ex)
            {
                Fail($"JoinLobby threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Leaves a leftover lobby before creating or joining another. A client can be left InLobby
        /// after the host leaves, which blocked every later join until restart. No host migration,
        /// so leaving is always right.
        /// </summary>
        private void LeaveStaleLobby(string why)
        {
            if (State != SteamLobbyState.InLobby || hasPendingCall)
            {
                return;
            }

            Plugin.Log.LogInfo($"[steam-lobby] Still in lobby {LobbyId} (owner {IsOwner}, {members.Count} member(s)); leaving it before {why}.");
            LeaveLobby();
        }

        public void LeaveLobby()
        {
            // Always, or an in-flight create would later put us back InLobby.
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
            LobbyCode = "";
            IsOwner = false;
            OwnerSteamId = 0UL;
            members.Clear();
            State = SteamLobbyState.None;

            // Cleared or the friends list keeps offering Join Game for a lobby we have left.
            PublishConnectString();
        }

        /// <summary>
        /// So the game's pump hands us the result instead of consuming it. Non-fatal on failure:
        /// <see cref="Poll"/> remains as the fallback, first arrival wins.
        /// </summary>
        private void RegisterPendingCallResult()
        {
            try
            {
                pendingCallResult = new SteamCallResult { Handler = OnResultDelivered };
                CallbackDispatcher.Register(pendingCall, pendingCallResult);
            }
            catch (Exception ex)
            {
                pendingCallResult = null;
                Plugin.Log.LogWarning(
                    $"[steam-lobby] Could not register a call result, falling back to polling: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary><c>result</c> is freed when this returns; do not keep it.</summary>
        private void OnResultDelivered(IntPtr result, bool failed)
        {
            // The poll may have won already.
            if (!hasPendingCall)
            {
                return;
            }

            Plugin.Log.LogInfo("[steam-lobby] Result delivered by the game's dispatcher.");
            ApplyResult(pendingCallKind, result, failed);
        }

        private void ApplyResult(PendingCall kind, IntPtr result, bool failed)
        {
            ClearPending();

            if (failed)
            {
                Fail($"Steam reported a failure completing the {kind} call.");
                return;
            }

            switch (kind)
            {
                case PendingCall.Create:
                    ApplyLobbyCreated(result);
                    return;

                case PendingCall.Join:
                    ApplyLobbyEnter(result);
                    return;

                case PendingCall.List:
                    ApplyLobbyMatchList(result);
                    return;

                default:
                    Fail("A call completed with no record of what it was.");
                    return;
            }
        }

        private static (int Size, int CallbackId) ResultShapeFor(PendingCall kind) =>
            kind switch
            {
                PendingCall.Create => (LobbyCreatedSize, LobbyCreatedCallbackId),
                PendingCall.Join => (LobbyEnterSize, LobbyEnterCallbackId),
                PendingCall.List => (LobbyMatchListSize, LobbyMatchListCallbackId),
                _ => (0, 0),
            };

        public void Poll()
        {
            RefreshMembersIfDue();

            if (!hasPendingCall || !steamService.IsAvailable)
            {
                return;
            }

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
                    var kind = pendingCallKind;
                    ClearPending();
                    Fail($"The {kind} result never arrived within {PendingCallTimeoutSeconds:F0}s.");
                }

                return;
            }

            if (ioFailure)
            {
                var reason = DescribeCallFailure();
                ClearPending();
                Fail($"The call completed but its result is unusable: {reason}.");
                return;
            }

            ReadPolledResult();
        }

        /// <summary>Fallback when the registered <see cref="SteamCallResult"/> never dispatches.</summary>
        private void ReadPolledResult()
        {
            var kind = pendingCallKind;
            var (size, callbackId) = ResultShapeFor(kind);
            if (size == 0)
            {
                ClearPending();
                Fail("A call completed with no record of what it was.");
                return;
            }

            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                bool resultFailure;
                bool got;
                try
                {
                    got = SteamUtils.GetAPICallResult(pendingCall, buffer, size, callbackId, out resultFailure);
                }
                catch (Exception ex)
                {
                    ClearPending();
                    Fail($"GetAPICallResult threw: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                if (!got || resultFailure)
                {
                    var reason = DescribeCallFailure();
                    ClearPending();
                    Fail($"The result could not be retrieved: {reason}.");
                    return;
                }

                ApplyResult(kind, buffer, failed: false);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Raw Marshal reads, never a ValueType proxy (that crashed in Phase 2).</summary>
        private void ApplyLobbyCreated(IntPtr buffer)
        {
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
            PublishHostData();

            Plugin.Log.LogInfo(
                $"[steam-lobby] Lobby {LobbyId} created, code {LobbyCode}, owner {IsOwner}, "
                + $"{members.Count} member(s).");
        }

        /// <summary>
        /// Checks the protocol after entering: metadata is only readable by members, and lobbies
        /// reached by id or invite never passed the list filter.
        /// </summary>
        private void ApplyLobbyEnter(IntPtr buffer)
        {
            var lobbyId = (ulong)Marshal.ReadInt64(buffer, LobbyEnterLobbyIdOffset);
            var response = (uint)Marshal.ReadInt32(buffer, LobbyEnterResponseOffset);

            if (response != ChatRoomEnterSuccess)
            {
                Fail($"Steam refused entry to lobby {lobbyId}: EChatRoomEnterResponse {response}.");
                return;
            }

            LobbyId = lobbyId;
            State = SteamLobbyState.InLobby;
            lastError = "";
            RefreshMembers();

            var published = GetLobbyData(Protocol.VersionKey);
            if (!Protocol.IsCompatible(published))
            {
                // Mismatch silently corrupts sessions; empty means a pre-gate build, also refused.
                var describe = string.IsNullOrEmpty(published) ? "no version" : $"version '{published}'";
                LeaveLobby();
                Fail(
                    $"Lobby {lobbyId} publishes {describe}; this build speaks protocol "
                    + $"{Protocol.Version}. Left it. Both players need the same mod version.");
                return;
            }

            LobbyCode = GetLobbyData(SteamLobbyKeys.Code);
            PublishConnectString();

            Plugin.Log.LogInfo(
                $"[steam-lobby] Joined lobby {LobbyId}, code {LobbyCode}, owner {IsOwner}, "
                + $"{members.Count} member(s), protocol {published}.");
        }

        public void JoinByCode(string code)
        {
            // Any other lobby we are in here is stale.
            if (!string.Equals(LobbyCode, code, StringComparison.OrdinalIgnoreCase))
            {
                LeaveStaleLobby($"joining room {code}");
            }

            if (State == SteamLobbyState.InLobby)
            {
                Fail("Refusing to join a lobby while already in one.");
                return;
            }

            StartSearch(SteamLobbyKeys.Code, code, joinWhenFound: true);
        }

        public void JoinByMatchmakerCode(string code)
        {
            if (State == SteamLobbyState.InLobby)
            {
                return;
            }

            // Finds the Steam lobby for a session joined by matchmaker room code.
            StartSearch(SteamLobbyKeys.MatchmakerCode, code, joinWhenFound: true);
        }

        public void FindLobbyByCode(string code) =>
            StartSearch(SteamLobbyKeys.Code, code, joinWhenFound: false);

        private void StartSearch(string key, string code, bool joinWhenFound)
        {
            if (!steamService.IsAvailable)
            {
                Fail("Steam is not available, so no lobby can be searched for.");
                return;
            }

            if (hasPendingCall)
            {
                Fail("Refusing to search while another Steam call is in flight.");
                return;
            }

            var normalised = NormaliseCode(code);
            if (normalised.Length != CodeLength)
            {
                SearchState = SteamLobbySearchState.Failed;
                Fail($"'{code}' is not a {CodeLength}-character lobby code.");
                return;
            }

            try
            {
                // P1-3: filter by protocol so mismatched builds never appear. The entry check still applies.
                SteamMatchmaking.AddRequestLobbyListStringFilter(
                    Protocol.VersionKey, Protocol.Version.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListStringFilter(
                    key, normalised, ELobbyComparison.k_ELobbyComparisonEqual);

                SteamMatchmaking.AddRequestLobbyListResultCountFilter(1);

                pendingCall = SteamMatchmaking.RequestLobbyList();
                pendingCallKind = PendingCall.List;
                pendingJoinCode = normalised;
                joinAfterSearch = joinWhenFound;
                hasPendingCall = true;
                RegisterPendingCallResult();
                pendingCallElapsedSeconds = 0f;
                lastPollTime = 0f;

                SearchState = SteamLobbySearchState.Searching;
                FoundLobbyId = 0UL;

                // A search-only can run from inside a lobby.
                stateBeforeSearch = State;
                State = SteamLobbyState.Pending;
                lastError = "";

                Plugin.Log.LogInfo(
                    $"[steam-lobby] Searching for lobby code {normalised}"
                    + (joinWhenFound ? ", will join it." : ", without joining."));
            }
            catch (Exception ex)
            {
                Fail($"RequestLobbyList threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void OpenInviteOverlay()
        {
            if (State != SteamLobbyState.InLobby)
            {
                return;
            }

            try
            {
                // GameLobbyJoinRequested_t cannot be received here; only +connect_lobby comes back.
                SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(LobbyId));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-lobby] ActivateGameOverlayInviteDialog threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ApplyLobbyMatchList(IntPtr buffer)
        {
            var code = pendingJoinCode;
            var matching = (uint)Marshal.ReadInt32(buffer, LobbyMatchListCountOffset);
            State = stateBeforeSearch;

            if (matching == 0)
            {
                SearchState = SteamLobbySearchState.Completed;
                FoundLobbyId = 0UL;

                if (!joinAfterSearch)
                {
                    Plugin.Log.LogInfo($"[steam-lobby] No lobby matched code {code}.");
                    return;
                }

                // Steam's version filter makes a mismatched lobby indistinguishable from a missing one.
                Fail(
                    $"No lobby found with code {code}. Either nobody is hosting it, or the host "
                    + "is running a different version of the mod.");
                return;
            }

            ulong lobbyId;
            try
            {
                lobbyId = SteamMatchmaking.GetLobbyByIndex(0).m_SteamID;
            }
            catch (Exception ex)
            {
                SearchState = SteamLobbySearchState.Failed;
                Fail($"GetLobbyByIndex threw: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            SearchState = SteamLobbySearchState.Completed;
            FoundLobbyId = lobbyId;

            if (!joinAfterSearch)
            {
                Plugin.Log.LogInfo($"[steam-lobby] Code {code} resolved to lobby {lobbyId}.");
                return;
            }

            Plugin.Log.LogInfo($"[steam-lobby] Code {code} resolved to lobby {lobbyId}; joining.");
            JoinLobby(lobbyId);
        }

        /// <summary>Host only; Steam ignores lobby writes from non-owners.</summary>
        private void PublishHostData()
        {
            LobbyCode = GenerateCode();

            SetLobbyData(Protocol.VersionKey, Protocol.Version.ToString());
            SetLobbyData(SteamLobbyKeys.Code, LobbyCode);
            SetLobbyData(SteamLobbyKeys.HostName, Configuration.ModConfig.PlayerName.Value ?? "");

            PublishConnectString();
        }

        /// <summary>Rich-presence "connect" key: gives friends a Join Game that launches with its value.</summary>
        private void PublishConnectString()
        {
            try
            {
                SteamFriends.SetRichPresence(
                    SteamLobbyKeys.RichPresenceConnect,
                    LobbyId == 0UL ? "" : $"+connect_lobby {LobbyId}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-lobby] SetRichPresence threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary><c>InvalidHandle</c> means the game's pump consumed our result first.</summary>
        private string DescribeCallFailure()
        {
            ESteamAPICallFailure reason;
            try
            {
                reason = SteamUtils.GetAPICallFailureReason(pendingCall);
            }
            catch (Exception ex)
            {
                return $"reason unavailable ({ex.GetType().Name})";
            }

            return reason switch
            {
                ESteamAPICallFailure.k_ESteamAPICallFailureInvalidHandle =>
                    "InvalidHandle - the result was already consumed, almost certainly by the game's "
                    + "own callback pump draining the shared manual-dispatch pipe before we read it",
                ESteamAPICallFailure.k_ESteamAPICallFailureMismatchedCallback =>
                    "MismatchedCallback - the expected callback id does not match the call",
                ESteamAPICallFailure.k_ESteamAPICallFailureNetworkFailure => "NetworkFailure",
                ESteamAPICallFailure.k_ESteamAPICallFailureSteamGone => "SteamGone",
                ESteamAPICallFailure.k_ESteamAPICallFailureNone => "None reported, which is itself odd",
                _ => $"unknown ({(int)reason})",
            };
        }

        private static string NormaliseCode(string code) =>
            string.IsNullOrWhiteSpace(code) ? "" : code.Trim().ToUpperInvariant();

        /// <summary>Cryptographic RNG: the lobby is public, so a predictable code would let strangers in.</summary>
        private static string GenerateCode()
        {
            var bytes = new byte[CodeLength];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);

            var chars = new char[CodeLength];
            for (var i = 0; i < CodeLength; i++)
            {
                chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
            }

            return new string(chars);
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

        private const float MEMBER_REFRESH_INTERVAL_SECONDS = 1f;
        private float lastMemberRefresh = -999f;

        /// <summary>
        /// OB-22: picks up members who join later (host never saw the client's avatar). Polled, not
        /// LobbyChatUpdate_t, since Callback&lt;T&gt; via interop is unproven; reads a local cache.
        /// </summary>
        private void RefreshMembersIfDue()
        {
            if (State != SteamLobbyState.InLobby || !steamService.IsAvailable)
            {
                return;
            }

            var now = UnityEngine.Time.unscaledTime;
            if (now - lastMemberRefresh < MEMBER_REFRESH_INTERVAL_SECONDS)
            {
                return;
            }

            lastMemberRefresh = now;

            var before = members.Count;
            var ownerBefore = OwnerSteamId;
            RefreshMembers();

            if (members.Count != before)
            {
                Plugin.Log.LogInfo($"[steam-lobby] Member list now has {members.Count} member(s) (was {before}).");
            }

            // Owner change means the host left; LeaveStaleLobby releases it on the next create/join.
            if (ownerBefore != 0UL && OwnerSteamId != ownerBefore)
            {
                Plugin.Log.LogInfo($"[steam-lobby] Lobby {LobbyId} owner changed {ownerBefore} -> {OwnerSteamId} (now ours: {IsOwner}); the host left.");
            }
        }

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

                OwnerSteamId = SteamMatchmaking.GetLobbyOwner(lobby).m_SteamID;
                IsOwner = OwnerSteamId == steamService.LocalSteamId;
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
            // Unregister first, or a late result re-enters a cleared state machine.
            if (pendingCallResult != null)
            {
                try
                {
                    CallbackDispatcher.Unregister(pendingCall, pendingCallResult);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning(
                        $"[steam-lobby] Unregistering a call result threw: {ex.GetType().Name}: {ex.Message}");
                }

                pendingCallResult = null;
            }

            hasPendingCall = false;
            pendingCallKind = PendingCall.None;
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

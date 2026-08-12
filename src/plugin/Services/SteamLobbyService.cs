using MegabonkTogether.Common;
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
    /// <para><b>There is a race here, and it is the main risk in this class.</b> The game pumps
    /// <c>CallbackDispatcher.RunFrame</c> every frame, which drains the process's single
    /// manual-dispatch pipe — including the <c>SteamAPICallCompleted_t</c> raised for <i>our</i>
    /// call — and frees each message. Whoever reaches the result first gets it. One run created and
    /// read back a lobby successfully; the next reported the call complete with its failure flag
    /// set, which is what a consumed handle looks like.</para>
    ///
    /// <para>Mitigated by polling every frame while a call is in flight, from a ticker whose
    /// GameObject is created in <c>Plugin.Load</c> and therefore updates before the game's own
    /// <c>SteamManager</c>. <b>That narrows the window rather than closing it</b> — Unity does not
    /// guarantee ordering between two default-priority scripts. If
    /// <c>k_ESteamAPICallFailureInvalidHandle</c> keeps appearing, the fix is not a faster poll but
    /// a private pipe: direct P/Invoke, or a <c>CallResult</c> injected into the game's own
    /// dispatcher.</para>
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

        //   LobbyEnter_t     k_iCallback 504   m_ulSteamIDLobby @0x0 (ulong)
        //                                      m_rgfChatPermissions @0x8 (uint)
        //                                      m_bLocked @0xC (bool)
        //                                      m_EChatRoomEnterResponse @0x10 (uint)
        private const int LobbyEnterCallbackId = 504;
        private const int LobbyEnterSize = 24;
        private const int LobbyEnterLobbyIdOffset = 0;
        private const int LobbyEnterResponseOffset = 16;

        //   LobbyMatchList_t k_iCallback 510   m_nLobbiesMatching @0x0 (uint)
        //
        // Four bytes, not eight. Steam's pack(8) caps alignment, it does not pad a struct up to
        // it, and cubCallback has to be the size Steam actually wrote.
        private const int LobbyMatchListCallbackId = 510;
        private const int LobbyMatchListSize = 4;
        private const int LobbyMatchListCountOffset = 0;

        /// <summary><c>k_EChatRoomEnterResponseSuccess</c>. Every other value is a refusal.</summary>
        private const uint ChatRoomEnterSuccess = 1;

        #endregion

        /// <summary>Which result the in-flight call will produce.</summary>
        private enum PendingCall
        {
            None,
            Create,
            Join,

            /// <summary>A lobby-list search, whose result feeds a join.</summary>
            List,
        }

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
        private PendingCall pendingCallKind = PendingCall.None;

        /// <summary>
        /// Registered with the game's dispatcher for the duration of a call, so the result is
        /// delivered rather than raced for. Held in a field because unregistering needs the same
        /// instance, and because letting it be collected while the dispatcher holds a reference
        /// would be a use-after-free on the native side.
        /// </summary>
        private SteamCallResult pendingCallResult;
        private bool hasPendingCall;
        private float pendingCallElapsedSeconds;
        private float lastPollTime;

        private string lastError = "";

        /// <summary>The code the in-flight search is looking for, held so the result can say so.</summary>
        private string pendingJoinCode = "";

        /// <summary>Whether the in-flight search should join what it finds.</summary>
        private bool joinAfterSearch;

        /// <summary>
        /// Restored when a search-only completes. A search does not change lobby membership, so it
        /// must not leave <see cref="State"/> saying it did.
        /// </summary>
        private SteamLobbyState stateBeforeSearch = SteamLobbyState.None;

        /// <summary>
        /// Codes avoid characters that are misread when spoken or retyped: no O against 0, no I or
        /// l against 1. Thirty-two symbols over six places is about a billion codes, which is
        /// ample for a game where a lobby lives for minutes.
        /// </summary>
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

            // Read once, here, rather than polled: the command line cannot change while the process
            // runs, and this is pure .NET with no Steam involved, so it is safe long before Steam
            // is up. Nothing acts on it yet — the lobby flow is still the WebSocket matchmaker's —
            // but it is logged so an invite-launch is visible in a log we are sent.
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

            if (hasPendingCall || State == SteamLobbyState.InLobby)
            {
                Fail($"Refusing to create a lobby while {State}.");
                return;
            }

            try
            {
                // Public, which is a real change from the WebSocket matchmaker and worth being
                // explicit about. RequestLobbyList only returns public lobbies to a non-friend, so
                // a code that strangers can use requires one. The code is what keeps a lobby
                // private in practice — the same model as today, where the six characters are the
                // only thing standing between a stranger and your session — but the lobby itself
                // is now enumerable by anyone who asks Steam for this app's list.
                pendingCall = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, maxMembers);
                pendingCallKind = PendingCall.Create;
                hasPendingCall = true;
                RegisterPendingCallResult();

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

        public void JoinLobby(ulong lobbyId)
        {
            if (!steamService.IsAvailable)
            {
                Fail("Steam is not available, so no lobby can be joined.");
                return;
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
            LobbyCode = "";
            IsOwner = false;
            OwnerSteamId = 0UL;
            members.Clear();
            State = SteamLobbyState.None;

            // Cleared or the friends list keeps offering Join Game for a lobby we have left.
            PublishConnectString();
        }

        /// <summary>
        /// Registers a <see cref="SteamCallResult"/> for the call now in flight, so the game's own
        /// callback pump hands us the result instead of consuming it.
        ///
        /// <para>Failure here is not fatal and is not treated as such: the poll in <see cref="Poll"/>
        /// remains, and between them whichever arrives first wins. That redundancy is deliberate
        /// while the injected type is unproven — if registration silently never dispatches, the
        /// behaviour is exactly what it was before this existed rather than worse.</para>
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

        /// <summary>
        /// The dispatcher delivering our result, from inside the game's own <c>Update</c>.
        ///
        /// <para><c>result</c> is the dispatcher's buffer and is freed as soon as this returns, so
        /// it is read here and not kept.</para>
        /// </summary>
        private void OnResultDelivered(IntPtr result, bool failed)
        {
            // The poll may have got there first on a lucky frame. Whoever is second finds nothing
            // pending and does nothing, rather than applying the same result twice.
            if (!hasPendingCall)
            {
                return;
            }

            Plugin.Log.LogInfo("[steam-lobby] Result delivered by the game's dispatcher.");
            ApplyResult(pendingCallKind, result, failed);
        }

        /// <summary>
        /// The one place a completed call is turned into state, whichever path delivered it.
        /// </summary>
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

        /// <summary>Result size and callback id for each kind, kept next to the layouts above.</summary>
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

        /// <summary>
        /// The fallback path: fetch the result ourselves, for the case where the dispatcher never
        /// delivered it.
        ///
        /// <para>Kept alongside the registered <see cref="SteamCallResult"/> rather than replaced by
        /// it. If registration works this rarely runs; if the injected type silently never
        /// dispatches, this is still the mechanism and the behaviour is what it was before. The one
        /// thing it cannot do is beat the game's pump, which is the whole reason the registration
        /// exists.</para>
        /// </summary>
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

        /// <summary>
        /// Reads <c>LobbyCreated_t</c> out of a buffer we own.
        ///
        /// <para>The point of doing it this way is that nothing here crosses the interop boundary
        /// as a struct: <c>GetAPICallResult</c> takes an <c>IntPtr</c> and a byte count, Steam
        /// writes native memory, and the fields come back out with <c>Marshal</c> at offsets from
        /// the dump. Phase 2 crashed reading a field off a <c>ValueType</c>-derived proxy that Steam
        /// had filled through an <c>out</c> parameter; this shape cannot fail that way.</para>
        /// </summary>
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
        /// Reads <c>LobbyEnter_t</c>, then applies the protocol gate.
        ///
        /// <para><b>The version is checked after entering, not before, and that is not a
        /// compromise.</b> Lobby metadata is only readable once you are a member — before that
        /// there is nothing to read except through the lobby list, which the browser filters
        /// separately. Entering and leaving again costs a round trip and no state, and it is the
        /// only way to gate a lobby reached by id or by an invite that never went through a
        /// filtered list.</para>
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
                // Left immediately rather than tolerated. A version mismatch is exactly the
                // silent-corruption case the constant exists to prevent, and an empty value
                // means a build from before the gate existed — refused on purpose.
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

            // Searches on the matchmaker's room code rather than the Steam lobby's own. A client
            // that joined by typing a room code never saw a Steam lobby id, and this is how it
            // finds the one standing for the session it is already in.
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
                // Both filters, not just the code. Filtering the browser by protocol version is
                // P1-3 in its final form: a mismatched build never appears in the results, so it is
                // refused before a connection is attempted rather than after. The entry check in
                // ReadLobbyEnterResult still stands, because a lobby reached by id or by an invite
                // never passed through here.
                SteamMatchmaking.AddRequestLobbyListStringFilter(
                    Protocol.VersionKey, Protocol.Version.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListStringFilter(
                    key, normalised, ELobbyComparison.k_ELobbyComparisonEqual);

                // A code identifies one lobby. Asking for more results would be asking Steam to do
                // work whose answer we would throw away.
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

                // Remembered rather than assumed None: a search-only can run from inside a lobby,
                // and it must give membership back exactly as it found it.
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
                // Steam's own invite dialog, so the friend list, the invite and the accept flow are
                // all Valve's. Nothing comes back to us here: accepting produces a
                // GameLobbyJoinRequested_t on the invitee's machine, which this install cannot
                // receive, or a +connect_lobby launch argument if their game was closed.
                SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(LobbyId));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[steam-lobby] ActivateGameOverlayInviteDialog threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads <c>LobbyMatchList_t</c> and joins the single lobby it should contain.
        /// </summary>
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
                    // Not a failure on the search-only path: "nothing matched" is a result.
                    Plugin.Log.LogInfo($"[steam-lobby] No lobby matched code {code}.");
                    return;
                }

                // One message for both causes on purpose. A player cannot tell "no such lobby"
                // from "that lobby is a different mod version", and neither can we — the version
                // filter is applied by Steam, so a mismatched lobby is simply absent from the
                // results rather than reported as incompatible.
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

        /// <summary>
        /// Writes everything a joiner needs to find and vet this lobby. Host only — Steam refuses
        /// lobby-level writes from anyone but the owner, so this silently does nothing elsewhere.
        /// </summary>
        private void PublishHostData()
        {
            LobbyCode = GenerateCode();

            SetLobbyData(Protocol.VersionKey, Protocol.Version.ToString());
            SetLobbyData(SteamLobbyKeys.Code, LobbyCode);
            SetLobbyData(SteamLobbyKeys.HostName, Configuration.ModConfig.PlayerName.Value ?? "");

            PublishConnectString();
        }

        /// <summary>
        /// Sets, or clears, the rich-presence string that puts <b>Join Game</b> next to our name on
        /// a friend's list. Steam looks for the literal key "connect" and hands its value back to
        /// the friend's game as a launch argument, which is the half of invites this install can
        /// actually receive.
        /// </summary>
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

        /// <summary>
        /// Asks Steam why the in-flight call's result is unusable, and says what that means here.
        ///
        /// <para>The distinction that matters is <c>InvalidHandle</c>. It means the handle no
        /// longer exists — which is what "the game's callback pump retrieved and freed our result
        /// before we polled for it" looks like from this side. Anything else is an ordinary
        /// failure and says nothing about the polling approach.</para>
        /// </summary>
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
                    "InvalidHandle — the result was already consumed, almost certainly by the game's "
                    + "own callback pump draining the shared manual-dispatch pipe before we read it",
                ESteamAPICallFailure.k_ESteamAPICallFailureMismatchedCallback =>
                    "MismatchedCallback — the expected callback id does not match the call",
                ESteamAPICallFailure.k_ESteamAPICallFailureNetworkFailure => "NetworkFailure",
                ESteamAPICallFailure.k_ESteamAPICallFailureSteamGone => "SteamGone",
                ESteamAPICallFailure.k_ESteamAPICallFailureNone => "None reported, which is itself odd",
                _ => $"unknown ({(int)reason})",
            };
        }

        private static string NormaliseCode(string code) =>
            string.IsNullOrWhiteSpace(code) ? "" : code.Trim().ToUpperInvariant();

        /// <summary>
        /// A code drawn from a cryptographic source rather than <c>System.Random</c>. Not because a
        /// lobby code is a secret worth attacking, but because a predictable one lets anybody walk
        /// the space and drop into strangers' games, and the whole reason the lobby is public is
        /// that the code is the only thing keeping them out.
        /// </summary>
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
            // Unregistered before anything else: the dispatcher holds this instance, and a result
            // arriving for a call we have finished with would re-enter a cleared state machine.
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

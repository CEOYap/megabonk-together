using MegabonkTogether.Common;
using Steamworks;
using System;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Runs Phase 3's primitives once and reports what happened, so the two things nobody knows
    /// about this install stop being assumptions.
    ///
    /// <para><b>1. Do strings survive the boundary?</b> Every lobby call takes or returns one.
    /// Ordinary game methods take strings fine throughout this codebase, but <c>AssetBundle</c>'s
    /// did not — it marshalled through <c>Il2CppSystem.ReadOnlySpan&lt;char&gt;.GetPinnableReference</c>,
    /// which is unbound here, and cost several build-and-launch cycles before anyone worked that
    /// out. Steamworks.NET marshals through its own UTF-8 handle instead, so it should be fine;
    /// "should be fine" is what the last two of these were called.</para>
    ///
    /// <para><b>2. Can a polled call result be read at all?</b> The game drains the process's
    /// single manual-dispatch pipe every frame and frees each message. If that also releases the
    /// call result, <c>GetAPICallResult</c> can never see ours, and Phase 3's whole approach needs
    /// rethinking. This is the cheapest possible way to find out, and it needs one player.</para>
    ///
    /// <para>Off by default. It creates a <b>private</b> Steam lobby on Valve's infrastructure and
    /// leaves it again, which is real but invisible to anyone else.</para>
    /// </summary>
    internal class SteamLobbySelfTest
    {
        private enum Step
        {
            NotStarted,
            Strings,
            AwaitingLobby,
            LobbyData,
            AwaitingSearch,
            Done,
        }

        private const string ProbeKey = "mt_probe";
        private const string ProbeValue = "phase3";

        private readonly ISteamService steamService;
        private readonly ISteamLobbyService lobbyService;

        /// <summary>Roughly nine seconds of grace for Steam to index a lobby it has just created.</summary>
        private const int MaxSearchAttempts = 6;
        private const float SearchRetryIntervalSeconds = 1.5f;

        private Step step = Step.NotStarted;
        private bool stringsPassed;
        private string searchedCode = "";
        private int searchAttempts;
        private float nextSearchAt;

        public SteamLobbySelfTest(ISteamService steamService, ISteamLobbyService lobbyService)
        {
            this.steamService = steamService;
            this.lobbyService = lobbyService;
        }

        /// <summary>Whether the test has finished, successfully or not.</summary>
        public bool IsFinished => step == Step.Done;

        public void Advance()
        {
            if (step == Step.Done || steamService.Readiness != SteamReadiness.Ready)
            {
                return;
            }

            switch (step)
            {
                case Step.NotStarted:
                    step = Step.Strings;
                    return;

                case Step.Strings:
                    stringsPassed = RunStringRoundTrip();
                    lobbyService.CreateLobby(maxMembers: 6);
                    step = Step.AwaitingLobby;
                    return;

                case Step.AwaitingLobby:
                    if (lobbyService.State == SteamLobbyState.Pending)
                    {
                        return;
                    }

                    if (lobbyService.State != SteamLobbyState.InLobby)
                    {
                        Finish(false, $"lobby creation {lobbyService.DescribeStatus()}");
                        return;
                    }

                    step = Step.LobbyData;
                    return;

                case Step.LobbyData:
                    RunLobbyDataRoundTrip();

                    searchedCode = lobbyService.LobbyCode;
                    if (string.IsNullOrEmpty(searchedCode))
                    {
                        lobbyService.LeaveLobby();
                        Finish(false, "the host published no lobby code");
                        return;
                    }

                    // Searched from *inside* the lobby, which the first version of this test got
                    // wrong: it left first, and the last member leaving destroys the lobby, so it
                    // was searching for something that no longer existed and reporting the empty
                    // result as a search failure.
                    step = Step.AwaitingSearch;
                    StartSearchAttempt();
                    return;

                case Step.AwaitingSearch:
                    AdvanceSearch();
                    return;
            }
        }

        /// <summary>
        /// Searches for our own lobby, retrying for a few seconds.
        ///
        /// <para>Steam's lobby list is an index, not the lobby itself, and a lobby created and
        /// written to a moment ago may not be in it yet. A single attempt would report an indexing
        /// delay as a broken search.</para>
        /// </summary>
        private void AdvanceSearch()
        {
            if (lobbyService.SearchState == SteamLobbySearchState.Searching)
            {
                return;
            }

            if (lobbyService.FoundLobbyId != 0UL)
            {
                var matchedOurs = lobbyService.FoundLobbyId == lobbyService.LobbyId;
                lobbyService.LeaveLobby();

                Finish(
                    matchedOurs,
                    matchedOurs
                        ? $"lobby created, written to, read back, and found by code {searchedCode}"
                        : $"code {searchedCode} matched lobby {lobbyService.FoundLobbyId}, not ours");
                return;
            }

            if (searchAttempts < MaxSearchAttempts)
            {
                if (UnityEngine.Time.unscaledTime >= nextSearchAt)
                {
                    StartSearchAttempt();
                }

                return;
            }

            lobbyService.LeaveLobby();

            // Not reported as a pass or a clean fail, because a single player cannot tell the two
            // apart: either the filtered search does not work, or Steam declines to return a lobby
            // to the machine already sitting in it. Only a second machine settles that.
            Finish(
                false,
                $"code {searchedCode} never came back from the lobby list after {MaxSearchAttempts} "
                + "attempts. Inconclusive on one machine — this is either a broken search or Steam "
                + "not listing a lobby back to its own member. A second player settles it.");
        }

        private void StartSearchAttempt()
        {
            searchAttempts++;
            nextSearchAt = UnityEngine.Time.unscaledTime + SearchRetryIntervalSeconds;
            lobbyService.FindLobbyByCode(searchedCode);
        }

        /// <summary>
        /// A string in and the same string out, with no lobby involved. Rich presence is used
        /// because Phase 3 wants it anyway and it needs nothing set up first — and the key is a
        /// custom one, so it never reaches a friends list as displayed text.
        /// </summary>
        private bool RunStringRoundTrip()
        {
            try
            {
                if (!SteamFriends.SetRichPresence(ProbeKey, ProbeValue))
                {
                    Plugin.Log.LogWarning("[steam-lobby] Self-test: SetRichPresence returned false.");
                    return false;
                }

                var read = SteamFriends.GetFriendRichPresence(new CSteamID(steamService.LocalSteamId), ProbeKey);
                SteamFriends.SetRichPresence(ProbeKey, "");

                if (read != ProbeValue)
                {
                    Plugin.Log.LogWarning(
                        $"[steam-lobby] Self-test: strings do not round-trip. Wrote '{ProbeValue}', read '{read}'.");
                    return false;
                }

                Plugin.Log.LogInfo("[steam-lobby] Self-test: strings round-trip correctly.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    $"[steam-lobby] Self-test: rich presence threw: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Lobby data and member data, which is the shape Phase 3 replaces union tags 73 and 74
        /// with. The protocol version is written with the key it will really use, so the readback
        /// exercises the actual gate rather than a stand-in.
        /// </summary>
        private void RunLobbyDataRoundTrip()
        {
            var wroteLobby = lobbyService.SetLobbyData(Protocol.VersionKey, Protocol.Version.ToString());
            var readLobby = lobbyService.GetLobbyData(Protocol.VersionKey);
            var compatible = Protocol.IsCompatible(readLobby);

            var wroteMember = lobbyService.SetLocalMemberData("ready", "1");
            var readMember = lobbyService.GetMemberData(steamService.LocalSteamId, "ready");

            Plugin.Log.LogInfo(
                $"[steam-lobby] Self-test: lobby data write {wroteLobby}, read '{readLobby}', "
                + $"compatible {compatible}. Member data write {wroteMember}, read '{readMember}'. "
                + $"Members: {lobbyService.GetMembers().Count}.");
        }

        private void Finish(bool passed, string detail)
        {
            step = Step.Done;

            var summary =
                $"[steam-lobby] Self-test finished: {(passed ? "PASSED" : "FAILED")} — {detail}. "
                + $"Strings {(stringsPassed ? "ok" : "FAILED")}.";

            if (passed && stringsPassed)
            {
                Plugin.Log.LogInfo(summary);
            }
            else
            {
                // A warning rather than an error: nothing depends on any of this yet, and a failure
                // here is a finding about the install rather than a fault in a running session.
                Plugin.Log.LogWarning(summary);
            }
        }
    }
}

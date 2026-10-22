using OmniBonk.Common;
using Steamworks;
using System;

namespace OmniBonk.Services
{
    /// <summary>
    /// One-player self-test: do strings survive the IL2CPP/Steam boundary, and can a polled call
    /// result be read before the game's pump frees it? Off by default; creates and leaves a private lobby.
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

                    // Search from inside: the last member leaving destroys the lobby.
                    step = Step.AwaitingSearch;
                    StartSearchAttempt();
                    return;

                case Step.AwaitingSearch:
                    AdvanceSearch();
                    return;
            }
        }

        /// <summary>Retries: Steam's lobby index lags behind creation.</summary>
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

            // Inconclusive with one player: Steam may hide a lobby from its own member.
            Finish(
                false,
                $"code {searchedCode} never came back from the lobby list after {MaxSearchAttempts} "
                + "attempts. Inconclusive on one machine - this is either a broken search or Steam "
                + "not listing a lobby back to its own member. A second player settles it.");
        }

        private void StartSearchAttempt()
        {
            searchAttempts++;
            nextSearchAt = UnityEngine.Time.unscaledTime + SearchRetryIntervalSeconds;
            lobbyService.FindLobbyByCode(searchedCode);
        }

        /// <summary>String round-trip via rich presence, under a custom key never displayed to friends.</summary>
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

        /// <summary>Lobby and member data round-trip, using the real protocol version key.</summary>
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
                $"[steam-lobby] Self-test finished: {(passed ? "PASSED" : "FAILED")} - {detail}. "
                + $"Strings {(stringsPassed ? "ok" : "FAILED")}.";

            if (passed && stringsPassed)
            {
                Plugin.Log.LogInfo(summary);
            }
            else
            {
                Plugin.Log.LogWarning(summary);
            }
        }
    }
}

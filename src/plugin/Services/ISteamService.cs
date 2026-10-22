namespace OmniBonk.Services
{
    /// <summary>Coarse Steam P2P readiness: connect, wait, give up, or no Steam at all.</summary>
    public enum SteamReadiness
    {
        /// <summary>Steam not initialised: <b>no Steam call may be made at all</b>. Normal when launched from the exe.</summary>
        Unavailable,

        /// <summary>Steam is up but relay access or authentication has not been asked for yet.</summary>
        NotStarted,

        /// <summary>Still coming up; do not connect yet.</summary>
        Working,

        /// <summary>Relay network and authentication are both available. Safe to connect.</summary>
        Ready,

        /// <summary>Steam says it cannot. <see cref="ISteamService.DescribeStatus"/> has the detail.</summary>
        Failed,
    }

    /// <summary>
    /// The only place the mod asks Steam. Never initialises Steam (the game does; a second
    /// <c>SteamAPI.Init()</c> is undefined). Never calls Steam while <see cref="IsAvailable"/> is false:
    /// that is a native crash with no log.
    /// </summary>
    public interface ISteamService
    {
        /// <summary>False makes every other member inert.</summary>
        bool IsAvailable { get; }

        /// <summary>The local player's SteamID, or 0 when Steam is unavailable.</summary>
        ulong LocalSteamId { get; }

        SteamReadiness Readiness { get; }

        /// <summary>Starts relay and auth init. Idempotent; no-op until Steam is available.</summary>
        void BeginRelayAccess();

        /// <summary>Two native calls: drive from an accumulator, not every frame.</summary>
        void Poll();

        /// <summary>A single line naming the SteamID, relay state and authentication state.</summary>
        string DescribeStatus();
    }
}

namespace MegabonkTogether.Services
{
    /// <summary>
    /// How far along Steam's peer-to-peer prerequisites are.
    ///
    /// <para>Deliberately coarser than <c>ESteamNetworkingAvailability</c>: callers need to decide
    /// whether to connect, whether to wait, or whether to give up and say why. The Steam enum's
    /// nine values collapse to those three plus "Steam is not here at all", which is a separate
    /// case because it is the normal state of an instance launched straight from the exe.</para>
    /// </summary>
    public enum SteamReadiness
    {
        /// <summary>
        /// The game's own Steam is not initialised, so <b>no Steam call may be made at all</b>.
        /// This is not an error: launching <c>Megabonk.exe</c> directly rather than through Steam
        /// produces it every time, and the two-instance test harness depends on being able to.
        /// </summary>
        Unavailable,

        /// <summary>Steam is up but relay access or authentication has not been asked for yet.</summary>
        NotStarted,

        /// <summary>Asked for, still coming up. Connecting now is the stall it exists to avoid.</summary>
        Working,

        /// <summary>Relay network and authentication are both available. Safe to connect.</summary>
        Ready,

        /// <summary>Steam says it cannot. <see cref="ISteamService.DescribeStatus"/> has the detail.</summary>
        Failed,
    }

    /// <summary>
    /// Everything the mod knows about Steam, and the only place it is allowed to ask.
    ///
    /// <para><b>This service never initialises Steam.</b> The game does that itself and pumps
    /// Steamworks.NET's callback dispatcher every frame from <c>SteamManager.Update</c>. Calling
    /// <c>SteamAPI.Init()</c> a second time in the same process is undefined behaviour.</para>
    ///
    /// <para><b>And it never calls Steam while <see cref="IsAvailable"/> is false.</b> Steamworks'
    /// static wrappers resolve an interface pointer that is null before initialisation, and calling
    /// through it is a native access violation, not a managed exception — no stack trace, no log
    /// line, just a dead process. Every entry point here checks first.</para>
    /// </summary>
    public interface ISteamService
    {
        /// <summary>
        /// Whether the game's own Steam initialisation succeeded. False makes every other member
        /// here inert.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>The local player's SteamID, or 0 when Steam is unavailable.</summary>
        ulong LocalSteamId { get; }

        SteamReadiness Readiness { get; }

        /// <summary>
        /// Asks Steam to start fetching the relay network configuration and an authentication
        /// ticket. Idempotent, and a no-op until Steam is available.
        /// </summary>
        void BeginRelayAccess();

        /// <summary>
        /// Re-reads Steam's own availability and moves <see cref="Readiness"/>. Cheap, but it is
        /// two native calls — drive it from an accumulator, not from every frame.
        /// </summary>
        void Poll();

        /// <summary>A single line naming the SteamID, relay state and authentication state.</summary>
        string DescribeStatus();
    }
}

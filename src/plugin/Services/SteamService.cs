using Il2CppInterop.Runtime;
using Steamworks;
using System;
using System.Runtime.InteropServices;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// Phase 2 of the Steamworks migration: get Steam's peer-to-peer prerequisites running and be
    /// able to say whether they are. No transport change, no lobbies — see
    /// <c>docs/steamworks/00-migration-plan.md</c>.
    ///
    /// <para><b>This talks to the game's own Steamworks.NET, not a copy we ship.</b> The game pumps
    /// <c>Steamworks.CallbackDispatcher.RunFrame</c> every frame from <c>SteamManager.Update</c>,
    /// and that dispatcher drains the process's single pipe through
    /// <c>SteamAPI_ManualDispatch_GetNextCallback</c> — a queue that <i>consumes</i>. A second
    /// managed Steamworks.NET would have its own dispatcher over the same pipe and the two would
    /// take each other's callbacks, so the game would intermittently lose its own achievement and
    /// leaderboard results. Both facts are decompiled, not assumed.</para>
    ///
    /// <para>Status arrives through <see cref="SteamCallback"/> — our own <c>Callback</c> injected
    /// into the game's dispatcher — rather than by asking for it.</para>
    ///
    /// <para><b>Nothing here calls a Steamworks method that takes a struct by reference, and that
    /// is a hard rule rather than a preference.</b> <c>GetRelayNetworkStatus</c> and
    /// <c>GetAuthenticationStatus</c> both do, and both are gone. That shape has produced a fatal
    /// <c>AccessViolationException</c> twice on this project: first reading a field off the struct
    /// they fill, then — after that was removed and the call looked safe — merely passing it, on
    /// another player's machine, where the IL2CPP exception it raised took Il2CppInterop's own
    /// exception formatter down with it. It had run fine here dozens of times. The lesson is that
    /// "it works on this install" is not evidence about this shape at all.</para>
    ///
    /// <para>The callbacks deliver the same information as a raw pointer to the native struct,
    /// which <c>Marshal</c> reads at fixed offsets — no struct crosses the boundary. Authentication
    /// is additionally re-read through <c>InitAuthentication</c>, which returns the availability
    /// directly and takes nothing by reference.</para>
    /// </summary>
    internal class SteamService : ISteamService
    {
        /// <summary>
        /// Set once <see cref="BeginRelayAccess"/> has actually reached Steam. Not the same as
        /// "asked for": the first several calls happen before the game's Steam is up and do
        /// nothing, and retrying is the point.
        /// </summary>
        private bool relayAccessRequested;

        private bool loggedReady;
        private bool registeredCallbacks;

        /// <summary>
        /// Held for as long as they are registered. The dispatcher keeps a reference on the IL2CPP
        /// side, and letting the managed wrapper go while that is true is how a callback becomes a
        /// crash later.
        /// </summary>
        private SteamCallback relayStatusCallback;
        private SteamCallback authStatusCallback;

        // Both status structs put m_eAvail first, and the native payload a callback delivers has
        // the same layout there as the managed declaration. SteamRelayNetworkStatus_t carries three
        // more fields we can read for free now that we have the raw pointer.
        private const int AvailOffset = 0;
        private const int PingMeasurementInProgressOffset = 4;
        private const int AvailNetworkConfigOffset = 8;
        private const int AvailAnyRelayOffset = 12;

        private ESteamNetworkingAvailability networkConfigAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;
        private ESteamNetworkingAvailability anyRelayAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;
        private bool pingMeasurementInProgress;

        private ESteamNetworkingAvailability relayAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;
        private ESteamNetworkingAvailability authAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;


        /// <summary>
        /// Latches off the whole service if a Steam call ever throws. One failure means the
        /// assumption that these bind cleanly is wrong, and repeating a call that just failed
        /// across the IL2CPP boundary is how a recoverable error becomes a crash.
        /// </summary>
        private bool disabled;

        public bool IsAvailable
        {
            get
            {
                if (disabled)
                {
                    return false;
                }

                try
                {
                    // The game's own flag, and the exact one it gates SteamAPI.RunCallbacks on —
                    // SteamManager.IsInitialized returns the static byte that SteamManager.Update
                    // tests before pumping. So "true" means Steam is up *and* being serviced.
                    return SteamManager.IsInitialized();
                }
                catch (Exception ex)
                {
                    Disable("SteamManager.IsInitialized", ex);
                    return false;
                }
            }
        }

        public ulong LocalSteamId
        {
            get
            {
                if (!IsAvailable)
                {
                    return 0UL;
                }

                try
                {
                    // The game already resolves this and keeps it in a static, so read that rather
                    // than crossing into Steam for something it has cached.
                    return SteamManager.steamId;
                }
                catch (Exception ex)
                {
                    Disable("SteamManager.steamId", ex);
                    return 0UL;
                }
            }
        }

        public SteamReadiness Readiness
        {
            get
            {
                if (!IsAvailable)
                {
                    return SteamReadiness.Unavailable;
                }

                if (!relayAccessRequested)
                {
                    return SteamReadiness.NotStarted;
                }

                // Both have to be up. Connecting with the relay network ready but authentication
                // still coming fails in a way that reads like a NAT problem, which is a bad hour to
                // spend for the sake of one extra check.
                if (IsFailure(relayAvailability) || IsFailure(authAvailability))
                {
                    return SteamReadiness.Failed;
                }

                return relayAvailability == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current
                    && authAvailability == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current
                    ? SteamReadiness.Ready
                    : SteamReadiness.Working;
            }
        }

        /// <summary>
        /// <para><b>Not called from Plugin.Load, and it cannot be.</b> The plan said "at plugin
        /// startup"; the game's <c>SteamManager</c> initialises from a
        /// <c>RuntimeInitializeOnLoadMethod(AfterSceneLoad)</c>, which runs well after BepInEx
        /// loads plugins. Calling Steam at that point would either do nothing or dereference a null
        /// interface. So this is driven from a ticker that retries until the gate opens, and it is
        /// still "as early as possible" — just measured from when Steam exists rather than from
        /// when we do.</para>
        /// </summary>
        public void BeginRelayAccess()
        {
            if (relayAccessRequested || !IsAvailable)
            {
                return;
            }

            // Registered *before* asking, deliberately. These callbacks report transitions, so
            // subscribing after kicking the process off can miss the one that says it finished.
            RegisterStatusCallbacks();

            try
            {
                // Begins fetching the SDR network configuration and measuring pings to the relay
                // PoPs. Asynchronous: this returns immediately and the callbacks report progress.
                // Skipping it does not break connecting, it just moves the multi-second wait to the
                // first connection attempt, where it looks like a hang.
                SteamNetworkingUtils.InitRelayNetworkAccess();

                // Same shape for the identity side. Connecting before authentication is available
                // fails, and fails looking like a network fault rather than an auth one.
                authAvailability = SteamNetworkingSockets.InitAuthentication();

                relayAccessRequested = true;

                Plugin.Log.LogInfo(
                    $"[steam] Relay access and authentication requested; SteamID {LocalSteamId}.");
            }
            catch (Exception ex)
            {
                Disable("InitRelayNetworkAccess/InitAuthentication", ex);
            }
        }

        public void Poll()
        {
            if (!relayAccessRequested)
            {
                BeginRelayAccess();
                return;
            }

            if (!IsAvailable)
            {
                return;
            }

            // Nothing is fetched here any more. Status arrives through the two registered
            // callbacks; see the class remarks for why polling it was removed outright.
            //
            // Authentication is the one exception, and only because it can be re-read without a
            // by-ref struct: InitAuthentication returns the current availability and is documented
            // as safe to call repeatedly. It is how we notice authentication coming up if its
            // callback never arrives.
            if (authAvailability != ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current)
            {
                try
                {
                    authAvailability = SteamNetworkingSockets.InitAuthentication();
                }
                catch (Exception ex)
                {
                    Disable("InitAuthentication", ex);
                    return;
                }
            }

            // Logged once on arrival rather than on a timer. This is the line that answers "did
            // Phase 2 work" in a log somebody sends us, and it must not be one of two hundred.
            if (!loggedReady && Readiness == SteamReadiness.Ready)
            {
                loggedReady = true;
                Plugin.Log.LogInfo($"[steam] {DescribeStatus()}");
            }
        }

        /// <summary>
        /// Subscribes to the two status callbacks. Once, and latched before the attempt so a
        /// failure is one log line rather than one per tick.
        /// </summary>
        private void RegisterStatusCallbacks()
        {
            if (registeredCallbacks)
            {
                return;
            }

            registeredCallbacks = true;

            try
            {
                relayStatusCallback = new SteamCallback
                {
                    CallbackType = Il2CppType.Of<SteamRelayNetworkStatus_t>(),
                    Handler = OnRelayNetworkStatus,
                };
                CallbackDispatcher.Register(relayStatusCallback);

                authStatusCallback = new SteamCallback
                {
                    CallbackType = Il2CppType.Of<SteamNetAuthenticationStatus_t>(),
                    Handler = OnAuthenticationStatus,
                };
                CallbackDispatcher.Register(authStatusCallback);
            }
            catch (Exception ex)
            {
                relayStatusCallback = null;
                authStatusCallback = null;
                Plugin.Log.LogWarning(
                    $"[steam] Could not register for Steam's status callbacks, so relay readiness "
                    + $"will not be reported: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// <para>The raw payload is the native <c>SteamRelayNetworkStatus_t</c>. Reading it with
        /// <c>Marshal</c> at fixed offsets is the whole point — it is the same information the
        /// by-ref accessor returned, obtained without a struct crossing the interop boundary.</para>
        /// </summary>
        private void OnRelayNetworkStatus(IntPtr payload)
        {
            relayAvailability = (ESteamNetworkingAvailability)Marshal.ReadInt32(payload, AvailOffset);
            pingMeasurementInProgress = Marshal.ReadInt32(payload, PingMeasurementInProgressOffset) != 0;
            networkConfigAvailability = (ESteamNetworkingAvailability)Marshal.ReadInt32(payload, AvailNetworkConfigOffset);
            anyRelayAvailability = (ESteamNetworkingAvailability)Marshal.ReadInt32(payload, AvailAnyRelayOffset);
        }

        private void OnAuthenticationStatus(IntPtr payload)
        {
            authAvailability = (ESteamNetworkingAvailability)Marshal.ReadInt32(payload, AvailOffset);
        }

        public string DescribeStatus()
        {
            if (disabled)
            {
                return "Steam disabled after a failed call; see the earlier error.";
            }

            if (!IsAvailable)
            {
                return "Steam is not initialised. Launched outside Steam? Netplay does not need it yet.";
            }

            return $"SteamID {LocalSteamId}, readiness {Readiness}, "
                + $"relay {Describe(relayAvailability)}, auth {Describe(authAvailability)}, "
                + $"network config {Describe(networkConfigAvailability)}, "
                + $"any relay {Describe(anyRelayAvailability)}"
                + (pingMeasurementInProgress ? ", still measuring pings." : ".");
        }

        /// <summary>
        /// Negative values are Steam's failure range; positive ones are stages of coming up. Only
        /// <c>Current</c> means usable.
        /// </summary>
        private static bool IsFailure(ESteamNetworkingAvailability availability) => (int)availability < 0;

        private static string Describe(ESteamNetworkingAvailability availability) =>
            availability switch
            {
                ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current => "available",
                ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Attempting => "attempting",
                ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Waiting => "waiting",
                ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Retrying => "retrying",
                ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried => "never tried",
                ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Previously => "lost",
                ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Failed => "failed",
                ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_CannotTry => "cannot try",
                _ => $"unknown ({(int)availability})",
            };

        private void Disable(string call, Exception ex)
        {
            if (disabled)
            {
                return;
            }

            disabled = true;
            Plugin.Log.LogError(
                $"[steam] {call} failed, so every Steam call is now disabled: "
                + $"{ex.GetType().Name}: {ex.Message}. Netplay is unaffected — nothing depends on "
                + "Steam yet — but the Steamworks migration cannot proceed until this is understood.");
        }
    }
}

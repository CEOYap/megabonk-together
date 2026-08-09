using Steamworks;
using System;

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
    /// <para>The cost of that choice is that we have no callback registry of our own, so anything
    /// later phases need from a callback has to arrive another way — polling, or a config-value
    /// function pointer. That is written up in the migration plan; it is a real constraint, and it
    /// is still cheaper than breaking the game's Steam integration.</para>
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

        private ESteamNetworkingAvailability relayAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;
        private ESteamNetworkingAvailability authAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;

        // Diagnostics only, from the relay details struct. Nothing gates on them: they exist so a
        // log line can say *which* half of SDR is not up, which is the difference between "Steam is
        // still measuring pings" and "this machine cannot reach a relay at all".
        private ESteamNetworkingAvailability networkConfigAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;
        private ESteamNetworkingAvailability anyRelayAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;
        private bool pingMeasurementInProgress;

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

            try
            {
                // Begins fetching the SDR network configuration and measuring pings to the relay
                // PoPs. Asynchronous: this returns immediately and Poll watches for the result.
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

            try
            {
                // These two out parameters are the riskiest marshalling in this change, and there
                // is no overload without them. Both structs carry a managed byte[] for their debug
                // message, so Il2CppInterop generates them as ValueType-derived proxy classes
                // rather than blittable structs, and an out parameter of one is a shape this
                // project has not exercised before. If it is wrong it will be wrong natively — an
                // access violation, not the exception the catch below is for.
                //
                // The gate reads the return value rather than pDetails.m_eAvail. Steam documents
                // them as the same value, and keeping the decision off the struct means a bad read
                // of the details would degrade the diagnostics rather than the behaviour.
                relayAvailability = SteamNetworkingUtils.GetRelayNetworkStatus(out var relayDetails);
                authAvailability = SteamNetworkingSockets.GetAuthenticationStatus(out _);

                pingMeasurementInProgress = relayDetails.m_bPingMeasurementInProgress != 0;
                networkConfigAvailability = relayDetails.m_eAvailNetworkConfig;
                anyRelayAvailability = relayDetails.m_eAvailAnyRelay;
            }
            catch (Exception ex)
            {
                Disable("GetRelayNetworkStatus/GetAuthenticationStatus", ex);
                return;
            }

            // Logged once on arrival rather than on a timer. This is the line that answers "did
            // Phase 2 work" in a log somebody sends us, and it must not be one of two hundred.
            if (!loggedReady && Readiness == SteamReadiness.Ready)
            {
                loggedReady = true;
                Plugin.Log.LogInfo($"[steam] {DescribeStatus()}");
            }
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

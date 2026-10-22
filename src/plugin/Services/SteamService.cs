using Il2CppInterop.Runtime;
using Steamworks;
using System;
using System.Runtime.InteropServices;

namespace OmniBonk.Services
{
    /// <summary>
    /// Starts Steam's P2P relay and auth prerequisites and reports their status.
    /// <para>Uses the game's own Steamworks.NET (decompiled): its dispatcher consumes the single
    /// callback pipe, so a second copy would steal the game's callbacks.</para>
    /// <para><b>Hard rule: never call a Steamworks method taking a struct by reference.</b> That
    /// shape caused fatal <c>AccessViolationException</c>s twice, once only on another player's
    /// machine. Status comes from callbacks read with <c>Marshal</c> at fixed offsets.</para>
    /// </summary>
    internal class SteamService : ISteamService
    {
        /// <summary>Set once a call actually reached Steam; early calls before Steam is up are retried.</summary>
        private bool relayAccessRequested;

        private bool loggedReady;
        private bool registeredCallbacks;

        /// <summary>Kept alive while registered: the IL2CPP dispatcher holds a reference.</summary>
        private SteamCallback relayStatusCallback;
        private SteamCallback authStatusCallback;

        // Native layout: m_eAvail first in both status structs.
        private const int AvailOffset = 0;
        private const int PingMeasurementInProgressOffset = 4;
        private const int AvailNetworkConfigOffset = 8;
        private const int AvailAnyRelayOffset = 12;

        private ESteamNetworkingAvailability networkConfigAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;
        private ESteamNetworkingAvailability anyRelayAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;
        private bool pingMeasurementInProgress;

        private ESteamNetworkingAvailability relayAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;
        private ESteamNetworkingAvailability authAvailability = ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_NeverTried;


        /// <summary>Latched on any throw: retrying a failed IL2CPP call risks a crash.</summary>
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
                    // The flag SteamManager.Update gates pumping on: up and serviced.
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

                // Both required: missing auth fails like a NAT problem.
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
        /// Retried from a ticker, not Plugin.Load: the game's SteamManager initialises after
        /// plugins load.
        /// </summary>
        public void BeginRelayAccess()
        {
            if (relayAccessRequested || !IsAvailable)
            {
                return;
            }

            // Before asking, or the completion transition can be missed.
            RegisterStatusCallbacks();

            try
            {
                // Async; done early so the first connect does not appear to hang.
                SteamNetworkingUtils.InitRelayNetworkAccess();

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

            // Status comes from callbacks. Auth is re-read too (no by-ref struct, safe to repeat)
            // in case its callback never arrives.
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

            if (!loggedReady && Readiness == SteamReadiness.Ready)
            {
                loggedReady = true;
                Plugin.Log.LogInfo($"[steam] {DescribeStatus()}");
            }
        }

        /// <summary>Latched before the attempt so a failure logs once.</summary>
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

        /// <summary>Native <c>SteamRelayNetworkStatus_t</c>, read at fixed offsets.</summary>
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

        /// <summary>Negative values are Steam's failure range.</summary>
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
                + $"{ex.GetType().Name}: {ex.Message}. Netplay is unaffected - nothing depends on "
                + "Steam yet - but the Steamworks migration cannot proceed until this is understood.");
        }
    }
}

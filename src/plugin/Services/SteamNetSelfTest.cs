namespace MegabonkTogether.Services
{
    /// <summary>
    /// Brings the Steam transport up on its own and tears it down again, so the parts of Phase 4
    /// that do not need a second player stop being assumptions.
    ///
    /// <para><b>What one player can actually prove, which is more than it sounds.</b> Steam
    /// peer-to-peer has no loopback, so no connection can be made here and nothing about the
    /// receive path or the delivery mapping is exercised. What <i>is</i> exercised is every step
    /// that has to succeed before a connection is even attempted, and each of them is a place this
    /// project has been surprised before:</para>
    ///
    /// <list type="number">
    /// <item><b>The identity layout.</b> <c>GetIdentity</c> fills a
    /// <c>SteamNetworkingIdentity</c> and we compare it against the SteamID the game already knows.
    /// This is the one that matters: it is the evidence that passing that struct to
    /// <c>ConnectP2P</c> is safe, and the whole of <c>docs/steamworks/05-interop-struct-shapes.md</c>
    /// turns on it. A mismatch here means stop.</item>
    /// <item><b>The listen socket and poll group bind</b> against the game's own interop assembly,
    /// with an <c>Il2CppStructArray</c> for options rather than a managed array.</item>
    /// <item><b>The connection-status callback registers</b> with the game's dispatcher.
    /// <c>SteamCallback</c> is proven for two status types already, but this is the first use of it
    /// for a type in the sockets callback range, and the callback id is derived from an attribute
    /// on the type — get that wrong and it is filed under the wrong id and silently never
    /// fires.</item>
    /// <item><b>Teardown does not crash</b>, which is where a poll group or socket handle released
    /// in the wrong order would show up.</item>
    /// </list>
    ///
    /// <para>Off by default. It opens a listen socket on Valve's relay network for a few frames and
    /// closes it; nobody can see it and nothing can reach it.</para>
    /// </summary>
    internal class SteamNetSelfTest(ISteamService steamService, ISteamNetTransport transport)
    {
        private enum Step
        {
            NotStarted,
            Listening,
            Done,
        }

        /// <summary>
        /// Frames to sit listening before tearing down. Long enough that the callback pump has run
        /// many times, so a status callback that was going to arrive has had every chance.
        /// </summary>
        private const int FramesToHold = 120;

        private Step step = Step.NotStarted;
        private int heldFrames;

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
                    Plugin.Log.LogInfo("[steam-net] Self-test: bringing the transport up as a host.");

                    // StartHost probes the identity layout itself and refuses to continue if it
                    // does not round-trip, so a pass here covers step 1 as well as step 2.
                    if (!transport.StartHost())
                    {
                        Finish(false, transport.DescribeStatus());
                        return;
                    }

                    Plugin.Log.LogInfo($"[steam-net] Self-test: {transport.DescribeStatus()}");
                    step = Step.Listening;
                    return;

                case Step.Listening:
                    // Polling an empty poll group every frame is exactly what a real session does
                    // between messages, so this is also the cheapest check that the receive call
                    // binds and returns rather than throwing.
                    transport.Poll();

                    if (++heldFrames < FramesToHold)
                    {
                        return;
                    }

                    transport.Shutdown();
                    Finish(true, "listen socket, poll group and status callback all came up");
                    return;
            }
        }

        private void Finish(bool passed, string detail)
        {
            step = Step.Done;

            if (passed)
            {
                Plugin.Log.LogInfo(
                    $"[steam-net] Self-test finished: PASSED — {detail}. This proves the setup path "
                    + "only; no peer connected, so the receive path and the delivery mapping are "
                    + "still unverified.");
                return;
            }

            Plugin.Log.LogWarning($"[steam-net] Self-test finished: FAILED — {detail}");
        }
    }
}

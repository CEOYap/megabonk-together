namespace OmniBonk.Services
{
    /// <summary>
    /// One-player Steam transport self-test (no loopback, so no connection): identity layout
    /// round-trip (a mismatch means stop), listen socket and poll group bind, status callback
    /// registers, teardown does not crash. Off by default.
    /// </summary>
    internal class SteamNetSelfTest(ISteamService steamService, ISteamNetTransport transport)
    {
        private enum Step
        {
            NotStarted,
            Listening,
            Done,
        }

        /// <summary>Frames to listen before teardown, so pending callbacks can arrive.</summary>
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

                    // StartHost also probes the identity layout.
                    if (!transport.StartHost())
                    {
                        Finish(false, transport.DescribeStatus());
                        return;
                    }

                    Plugin.Log.LogInfo($"[steam-net] Self-test: {transport.DescribeStatus()}");
                    step = Step.Listening;
                    return;

                case Step.Listening:
                    // Also checks the receive call binds.
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
                    $"[steam-net] Self-test finished: PASSED - {detail}. This proves the setup path "
                    + "only; no peer connected, so the receive path and the delivery mapping are "
                    + "still unverified.");
                return;
            }

            Plugin.Log.LogWarning($"[steam-net] Self-test finished: FAILED - {detail}");
        }
    }
}

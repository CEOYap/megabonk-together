namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Temporary instrumentation for "the charge shrine model is invisible on non-host peers".
    ///
    /// <para>Decompilation settled the frame of the problem but not its cause.
    /// <c>ChargeShrine$$Start</c> calls <c>meshRenderer.set_enabled(false)</c>, so the model is
    /// hidden by default; <c>ChargeShrine$$OnTriggerEnter</c> is the only thing that ever enables
    /// it, and it returns at its first line when <c>rewardGiven || charging</c>;
    /// <c>ChargeShrine$$Complete</c> sets <c>rewardGiven = true</c>, after which the model can
    /// never come back. Three different failures produce the same silent symptom:</para>
    ///
    /// <list type="number">
    /// <item>the client never receives <c>StartingChargingShrine</c> — no line here at all on the
    /// client while the host logs one;</item>
    /// <item>it receives it but <c>charging</c> is already true, so the trigger no-ops — the
    /// "before" line shows <c>charging=True</c> and the mesh stays off across the call;</item>
    /// <item><c>Complete()</c> runs early on the client — the completion line arrives on the
    /// client well before the host's, with <c>chargeTime</c> or <c>progress</c> disagreeing.</item>
    /// </list>
    ///
    /// <para><b>Delete this file once the bug is attributed.</b> It is a stateless formatter with
    /// no counters and no throttling on purpose: shrine starts happen ~13 times in a full run, so
    /// there is nothing to throttle, and a counter would hide the ordering that decides the
    /// answer.</para>
    /// </summary>
    internal static class ShrineDiagnostics
    {
        /// <summary>
        /// Never throws — this runs from a network receive path and from a Harmony postfix, and a
        /// diagnostic that can abort either one is worse than no diagnostic.
        /// </summary>
        internal static string Describe(ChargeShrine shrine)
        {
            try
            {
                if (shrine == null)
                {
                    return "shrine=<null>";
                }

                var renderer = shrine.meshRenderer;

                return $"charging={shrine.charging} rewardGiven={shrine.rewardGiven} " +
                       $"completed={shrine.completed} progress={shrine.chargeProgress:F3} " +
                       $"currentChargeTime={shrine.currentChargeTime:F2} chargeTime={shrine.chargeTime:F2} " +
                       $"meshRenderer={(renderer == null ? "<null>" : renderer.enabled.ToString())}";
            }
            catch (System.Exception ex)
            {
                return $"describe failed: {ex.GetType().Name}";
            }
        }
    }
}

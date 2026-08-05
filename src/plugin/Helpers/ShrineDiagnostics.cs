using System.Text;
using UnityEngine;

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

        /// <summary>
        /// One-shot dump of the shrine's renderers, for the *second* question this file exists to
        /// answer.
        ///
        /// <para>The first round of instrumentation eliminated every sync explanation:
        /// <c>meshRenderer.enabled</c> was true in all 78 samples across host and client, before
        /// and after each trigger and after each completion, while the model was still invisible on
        /// the client. It also caught a decompilation error — the <c>set_enabled(false)</c> calls in
        /// <c>Start</c> and <c>Complete</c> read as <c>meshRenderer</c> in Ghidra, but
        /// <c>dump.cs</c> puts <c>zoneRenderer</c> at 0x68 and <c>meshRenderer</c> at 0x70, and
        /// Ghidra's applied struct names are known to sit a slot out. Those calls are on the ground
        /// zone, not the model.</para>
        ///
        /// <para>So the renderer is enabled and the mesh is not drawn, and visibility depends on
        /// view angle. That is the signature of frustum culling against bounds that do not sit
        /// where the object does. This prints what the client actually instantiated — every
        /// renderer under the shrine, its enabled state, whether it has a mesh at all, and its
        /// world-space bounds — so it can be diffed against the host's copy of the same shrine.</para>
        ///
        /// <para><b>What to look for:</b> a bounds centre far from the shrine's own position, a
        /// zero-size bounds, a null or empty mesh, a renderer count that differs between peers, or
        /// a localScale of zero. Any of those explains an enabled-but-invisible model; none of them
        /// is a netcode bug.</para>
        /// </summary>
        internal static string DescribeRenderers(ChargeShrine shrine)
        {
            try
            {
                if (shrine == null)
                {
                    return "shrine=<null>";
                }

                var root = shrine.transform;
                var sb = new StringBuilder();

                sb.Append($"pos={root.position} scale={root.lossyScale} name={shrine.gameObject.name}");

                var renderers = shrine.GetComponentsInChildren<Renderer>(true);
                if (renderers == null)
                {
                    sb.Append(" renderers=<null>");
                    return sb.ToString();
                }

                sb.Append($" renderers={renderers.Length}");

                foreach (var renderer in renderers)
                {
                    if (renderer == null)
                    {
                        sb.Append(" | <destroyed>");
                        continue;
                    }

                    var bounds = renderer.bounds;

                    sb.Append($" | {renderer.gameObject.name}: enabled={renderer.enabled}")
                      .Append($" activeInHierarchy={renderer.gameObject.activeInHierarchy}")
                      .Append($" boundsCentre={bounds.center} boundsSize={bounds.size}");

                    var meshFilter = renderer.GetComponent<MeshFilter>();
                    if (meshFilter != null)
                    {
                        sb.Append($" mesh={(meshFilter.sharedMesh == null ? "<null>" : meshFilter.sharedMesh.name)}");
                    }
                }

                return sb.ToString();
            }
            catch (System.Exception ex)
            {
                return $"describe renderers failed: {ex.GetType().Name}";
            }
        }
    }
}

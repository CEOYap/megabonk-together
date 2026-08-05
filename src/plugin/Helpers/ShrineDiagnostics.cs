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
        /// zero-size bounds, a null or empty mesh, or a scale of zero. Any of those explains an
        /// enabled-but-invisible model; none of them is a netcode bug.</para>
        ///
        /// <para><b>No <c>GetComponentsInChildren&lt;T&gt;</c>.</b> The first version used it and
        /// threw <c>MissingMethodException: '!!0[] UnityEngine.Component.GetComponentsInChildren(Boolean)'</c>
        /// on every call — Il2CppInterop does not resolve that generic overload, which the il2cpp
        /// skill lists as a known failure mode. Note also that the surrounding try/catch did not
        /// contain it: MissingMethodException is raised when the method is JIT-compiled, not when
        /// the missing call runs, so the whole method failed to compile and threw at the caller.
        /// A catch cannot protect against a body that will not compile.</para>
        ///
        /// <para>This walks the hierarchy by hand instead, using only <c>transform.childCount</c>,
        /// <c>GetChild</c> and the non-generic-friendly <c>GetComponent&lt;T&gt;</c> that the rest
        /// of the codebase already relies on, plus the two Renderer fields <c>dump.cs</c>
        /// confirms.</para>
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

                sb.Append($"name={shrine.gameObject.name} pos={root.position} scale={root.lossyScale}");

                AppendRenderer(sb, "meshRenderer", shrine.meshRenderer);
                AppendRenderer(sb, "zoneRenderer", shrine.zoneRenderer);

                var runeStone = shrine.runeStone;
                if (runeStone == null)
                {
                    sb.Append(" | runeStone=<null>");
                }
                else
                {
                    sb.Append($" | runeStone pos={runeStone.position} scale={runeStone.lossyScale}")
                      .Append($" active={runeStone.gameObject.activeInHierarchy}")
                      .Append($" underThisShrine={IsUnder(runeStone, root)}")
                      .Append($" ancestry={Ancestry(runeStone)}");
                }

                AppendChildren(sb, root, 0);

                return sb.ToString();
            }
            catch (System.Exception ex)
            {
                return $"describe renderers failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Walks <paramref name="candidate"/>'s parent chain looking for <paramref name="ancestor"/>.
        ///
        /// <para>This is the whole question. <c>GameObject.Instantiate</c> remaps references that
        /// point <i>inside</i> the cloned hierarchy; one pointing outside it survives unremapped and
        /// is shared by every clone. On the client, three shrines at three different root positions
        /// all reported their runeStone at exactly (281.15, 16.60, -67.16), while each shrine's own
        /// zoneRenderer sat correctly on its own root — which is what one shared Transform looks
        /// like. <c>false</c> here on the client and <c>true</c> on the host confirms it.</para>
        ///
        /// <para>Hand-rolled rather than <c>Transform.IsChildOf</c>: <c>childCount</c>,
        /// <c>GetChild</c> and <c>parent</c> are proven to resolve under Il2CppInterop by the
        /// previous run's output, and after <c>GetComponentsInChildren&lt;T&gt;(bool)</c> threw
        /// MissingMethodException there is no reason to spend a playtest finding out whether
        /// another convenience method binds.</para>
        ///
        /// <para>Depth-capped so a cycle or a very deep hierarchy cannot hang the caller.</para>
        /// </summary>
        private static bool IsUnder(Transform candidate, Transform ancestor)
        {
            var current = candidate;

            for (int depth = 0; depth < 32 && current != null; depth++)
            {
                if (current == ancestor)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        /// <summary>
        /// The runeStone's parent chain by name, nearest first. If the reference is shared, this
        /// names the object it actually hangs off — which is where the fix has to look.
        /// </summary>
        private static string Ancestry(Transform node)
        {
            var sb = new StringBuilder();
            var current = node == null ? null : node.parent;

            for (int depth = 0; depth < 4 && current != null; depth++)
            {
                if (depth > 0)
                {
                    sb.Append('/');
                }

                sb.Append(current.gameObject.name);
                current = current.parent;
            }

            return sb.Length == 0 ? "<no parent>" : sb.ToString();
        }

        private static void AppendRenderer(StringBuilder sb, string label, Renderer renderer)
        {
            if (renderer == null)
            {
                sb.Append($" | {label}=<null>");
                return;
            }

            var bounds = renderer.bounds;

            sb.Append($" | {label}: enabled={renderer.enabled}")
              .Append($" active={renderer.gameObject.activeInHierarchy}")
              .Append($" boundsCentre={bounds.center} boundsSize={bounds.size}");
        }

        /// <summary>
        /// Manual two-level walk. Depth is capped because the point is to see what the shrine
        /// prefab is made of, not to dump an arbitrarily deep hierarchy into the log.
        /// </summary>
        private static void AppendChildren(StringBuilder sb, Transform parent, int depth)
        {
            if (parent == null || depth > 1)
            {
                return;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child == null)
                {
                    continue;
                }

                var renderer = child.GetComponent<Renderer>();
                if (renderer != null)
                {
                    AppendRenderer(sb, $"child[{depth}]{child.gameObject.name}", renderer);

                    var meshFilter = child.GetComponent<MeshFilter>();
                    if (meshFilter != null)
                    {
                        sb.Append($" mesh={(meshFilter.sharedMesh == null ? "<null>" : meshFilter.sharedMesh.name)}");
                    }
                }

                AppendChildren(sb, child, depth + 1);
            }
        }
    }
}

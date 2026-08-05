using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Temporary instrumentation for "the charge shrine model is invisible on non-host peers".
    ///
    /// <para><b>What is established.</b> The model is not missing on clients — it is drawn in the
    /// wrong place. Every client shrine's <c>runeStone</c> ends up at exactly
    /// <c>(281.15, 16.60, -67.16)</c> regardless of where its shrine is, while each shrine's own
    /// <c>zoneRenderer</c> stays correct and the mesh keeps valid bounds of the right size. On the
    /// host each stone sits <c>(0, +4.23, 0)</c> above its own shrine. That is why it is invisible
    /// at the shrine and visible only when looking toward that one point.</para>
    ///
    /// <para><b>What is ruled out.</b> Sync (all barrier state matches, the client receives and
    /// applies every start). A shared <c>runeStone</c> reference across clones —
    /// <c>underThisShrine=True</c> on both peers with identical ancestry
    /// <c>Armature/B_EnergyAltar/ChargeShrine(Clone)</c>. The mod's <c>get_position</c> redirect,
    /// which only fires for transforms named <c>"Hips"</c>. Renderer enable state, which is true
    /// throughout on both peers.</para>
    ///
    /// <para><b>The constraint that remains.</b> The stone starts at the correct position on the
    /// client and is displaced later, always to the same point. Something running during the
    /// shrine's life writes it, and writes the same value for every shrine. <c>Armature</c> and
    /// <c>B_EnergyAltar</c> in the ancestry say those bones are animation-driven, which is
    /// consistent with <c>ChargeShrine.Update</c> only ever calling <c>set_localScale</c>.</para>
    ///
    /// <para><b>Delete this file, its call sites, <c>Complete_Postfix</c> and <c>Update_Postfix</c>
    /// together once the bug is attributed.</b></para>
    /// </summary>
    internal static class ShrineDiagnostics
    {
        /// <summary>
        /// Last observed rune-stone world position per shrine, keyed by the shrine's instance id.
        /// Backs the movement detector; see <see cref="SampleForMovement"/>.
        /// </summary>
        private static readonly Dictionary<int, Vector3> lastRuneStonePosition = new Dictionary<int, Vector3>();

        /// <summary>Cleared between sessions so a stale entry cannot suppress the first report.</summary>
        internal static void Reset()
        {
            lastRuneStonePosition.Clear();
        }

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
        /// Full state dump: renderers, the rune stone's local *and* world transform, every link in
        /// the chain between the stone and the shrine root, and the animation components driving
        /// them.
        ///
        /// <para><b>The chain is the point.</b> The stone is a genuine descendant whose world
        /// position is wrong, so the offset enters at exactly one link. Printing localPosition and
        /// world position at every level names that link directly instead of costing another
        /// playtest per guess.</para>
        ///
        /// <para><b>No <c>GetComponentsInChildren&lt;T&gt;</c>.</b> An earlier version used it and
        /// threw <c>MissingMethodException</c> on every call — Il2CppInterop does not resolve that
        /// generic overload. The surrounding try/catch did not contain it either:
        /// MissingMethodException is raised when a method is JIT-compiled, not when the missing
        /// call runs, so the body never executed and the throw surfaced in the caller's frame. That
        /// is why every risky member access below sits in its own small method called from inside a
        /// try — a JIT failure then costs one field, not the whole dump.</para>
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

                sb.Append($"name={shrine.gameObject.name} pos={root.position}")
                  .Append($" localPos={root.localPosition} scale={root.lossyScale}");

                AppendRenderer(sb, "meshRenderer", shrine.meshRenderer);
                AppendRenderer(sb, "zoneRenderer", shrine.zoneRenderer);

                var runeStone = shrine.runeStone;
                if (runeStone == null)
                {
                    sb.Append(" | runeStone=<null>");
                }
                else
                {
                    sb.Append($" | runeStone pos={runeStone.position} localPos={runeStone.localPosition}")
                      .Append($" localScale={runeStone.localScale} lossyScale={runeStone.lossyScale}")
                      .Append($" active={runeStone.gameObject.activeInHierarchy}")
                      .Append($" underThisShrine={IsUnder(runeStone, root)}");

                    AppendChain(sb, runeStone, root);
                }

                AppendAnimators(sb, root);
                AppendChildren(sb, root, 0);

                return sb.ToString();
            }
            catch (System.Exception ex)
            {
                return $"describe renderers failed: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Walks stone -> root printing each link's local and world position. Whichever level shows
        /// an unexpected localPosition is where the displacement enters; every level above it will
        /// look normal and every level below inherits the error.
        /// </summary>
        private static void AppendChain(StringBuilder sb, Transform from, Transform root)
        {
            sb.Append(" | chain:");

            var current = from;

            for (int depth = 0; depth < 12 && current != null; depth++)
            {
                sb.Append($" [{depth}]{current.gameObject.name}")
                  .Append($" localPos={current.localPosition} worldPos={current.position}");

                if (current == root)
                {
                    return;
                }

                current = current.parent;
            }

            sb.Append(" (root not reached)");
        }

        /// <summary>
        /// Animation components on the shrine and its immediate children. A client-side clone being
        /// animated differently from the host's tile-generated shrine is the obvious asymmetry
        /// between the two peers, and bones under an Armature are animation-driven by definition.
        /// </summary>
        private static void AppendAnimators(StringBuilder sb, Transform root)
        {
            AppendAnimator(sb, "root", root);

            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child != null)
                {
                    AppendAnimator(sb, child.gameObject.name, child);
                }
            }
        }

        /// <summary>
        /// Isolated so that a member Il2CppInterop cannot bind kills this one line rather than the
        /// whole dump — the MissingMethodException lesson, applied.
        /// </summary>
        private static void AppendAnimator(StringBuilder sb, string label, Transform node)
        {
            try
            {
                var animator = node.GetComponent<Animator>();
                if (animator == null)
                {
                    return;
                }

                sb.Append($" | animator[{label}]: enabled={animator.enabled}")
                  .Append($" rootMotion={animator.applyRootMotion}")
                  .Append($" speed={animator.speed:F2}")
                  .Append($" controller={AnimatorController(animator)}");
            }
            catch (System.Exception ex)
            {
                sb.Append($" | animator[{label}]=<failed: {ex.GetType().Name}>");
            }
        }

        /// <summary>Its own method: the controller property returns a game object type and is the
        /// most likely member here not to bind.</summary>
        private static string AnimatorController(Animator animator)
        {
            try
            {
                var controller = animator.runtimeAnimatorController;
                return controller == null ? "<null>" : controller.name;
            }
            catch (System.Exception ex)
            {
                return $"<failed: {ex.GetType().Name}>";
            }
        }

        /// <summary>
        /// Called every frame from <c>ChargeShrine.Update</c>'s postfix. Logs only when the rune
        /// stone actually moves, which is what pins the moment of displacement and what is on the
        /// stack when it happens.
        ///
        /// <para>This is the field that would otherwise have cost another playtest: the previous
        /// run established the stone starts correct and is displaced later, but not when or by
        /// what. Silent unless something moves, so it costs one dictionary lookup and one distance
        /// compare per shrine per frame while healthy.</para>
        /// </summary>
        internal static string SampleForMovement(ChargeShrine shrine)
        {
            try
            {
                if (shrine == null)
                {
                    return null;
                }

                var runeStone = shrine.runeStone;
                if (runeStone == null)
                {
                    return null;
                }

                var id = shrine.GetInstanceID();
                var now = runeStone.position;

                if (!lastRuneStonePosition.TryGetValue(id, out var previous))
                {
                    lastRuneStonePosition[id] = now;
                    return null;
                }

                // Squared distance, so a stone drifting by a rounding error does not report. The
                // observed displacement is tens of units; this only has to reject noise.
                if ((now - previous).sqrMagnitude < 0.01f)
                {
                    return null;
                }

                lastRuneStonePosition[id] = now;

                return $"runeStone moved {previous} -> {now} " +
                       $"(delta={(now - previous).magnitude:F2}) localPos={runeStone.localPosition} " +
                       $"shrinePos={shrine.transform.position} progress={shrine.chargeProgress:F3} " +
                       $"charging={shrine.charging} completed={shrine.completed}";
            }
            catch (System.Exception ex)
            {
                return $"movement sample failed: {ex.GetType().Name}";
            }
        }

        /// <summary>
        /// Walks <paramref name="candidate"/>'s parent chain looking for <paramref name="ancestor"/>.
        /// Hand-rolled rather than <c>Transform.IsChildOf</c>: <c>childCount</c>, <c>GetChild</c>
        /// and <c>parent</c> are proven to resolve under Il2CppInterop by a previous run's output.
        /// Depth-capped so a cycle cannot hang the caller.
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

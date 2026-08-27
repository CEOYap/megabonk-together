using UnityEngine;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Puts an interactable that somebody else used onto this peer's own "used / total" counter —
    /// the panel behind the <b>Shrine Counter</b> setting, at
    /// <c>GameUI/GameUI/Debug/EnemyInformation/Shrines</c>.
    ///
    /// <para><b>What it is counting.</b> <c>InteractablesStatus</c> holds a
    /// <c>Dictionary&lt;string, InteractableStatusContainer&gt;</c> keyed by an interactable's debug
    /// name, and each container carries <c>numUsed</c> and <c>numTotal</c> — the two halves of
    /// "Chests 4 / 46". <c>numUsed</c> is raised by the interactable's own interaction path, and
    /// that is the path the mod replaces with a shortcut when replicating somebody else's
    /// interaction: <c>Destroy</c> removes the object and <c>Used</c> writes a log line, so the
    /// remote peer sees the object go and never counts it. Two players splitting a map therefore
    /// finish with two tallies that add up to roughly the right total between them and agree
    /// nowhere. OB-12 in <c>docs/netplay/08-observed-bugs.md</c>.</para>
    ///
    /// <para><b>Why calling the game's own handler rather than incrementing the field.</b>
    /// <c>OnInteractableUse</c> resolves the debug name through the interactable's own virtual
    /// <c>GetDebugName</c>, skips <c>InteractableMicrowave</c> — exactly that one type, by an
    /// explicit <c>GetType()</c> comparison, and NOT "types that have their own handlers", which is
    /// what this comment claimed until 2026-08-17 and is what let a shady guy be counted twice —
    /// and raises
    /// <c>A_InteractableUsed</c> so the panel redraws. Reaching into
    /// <c>interactablesByName</c> directly would reimplement the first two and skip the third.
    /// Decompiled before being called (VA <c>0x18051B1D0</c>, cached under
    /// <c>megabonk-re/decompiled/</c>) and it does exactly and only that.</para>
    ///
    /// <para><b>It is private in the game and public on the proxy.</b> Il2CppInterop generates
    /// public members regardless of the original accessibility, which is what makes this callable
    /// at all. It also means the compiler cannot warn if a future game build renames or drops it —
    /// see the note on the caller about where the <c>try</c> has to go.</para>
    /// </summary>
    internal static class InteractableCounterHelper
    {
        /// <summary>
        /// What this peer's counter read for one interactable, before a replicated interaction was
        /// dispatched. <see cref="Name"/> is captured up front on purpose: the debug name comes off
        /// the live component, and some branches destroy the object before we get to look again.
        /// </summary>
        internal readonly struct Snapshot
        {
            internal Snapshot(string name, int? used)
            {
                Name = name;
                Used = used;
            }

            internal string Name { get; }

            /// <summary>Null when the counter could not be read at all — then nothing is inferred.</summary>
            internal int? Used { get; }
        }

        /// <summary>Reads this peer's <c>numUsed</c> for an interactable, before dispatching to it.</summary>
        internal static Snapshot Take(GameObject interactableObj)
        {
            if (interactableObj == null)
            {
                return new Snapshot(null, null);
            }

            var interactable = ResolveInteractable(interactableObj);
            if (interactable == null)
            {
                return new Snapshot(null, null);
            }

            var name = interactable.GetDebugName();
            var used = ReadUsedCount(name);

            // OB-12. The challenge-shrine row is still out of sync after the counter fix and the
            // reason is not visible offline: InteractablesStatus.Init subscribes to
            // DetectInteractables, BaseInteractable, ChargeShrine, InteractableMicrowave and
            // InteractableShadyGuy -- nothing challenge-specific -- so a challenge shrine counts
            // through the same OnInteractableUse path this helper already calls, and should have
            // worked. Naming the key and its value is what separates "wrong key", "key absent" and
            // "counted but by something else" on the next run. Debug level: one line per replicated
            // interaction, not per frame.
            // Logs the GameObject's name rather than the component's type: GetType() on an
            // Il2CppInterop proxy reports the managed wrapper, which is the trap this branch has
            // paid for more than once, whereas the object name is what it is.
            Plugin.Log.LogDebug(
                $"OB-12: snapshot '{name}' from object '{interactableObj.name}' reads " +
                $"{(used.HasValue ? used.Value.ToString() : "<no key>")}.");

            return new Snapshot(name, used);
        }

        /// <summary>
        /// OB-12. Counts the interactable only if the dispatch did not already count it.
        ///
        /// <para><b>Why measured rather than reasoned about.</b> The <c>Interact</c> branch of
        /// <c>OnReceivedInteractableUsed</c> replays the real interaction on some sub-paths
        /// (<c>microwave.Interact()</c> and friends) and opts out on others (dead player, cannot
        /// afford, cannot interact). Where the real interaction runs, the game raises the counter
        /// itself, so counting unconditionally would double it — turning a counter that reads too
        /// low into one that reads too high. Deciding per sub-branch would work today and would be
        /// silently wrong the next time a sub-branch is added. Comparing the number before and
        /// after needs no such judgement: whatever ran, this counts once.</para>
        ///
        /// <para><b>OB-12's cause has now been corrected three times</b>, and each previous attempt
        /// failed by reasoning about which path executes instead of checking. That is the whole
        /// argument for this shape.</para>
        /// </summary>
        internal static void CountIfDispatchDidNot(GameObject interactableObj, Snapshot before)
        {
            // Nothing readable to compare against — do not guess in either direction.
            if (before.Used == null || string.IsNullOrEmpty(before.Name))
            {
                return;
            }

            var after = ReadUsedCount(before.Name);
            if (after == null || after.Value != before.Used.Value)
            {
                // Either the counter vanished, or the dispatch already counted it. Both mean stop.
                return;
            }

            // The object may have been destroyed by the dispatch. If it was, the game almost
            // certainly counted it too and we would have returned above; reaching here with a dead
            // object is the rare case, and there is no live component to resolve a name from.
            if (interactableObj == null)
            {
                Plugin.Log.LogDebug(
                    $"OB-12: '{before.Name}' was consumed without being counted and is already gone.");
                return;
            }

            Plugin.Log.LogDebug($"OB-12: counting '{before.Name}' -- the dispatch did not.");
            CountAsUsed(interactableObj);
        }

        /// <summary>
        /// Resolves the interactable the same way the dispatch does: the component on the object
        /// itself first, children only as a fallback.
        ///
        /// <para><b>Why this matters.</b> <c>OnReceivedInteractableUsed</c> reaches for its types
        /// with <c>GetComponent&lt;T&gt;()</c>, while this helper used
        /// <c>GetComponentInChildren&lt;BaseInteractable&gt;()</c>. On a prefab whose first
        /// <c>BaseInteractable</c> in the hierarchy is not the one the dispatch acts on, the two
        /// disagree — the snapshot would then watch one row while the count landed on another, which
        /// reads exactly like an interactable that is never counted.</para>
        /// </summary>
        private static BaseInteractable ResolveInteractable(GameObject interactableObj)
        {
            var onSelf = interactableObj.GetComponent<BaseInteractable>();
            return onSelf != null ? onSelf : interactableObj.GetComponentInChildren<BaseInteractable>();
        }

        /// <summary>
        /// Reads <c>numUsed</c> for a debug name, or null if it cannot be read.
        ///
        /// <para><c>interactablesByName</c> is an <c>Il2CppSystem</c> dictionary, so this uses its
        /// own <c>TryGetValue</c> rather than anything LINQ-shaped.</para>
        /// </summary>
        private static int? ReadUsedCount(string debugName)
        {
            if (string.IsNullOrEmpty(debugName))
            {
                return null;
            }

            var byName = InteractablesStatus.interactablesByName;
            if (byName == null)
            {
                return null;
            }

            if (!byName.TryGetValue(debugName, out var container) || container == null)
            {
                return null;
            }

            return container.numUsed;
        }

        /// <summary>
        /// Counts <paramref name="interactableObj"/> as used by this peer. Must be called
        /// <b>before</b> the object is destroyed — the debug name comes off the live component.
        /// </summary>
        internal static void CountAsUsed(GameObject interactableObj)
        {
            if (interactableObj == null)
            {
                return;
            }

            var interactable = interactableObj.GetComponentInChildren<BaseInteractable>();
            if (interactable == null)
            {
                return;
            }

            // success: true — the peer that sent this only broadcasts after its own interaction
            // succeeded, and the host relays with SendToAllClientsExcept(OwnerId), so nobody ever
            // receives their own message back. Every peer reaching here is one that did not
            // perform the interaction and is therefore one that has not counted it.
            InteractablesStatus.OnInteractableUse(interactable, true);
        }
    }
}

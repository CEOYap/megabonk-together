using UnityEngine;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// OB-12: counts a remotely-used interactable on this peer's "used / total" Shrine Counter, since
    /// the replication shortcut (Destroy/Used) skips the game's own count.
    /// <para>Calls the game's <c>OnInteractableUse</c> (decompiled, VA <c>0x18051B1D0</c>): it resolves
    /// the debug name, skips only <c>InteractableMicrowave</c>, and raises <c>A_InteractableUsed</c>.
    /// Private in the game, public on the proxy, so a rename will not be caught by the compiler.</para>
    /// </summary>
    internal static class InteractableCounterHelper
    {
        /// <summary>Counter before dispatch. Name captured early: dispatch may destroy the object.</summary>
        internal readonly struct Snapshot
        {
            internal Snapshot(string name, int? used)
            {
                Name = name;
                Used = used;
            }

            internal string Name { get; }

            /// <summary>Null when unreadable; then nothing is inferred.</summary>
            internal int? Used { get; }
        }

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

            // OB-12: shady guy is counted asynchronously by OnShadyGuyUsed (VA 0x18051B4A0) when its
            // shop closes; Interact() (VA 0x1804CD400) returns false so OnInteractableUse skips it.
            // Counting here too would double it. GetComponentInChildren matches the dispatch; not
            // `is`/`as`, which test the Il2CppInterop wrapper.
            if (interactableObj.GetComponentInChildren<InteractableShadyGuy>() != null)
            {
                Plugin.Log.LogDebug(
                    "OB-12: leaving a shady guy to the game's own OnShadyGuyUsed; its count " +
                    "arrives when the shop window closes, not now.");
                return new Snapshot(null, null);
            }

            var name = interactable.GetDebugName();
            var used = ReadUsedCount(name);

            // OB-12: diagnostic for the challenge-shrine row. Object name, not GetType() (proxy wrapper).
            Plugin.Log.LogDebug(
                $"OB-12: snapshot '{name}' from object '{interactableObj.name}' reads " +
                $"{(used.HasValue ? used.Value.ToString() : "<no key>")}.");

            return new Snapshot(name, used);
        }

        /// <summary>
        /// OB-12: counts only if the dispatch did not. Measured before/after rather than reasoned per
        /// branch, since some branches replay the real interaction and count it themselves.
        /// </summary>
        internal static void CountIfDispatchDidNot(GameObject interactableObj, Snapshot before)
        {
            if (before.Used == null || string.IsNullOrEmpty(before.Name))
            {
                return;
            }

            var after = ReadUsedCount(before.Name);
            if (after == null || after.Value != before.Used.Value)
            {
                return;
            }

            if (interactableObj == null)
            {
                Plugin.Log.LogDebug(
                    $"OB-12: '{before.Name}' was consumed without being counted and is already gone.");
                return;
            }

            Plugin.Log.LogDebug($"OB-12: counting '{before.Name}' -- the dispatch did not.");
            CountAsUsed(interactableObj);
        }

        /// <summary>Self first, then children, matching the dispatch's GetComponent, so both watch the same row.</summary>
        private static BaseInteractable ResolveInteractable(GameObject interactableObj)
        {
            var onSelf = interactableObj.GetComponent<BaseInteractable>();
            return onSelf != null ? onSelf : interactableObj.GetComponentInChildren<BaseInteractable>();
        }

        /// <summary>Null if unreadable. Il2CppSystem dictionary: use its own TryGetValue, not LINQ.</summary>
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

        /// <summary>Call <b>before</b> the object is destroyed; the name comes off the live component.</summary>
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

            // success: true. Senders only broadcast on success and never receive their own message back.
            InteractablesStatus.OnInteractableUse(interactable, true);
        }
    }
}

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
    /// <c>GetDebugName</c>, skips the types that have their own handlers, and raises
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

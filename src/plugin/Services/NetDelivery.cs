namespace OmniBonk.Services
{
    /// <summary>
    /// Transport-independent delivery guarantees, mapped to LiteNetLib or Steam inside the transport.
    /// Policy: <c>docs/netplay/02-delivery-method-reference.md</c>.
    /// <para>Rule: <see cref="Unreliable"/> only if a later message supersedes this one.</para>
    /// <para>Steam has only Reliable (ordered) and Unreliable: <see cref="ReliableUnordered"/>
    /// degrades to ordered, and <see cref="ReliableSequenced"/> has no equivalent, so do not map it
    /// silently.</para>
    /// </summary>
    public enum NetDelivery
    {
        /// <summary>
        /// Does not fragment: past ~1400 bytes the send silently fails. Never for lists, strings or snapshots.
        /// </summary>
        Unreliable,

        /// <summary>Independent one-shot events, e.g. spawns. No head-of-line blocking.</summary>
        ReliableUnordered,

        /// <summary>Paired or sequential events; the safe default.</summary>
        ReliableOrdered,

        /// <summary>Only the newest packet is guaranteed. Never for one-shots.</summary>
        ReliableSequenced,
    }
}

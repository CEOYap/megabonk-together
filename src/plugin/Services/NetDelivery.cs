namespace MegabonkTogether.Services
{
    /// <summary>
    /// Transport-independent delivery guarantees. The mod's own vocabulary for the four channel
    /// semantics it actually relies on, so that gameplay code names a *guarantee* rather than a
    /// LiteNetLib enum member.
    ///
    /// <para><b>Why this exists.</b> Steamworks' <c>k_nSteamNetworkingSend_*</c> flags and
    /// LiteNetLib's <c>DeliveryMethod</c> do not have the same members, and the migration is going
    /// to swap one for the other underneath ~100 call sites. Naming the guarantee at the call site
    /// means those sites do not change again in Phase 4 — only the mapping inside the transport
    /// does. Full policy and the current message→channel map:
    /// <c>docs/netplay/02-delivery-method-reference.md</c>.</para>
    ///
    /// <para><b>The rule these encode:</b> reliability is a correctness property, not a performance
    /// knob. A message may be <see cref="Unreliable"/> only if a later message supersedes it.</para>
    ///
    /// <para><b>Deliberately four members, not five.</b> LiteNetLib also has <c>Sequenced</c>
    /// (best-effort, drops stale, does not fragment). Nothing in this codebase uses it, and an enum
    /// member that exists is an invitation to use it — the delivery-method reference documents it,
    /// which is the right place for a semantic we are not currently buying. Appending a member later
    /// is safe; this is not a wire format.</para>
    ///
    /// <para><b>Two of these four do not survive the migration intact, and that is the point of
    /// naming them now.</b> Steam's send flags
    /// (<see href="https://partner.steamgames.com/doc/api/ISteamNetworkingSockets">ISteamNetworkingSockets</see>,
    /// exposed by <see href="https://github.com/rlabrecque/Steamworks.NET">Steamworks.NET</see> as
    /// <c>k_nSteamNetworkingSend_*</c>) offer exactly two reliability levels —
    /// <c>Reliable</c> (8, always ordered) and <c>Unreliable</c> (0) — plus the <c>NoNagle</c> (1)
    /// and <c>NoDelay</c> (4) modifiers. There is no unordered-reliable and no sequenced channel:</para>
    ///
    /// <list type="table">
    /// <item><term><see cref="Unreliable"/></term><description>→ <c>k_nSteamNetworkingSend_Unreliable</c>. Exact match.</description></item>
    /// <item><term><see cref="ReliableOrdered"/></term><description>→ <c>k_nSteamNetworkingSend_Reliable</c>. Exact match, and Steam fragments it.</description></item>
    /// <item><term><see cref="ReliableUnordered"/></term><description>→ <c>Reliable</c>. <b>Degrades:</b> it gains ordering it did not ask for, so the head-of-line blocking this member exists to avoid comes back. Correct, slower.</description></item>
    /// <item><term><see cref="ReliableSequenced"/></term><description>→ <b>no equivalent.</b> Neither flag reproduces "only the newest is guaranteed". Mapping it to <c>Reliable</c> delivers packets the sender expected to be droppable; mapping it to <c>Unreliable</c> drops the one it expected to arrive.</description></item>
    /// </list>
    ///
    /// <para><b>So do not let Phase 4 pick a mapping silently.</b> Only one send uses
    /// <see cref="ReliableSequenced"/> today — the implicit default on the client's <c>Introduced</c>
    /// handshake — and it is a one-shot that should arguably not be on that channel at all. Settle
    /// that call site before writing the Steam mapping, rather than choosing a lossy translation for
    /// it by default.</para>
    /// </summary>
    public enum NetDelivery
    {
        /// <summary>
        /// Best effort, unordered, <b>does not fragment</b>. High-rate continuous state that the
        /// next tick supersedes. Anything carrying a list, a string or a snapshot must not use this:
        /// past ~1400 bytes an unreliable send silently fails.
        /// </summary>
        Unreliable,

        /// <summary>
        /// Guaranteed, unordered, fragments. Independent one-shot events with no paired sibling —
        /// spawn notifications. Avoids the head-of-line blocking that ordering costs.
        /// </summary>
        ReliableUnordered,

        /// <summary>
        /// Guaranteed, ordered, fragments. Paired or sequential events (start/stop, open/close) and
        /// the safe default for anything whose loss leaves two peers permanently disagreeing.
        /// </summary>
        ReliableOrdered,

        /// <summary>
        /// <b>Only the most recent packet is guaranteed</b>; intermediate ones may be dropped by
        /// design. Periodic state where only the newest matters — and never a one-shot, despite the
        /// name reading like a reliable channel.
        /// </summary>
        ReliableSequenced,
    }
}

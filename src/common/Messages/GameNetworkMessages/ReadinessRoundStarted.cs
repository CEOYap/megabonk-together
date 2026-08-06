using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Host → clients. Opens a lobby-readiness round and tells every peer which round it is.
    ///
    /// <para>This is the message our readiness handshake never had. Readiness used to be a single
    /// unaddressed <see cref="ClientInGameReady"/> from client to host, so a report could not be
    /// attributed to a level transition and the host had no way to say "I am now asking again".
    /// </para>
    ///
    /// <para><b>The stamp is host-assigned, never client-predicted.</b> A client holds
    /// <c>SessionId = 0</c> until it receives one of these, and cannot report before then — so a
    /// report is either for the round the host has open or it is rejected. The alternative, having
    /// each side derive the round number independently, is only sound while no peer ever misses a
    /// transition, and the failsafe paths are exactly where one does.</para>
    /// </summary>
    [MemoryPackable]
    public partial class ReadinessRoundStarted : IGameNetworkMessage
    {
        /// <summary>Non-zero for the lifetime of a run. Zero is "no round".</summary>
        public uint SessionId { get; set; }

        /// <summary>1-based. Zero is "no round", never a valid round.</summary>
        public uint RoundId { get; set; }
    }
}

using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Host to clients: opens a readiness round. The stamp is host-assigned, never client-predicted;
    /// a client cannot report until it has one.
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

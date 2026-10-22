using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Endless Run: the host-chosen room for the next stage change, sent one transition ahead so a
    /// client has it before its own portal fires.
    /// <para><see cref="Sequence"/> counts from 1. <see cref="Room"/> values are wire format: never
    /// renumber <c>EndlessRoom</c>. Reliable (one-shot), ordered after <c>RunStarted</c>.</para>
    /// </summary>
    [MemoryPackable]
    public partial class EndlessNextRoom : IGameNetworkMessage
    {
        public int Sequence { get; set; }
        public int Room { get; set; }
    }
}

using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Endless Run: the room every peer loads at the next stage change. The host owns the route, a
    /// shuffle no client may roll itself, and sends each room one transition ahead: at run start
    /// for the first, and as each transition is applied for the one after. So a client already
    /// holds the room when its own portal fires, which can happen before the host's does.
    ///
    /// <para><see cref="Sequence"/> counts stage changes from 1 within a run.
    /// <see cref="Room"/> is the plugin's <c>EndlessRoom</c> value; those numbers are part of the
    /// wire format and must never be renumbered.</para>
    ///
    /// <para>A one-shot transition, so reliable, and ordered after <c>RunStarted</c>.</para>
    /// </summary>
    [MemoryPackable]
    public partial class EndlessNextRoom : IGameNetworkMessage
    {
        public int Sequence { get; set; }
        public int Room { get; set; }
    }
}

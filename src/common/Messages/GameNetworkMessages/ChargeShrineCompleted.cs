using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// OB-6. The host's decision that a charge shrine has completed.
    ///
    /// <para>Each peer used to fill a shrine's bar on its own and call ChargeShrine.Complete when its
    /// own bar was full, so the reward window opened at a different moment on every machine. Under
    /// shared experience the barrier then made the earlier player wait -- 8 to 15 s, and past the
    /// 20 s failsafe when one peer completed a shrine the other never did. With this message only the
    /// host completes a shrine; clients hold their bar at full until it arrives.</para>
    ///
    /// <para>A one-shot transition, so it must be reliable.</para>
    /// </summary>
    [MemoryPackable]
    public partial class ChargeShrineCompleted : IGameNetworkMessage
    {
        public uint ShrineNetplayId { get; set; }
    }
}

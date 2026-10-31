using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Duo Shrines (<c>LobbySettings.ShrineDuoBoost</c>): the host's count of players in a charge shrine,
    /// and the charge speed that follows from it. Plan: docs/netplay/20-shrine-charge-scaling-plan.md.
    ///
    /// <para>ChargeShrine has no player count (unlike BossLamp's numPlayers), so the game charges a shrine
    /// at one rate however many stand in it. Every peer divides the shrine's required time by
    /// <see cref="SpeedMultiplier"/>, which the host computes so every peer agrees.</para>
    ///
    /// <para>Superseded by the next one for the same shrine, but ReliableOrdered: it must land after the
    /// StartingChargingShrine it follows, whose replayed OnTriggerEnter resets the required time.</para>
    /// </summary>
    [MemoryPackable]
    public partial class ShrineChargersChanged : IGameNetworkMessage
    {
        public uint ShrineNetplayId { get; set; }

        /// <summary>Players in the shrine, uncapped. For logs; the cap is in <see cref="SpeedMultiplier"/>.</summary>
        public byte ChargerCount { get; set; }

        public float SpeedMultiplier { get; set; }
    }
}

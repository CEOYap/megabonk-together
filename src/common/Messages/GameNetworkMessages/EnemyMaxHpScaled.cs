using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// OB-9. An enemy's HP was rescaled *after* it spawned, so the <see cref="SpawnedEnemy"/> message
    /// already sent for it carries a stale value.
    ///
    /// <para><b>Why a separate message and not a field on <see cref="SpawnedEnemy"/>.</b> MemoryPack
    /// serializes positionally, so widening a shipped message corrupts sessions between builds
    /// silently rather than failing — the same hazard as renumbering a union tag. A new append-only
    /// tag is the only safe shape.</para>
    ///
    /// <para><b>Why the spawn message cannot simply be sent later.</b> The scaling is applied by the
    /// *caller* of <c>EnemyManager.SpawnEnemy</c>, and there are five different callers with no
    /// common "done" hook. <c>Enemy.SetSwarmMultiplierHp</c> is the one point they all pass through,
    /// and by then the spawn message has necessarily gone out.</para>
    /// </summary>
    [MemoryPackable]
    public partial class EnemyMaxHpScaled : IGameNetworkMessage
    {
        /// <summary>The mod's netplay enemy id, as allocated by <c>AddSpawnedEnemy</c>.</summary>
        public uint Id { get; set; }

        /// <summary>
        /// The enemy's HP after scaling. Current and maximum are equal here: this is sent from the
        /// scaler, which runs on a freshly spawned enemy before anything can have damaged it.
        /// </summary>
        public float Hp { get; set; }
    }
}

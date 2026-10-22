using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// OB-9. An enemy's HP was rescaled after its <see cref="SpawnedEnemy"/> went out (five callers
    /// scale after SpawnEnemy; <c>SetSwarmMultiplierHp</c> is the common point).
    /// <para>Separate message: MemoryPack is positional, so a new field would break the wire.</para>
    /// </summary>
    [MemoryPackable]
    public partial class EnemyMaxHpScaled : IGameNetworkMessage
    {
        /// <summary>The mod's netplay enemy id, as allocated by <c>AddSpawnedEnemy</c>.</summary>
        public uint Id { get; set; }

        /// <summary>HP after scaling; current equals max on a fresh enemy.</summary>
        public float Hp { get; set; }
    }
}

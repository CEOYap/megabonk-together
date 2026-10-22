using OmniBonk.Common.Models;
using MemoryPack;

namespace OmniBonk.Common.Messages
{
    [MemoryPackable]
    public partial class ProjectilesUpdate : IGameNetworkMessage
    {
        public ICollection<Projectile> Projectiles { get; set; } = new List<Projectile>();
    }
}

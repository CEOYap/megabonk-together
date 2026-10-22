using MemoryPack;

namespace OmniBonk.Common.Models
{
    [MemoryPackable]
    public partial class Projectile
    {
        public uint Id { get; set; }
        public QuantizedVector3 Position = new();
        public QuantizedVector3 FordwardVector = new();
    }
}

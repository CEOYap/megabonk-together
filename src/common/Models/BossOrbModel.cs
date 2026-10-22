using MemoryPack;

namespace OmniBonk.Common.Models
{
    [MemoryPackable]
    public partial class BossOrbModel
    {
        public uint Id { get; set; }

        public QuantizedVector3 Position = new();
    }
}

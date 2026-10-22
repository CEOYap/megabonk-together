using MemoryPack;

namespace OmniBonk.Common.Models
{
    [MemoryPackable]
    public partial class QuantizedRotation
    {
        public ushort QuantizedYaw { get; set; }
    }
}

using MemoryPack;

namespace OmniBonk.Common.Models
{
    [MemoryPackable]
    public partial class QuantizedVector3
    {
        public short QuantizedX { get; set; }
        public short QuantizedY { get; set; }
        public short QuantizedZ { get; set; }
    }
}

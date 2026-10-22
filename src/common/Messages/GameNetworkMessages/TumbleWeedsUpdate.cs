using OmniBonk.Common.Models;
using MemoryPack;

namespace OmniBonk.Common.Messages
{
    [MemoryPackable]
    public partial class TumbleWeedsUpdate : IGameNetworkMessage
    {
        public TumbleWeedModel[] TumbleWeeds { get; set; }
    }
}

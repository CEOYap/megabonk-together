using MemoryPack;

namespace OmniBonk.Common.Messages.WsMessages
{
    [MemoryPackable]
    public partial class GameStarting : IWsMessage
    {
        public uint ConnectionId { get; set; }
    }
}

using MemoryPack;

namespace OmniBonk.Common.Messages.WsMessages
{
    [MemoryPackable]
    public partial class GameStartingResponse : IWsMessage
    {
        public bool IsSuccess { get; set; }
    }
}

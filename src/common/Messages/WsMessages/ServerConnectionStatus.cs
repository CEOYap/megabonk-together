using OmniBonk.Common.Messages.WsMessages;
using MemoryPack;

namespace OmniBonk.Common.Messages
{
    [MemoryPackable]
    public partial class ServerConnectionStatus : IWsMessage
    {
        public bool HasJoined { get; set; }
        public uint ConnectionId { get; set; }
    }
}

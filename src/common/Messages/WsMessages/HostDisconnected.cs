using MemoryPack;

namespace OmniBonk.Common.Messages.WsMessages
{
    [MemoryPackable]
    public partial class HostDisconnected : IWsMessage
    {
        public string RoomCode { get; set; }
        public uint HostConnectionId { get; set; }
    }
}

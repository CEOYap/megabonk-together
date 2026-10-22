using MemoryPack;

namespace OmniBonk.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Host to clients: advance to character selection. Explicit so the host controls when the
    /// lobby ends. No payload: the host has already validated readiness.
    /// </summary>
    [MemoryPackable]
    public partial class LobbyStartRequested : IGameNetworkMessage
    {
    }
}

using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// Host → clients. "Everyone move on to character selection."
    ///
    /// <para><b>Why this is an explicit message and not something a client infers.</b> A client
    /// could watch the lobby-ready set and advance itself the moment the last member becomes ready
    /// — and then the host's Start button would do nothing, because everyone would already have
    /// left. Making the advance an instruction keeps the host in control of when the lobby ends,
    /// which is the point of a host-gated Start.</para>
    ///
    /// <para>Carries no payload. The host has already validated that every member is ready; adding
    /// a stamp here would invite the client to re-derive a decision the host has made, and the
    /// lobby has exactly one of these per lobby.</para>
    /// </summary>
    [MemoryPackable]
    public partial class LobbyStartRequested : IGameNetworkMessage
    {
    }
}

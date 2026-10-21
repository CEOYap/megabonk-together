using MemoryPack;

namespace MegabonkTogether.Common.Messages.GameNetworkMessages
{
    /// <summary>
    /// "This is the wire protocol I speak." A client sends it to the host while the lobby panel is
    /// open, until the host answers with its own. The host answers every one it receives.
    ///
    /// <para><b>Why a message, when P1-3 says the gate belongs in Steam lobby metadata.</b> That
    /// gate exists and still runs, but it covers only the Steam transport. The matchmaker path had
    /// nothing, and it is the path that can pair this mod with an older Megabonk Together build.
    /// The connect-key gate was disproved in game (see <c>docs/netplay/01-critical-fixes.md</c>,
    /// P1-3), so this is checked after connecting instead.</para>
    ///
    /// <para><b>Silence counts as a mismatch, and nothing is disconnected.</b> An older build cannot
    /// read this tag, so it never answers. The host therefore refuses to <i>start</i> the lobby
    /// until every client has sent a compatible hello. That uses no disconnect timer, which P1-3
    /// rejected because a slow link could kill a legitimate player.</para>
    ///
    /// <para>ReliableOrdered: it carries a string, and an answer that is lost would leave the Start
    /// button disabled until the next resend.</para>
    /// </summary>
    [MemoryPackable]
    public partial class ProtocolHello : IGameNetworkMessage
    {
        public uint ConnectionId { get; set; }

        /// <summary><see cref="Protocol.Version"/> of the sender. The only field that is compared.</summary>
        public int ProtocolVersion { get; set; }

        /// <summary>The sender's plugin version, for the log and the notification. Not compared.</summary>
        public string ModVersion { get; set; }
    }
}

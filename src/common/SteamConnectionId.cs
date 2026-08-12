namespace MegabonkTogether.Common
{
    /// <summary>
    /// How a Steam session decides what a peer's <c>ConnectionId</c> is.
    ///
    /// <para><b>Why this needs deciding at all.</b> On the rendezvous path the matchmaking server
    /// hands each peer a random id from a pool and tells everyone about it, so there is exactly one
    /// authority. Steam has no such server, and the ids still have to appear in message bodies and
    /// mean the same thing on every machine.</para>
    ///
    /// <para><b>The id is the Steam account id — the low 32 bits of the SteamID64.</b> That is not a
    /// hash and not a truncation of something that might repeat: Valve's SteamID64 packs the account
    /// id into bits 0-31 and the instance, type and universe above it, so for two accounts on the
    /// public universe the low word differs exactly when the accounts differ. <b>Collisions are
    /// impossible by construction rather than merely unlikely</b>, which is the whole reason to
    /// prefer it over hashing a SteamID into 32 bits — a birthday collision at six players is
    /// vanishingly rare and would be silent and permanent if it ever happened.</para>
    ///
    /// <para><b>This does not repeat the mistake that cost the readiness revert.</b> That failure
    /// was a guard evaluated separately on each machine over two membership sets that were filled by
    /// different mechanisms at different moments, so the ends could disagree about the answer.
    /// This is a pure function of one immutable input that both ends already hold, and it is
    /// deterministic — every machine computing it gets the same answer because there is nothing to
    /// disagree about. The distinction is worth keeping straight: the rule is not "never compute
    /// anything locally", it is "never let two machines *decide* independently".</para>
    ///
    /// <para><b>It is part of the wire contract.</b> Peers exchange these ids and index each other
    /// by them, so changing the derivation would make two builds disagree about who is who while
    /// still handshaking successfully — the same silent-corruption failure mode
    /// <see cref="Protocol.Version"/> exists to prevent. Changing it means bumping that
    /// version.</para>
    /// </summary>
    public static class SteamConnectionId
    {
        /// <summary>
        /// The connection id for a SteamID64, or 0 when the SteamID is not one that can carry a
        /// session.
        ///
        /// <para>Zero is the failure signal because it is already treated as "no id" throughout the
        /// codebase, and because an account id of zero means an unauthenticated or invalid
        /// SteamID — not a peer we could route to anyway. Callers must check.</para>
        /// </summary>
        public static uint FromSteamId(ulong steamId)
        {
            if (steamId == 0UL)
            {
                return 0U;
            }

            return (uint)(steamId & 0xFFFFFFFFUL);
        }
    }
}

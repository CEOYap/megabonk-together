namespace OmniBonk.Common
{
    /// <summary>
    /// A Steam peer's <c>ConnectionId</c>: the account id (low 32 bits of the SteamID64), so it is
    /// collision-free and computed identically on every machine from an immutable input.
    /// <para><b>Wire contract:</b> changing the derivation requires bumping <see cref="Protocol.Version"/>.</para>
    /// </summary>
    public static class SteamConnectionId
    {
        /// <summary>Returns 0 (meaning "no id") for an invalid SteamID; callers must check.</summary>
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

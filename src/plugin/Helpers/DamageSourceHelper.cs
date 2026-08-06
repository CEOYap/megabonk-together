namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Keeps a null out of <c>DamageContainer.damageSource</c> on both sides of the wire.
    ///
    /// <para><b>The defect.</b> <c>RunStats.OnEnemyDamaged</c> (VA <c>0x180411930</c>) looks the
    /// source up in <c>RunStats.damageSources</c>, a <c>Dictionary&lt;string, DamageSource&gt;</c>,
    /// with no null check — <c>TryGetValue(null)</c> then <c>Add(null, …)</c>. A null source
    /// therefore throws <c>ArgumentNullException</c> inside <c>Enemy.Damage</c>, which is a game
    /// method we call directly when applying a remote damage or death event. The exception
    /// propagates out of our handler, so <b>that damage is never applied on this peer</b> and enemy
    /// HP silently diverges from the host.</para>
    ///
    /// <para>Observed once in a two-player session as
    /// <c>[Error :Il2CppInterop] During invoking native-&gt;managed trampoline</c> carrying the full
    /// IL2CPP stack. Rare, but silent and cumulative when it happens.</para>
    ///
    /// <para><b>Why <see cref="string.Empty"/> rather than a named sentinel.</b> The value becomes a
    /// key in the game's own run-stats dictionary and can surface in its UI, so it should not be a
    /// word we invented. Empty is also behaviour-preserving for our one gate on this field —
    /// <c>EnemyPatch.AllowedDamageSource</c> is built from <c>EItem</c> names, so it rejects
    /// <c>""</c> exactly as it rejected <c>null</c> — and it matches the ad-hoc guard that already
    /// existed at one of the send sites.</para>
    ///
    /// <para><b>Applied at both send and receive, deliberately.</b> Normalising on send keeps nulls
    /// off the wire; normalising on receive means a peer running an older build, which still sends
    /// them, cannot make us throw. Cross-version peers handshake in this project, so the receiving
    /// guard is the load-bearing one.</para>
    /// </summary>
    internal static class DamageSourceHelper
    {
        /// <summary>Returns a source string that is safe to hand to game code.</summary>
        internal static string Normalize(string damageSource)
        {
            return damageSource ?? string.Empty;
        }
    }
}

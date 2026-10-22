namespace OmniBonk.Helpers
{
    /// <summary>
    /// Keeps null out of <c>DamageContainer.damageSource</c>: <c>RunStats.OnEnemyDamaged</c>
    /// (VA <c>0x180411930</c>) adds it as a dictionary key unchecked, so null throws inside
    /// <c>Enemy.Damage</c> and the damage is never applied. Empty string, since it can show in the
    /// game's UI. Applied on send and receive (older peers still send null).
    /// </summary>
    internal static class DamageSourceHelper
    {
        internal static string Normalize(string damageSource)
        {
            return damageSource ?? string.Empty;
        }
    }
}

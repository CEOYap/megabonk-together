namespace OmniBonk.Helpers
{
    /// <summary>
    /// OB-63 check: a client counts the replayed hits it no longer forwards to the host, by damage
    /// source. Before the fix each of these was applied a second time on the host. Logged at most every
    /// 30 s while counts change, and at session reset. Main thread only.
    /// </summary>
    internal static class ReplayedHitDiagnostics
    {
        private static readonly System.Collections.Generic.Dictionary<string, int> suppressed = new();
        private static float nextLogTime;

        internal static void Suppressed(string damageSource)
        {
            var key = damageSource ?? string.Empty;
            suppressed.TryGetValue(key, out var n);
            suppressed[key] = n + 1;

            var now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextLogTime)
            {
                return;
            }

            nextLogTime = now + 30f;
            Log();
        }

        internal static void Reset()
        {
            if (suppressed.Count > 0)
            {
                Log();
            }

            suppressed.Clear();
        }

        private static void Log()
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var kv in suppressed)
            {
                parts.Add($"{kv.Key} {kv.Value}");
            }

            Plugin.Log.LogInfo($"[dmg-fwd] replayed hits not sent back to the host: {string.Join(", ", parts)}.");
        }
    }
}

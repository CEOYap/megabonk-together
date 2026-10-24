namespace OmniBonk.Helpers
{
    /// <summary>
    /// OB-61 check: the host sums <c>WeaponAttack.GetSize()</c> over the projectiles it sends, a client
    /// over the copies it sizes. Equal counts and sums mean the client applied the host's sizes.
    /// Logged at most every 30 s while counts change, and at session reset. Main thread only.
    /// </summary>
    internal static class ProjectileSizeDiagnostics
    {
        private static int hostCount;
        private static double hostSum;
        private static int appliedCount;
        private static double appliedSum;
        private static int hostResized;
        private static int appliedResized;
        private static float nextLogTime;

        internal static void Host(float size)
        {
            hostCount++;
            hostSum += size;
            if (size != 1f)
            {
                hostResized++;
            }

            MaybeLog();
        }

        internal static void Applied(float size)
        {
            appliedCount++;
            appliedSum += size;
            if (size != 1f)
            {
                appliedResized++;
            }

            MaybeLog();
        }

        internal static void Reset()
        {
            if (hostCount + appliedCount > 0)
            {
                Log();
            }

            hostCount = appliedCount = hostResized = appliedResized = 0;
            hostSum = appliedSum = 0;
        }

        private static void MaybeLog()
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextLogTime)
            {
                return;
            }

            nextLogTime = now + 30f;
            Log();
        }

        private static void Log()
        {
            Plugin.Log.LogInfo(
                $"[size] session so far: host sent {hostCount} projectile(s), size sum {hostSum:F2}, {hostResized} not 1; " +
                $"sized {appliedCount} received, size sum {appliedSum:F2}, {appliedResized} not 1.");
        }
    }
}

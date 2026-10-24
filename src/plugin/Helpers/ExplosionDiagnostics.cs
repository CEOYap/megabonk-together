namespace OmniBonk.Helpers
{
    /// <summary>
    /// OB-58 check: the host counts mines that exploded and rockets that finished; a client counts the
    /// explosions it draws for received ones. Over a run the two should match. Logged at most every
    /// 30 s while counts change, and once more when the session resets. Main thread only.
    /// </summary>
    internal static class ExplosionDiagnostics
    {
        private static int hostMines;
        private static int hostRockets;
        private static int drawnMines;
        private static int drawnRockets;
        private static float nextLogTime;

        internal static void HostMineExploded() => Bump(ref hostMines);

        internal static void HostRocketDone() => Bump(ref hostRockets);

        internal static void DrewMine() => Bump(ref drawnMines);

        internal static void DrewRocket() => Bump(ref drawnRockets);

        internal static void Reset()
        {
            if (hostMines + hostRockets + drawnMines + drawnRockets > 0)
            {
                Log();
            }

            hostMines = hostRockets = drawnMines = drawnRockets = 0;
        }

        private static void Bump(ref int counter)
        {
            counter++;

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
                $"[explosions] session so far: host exploded {hostMines} mine(s), finished {hostRockets} rocket(s); " +
                $"drew {drawnMines} mine and {drawnRockets} rocket explosion(s) for received ones.");
        }
    }
}

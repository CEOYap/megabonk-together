namespace OmniBonk.Helpers
{
    /// <summary>
    /// OB-58 / OB-59 check: the host counts mines that exploded, rockets that finished, flasks that
    /// exploded and Bluetooth hits; a client counts the explosions it draws for received ones, and the
    /// local flask and Bluetooth hits it refused on received copies. Over a run the host and drawn
    /// counts should match. Logged at most every 30 s while counts change, and once more when the
    /// session resets. Main thread only.
    /// </summary>
    internal static class ExplosionDiagnostics
    {
        private static int hostMines;
        private static int hostRockets;
        private static int hostFlasks;
        private static int hostBluetoothHits;
        private static int drawnMines;
        private static int drawnRockets;
        private static int drawnFlasks;
        private static int missedFlasks;
        private static int blockedFlaskHits;
        private static int blockedBluetoothHits;
        private static float nextLogTime;

        internal static void HostMineExploded() => Bump(ref hostMines);

        internal static void HostRocketDone() => Bump(ref hostRockets);

        internal static void HostFlaskExploded() => Bump(ref hostFlasks);

        internal static void HostBluetoothHit() => Bump(ref hostBluetoothHits);

        internal static void DrewMine() => Bump(ref drawnMines);

        internal static void DrewRocket() => Bump(ref drawnRockets);

        internal static void DrewFlask() => Bump(ref drawnFlasks);

        /// <summary>A host flask explosion arrived after the client's copy was gone, so nothing was drawn.</summary>
        internal static void MissedFlask() => Bump(ref missedFlasks);

        internal static void BlockedFlaskHit() => Bump(ref blockedFlaskHits);

        internal static void BlockedBluetoothHit() => Bump(ref blockedBluetoothHits);

        internal static void Reset()
        {
            if (hostMines + hostRockets + hostFlasks + hostBluetoothHits + drawnMines + drawnRockets
                + drawnFlasks + missedFlasks + blockedFlaskHits + blockedBluetoothHits > 0)
            {
                Log();
            }

            hostMines = hostRockets = hostFlasks = hostBluetoothHits = 0;
            drawnMines = drawnRockets = drawnFlasks = missedFlasks = blockedFlaskHits = blockedBluetoothHits = 0;
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

            if (hostFlasks + hostBluetoothHits + drawnFlasks + missedFlasks + blockedFlaskHits + blockedBluetoothHits > 0)
            {
                Plugin.Log.LogInfo(
                    $"[explosions] flasks: host exploded {hostFlasks}, drew {drawnFlasks} for received ones, " +
                    $"{missedFlasks} arrived after the copy was gone, refused {blockedFlaskHits} local hit call(s); " +
                    $"bluetooth: host hit {hostBluetoothHits}, refused {blockedBluetoothHits} local HitTarget call(s).");
            }
        }
    }
}

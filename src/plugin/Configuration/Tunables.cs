namespace OmniBonk.Configuration
{
    /// <summary>
    /// Tunable numbers that trade bandwidth, CPU, smoothness or fairness.
    /// <para><c>const</c>, not <see cref="ModConfig"/>: folded by the JIT in hot loops, and player edits
    /// would cause hangs and desyncs. All are sender-side, so none changes the wire format.</para>
    /// </summary>
    internal static class Tunables
    {
        #region Send rates

        /// <summary>Own player state to host, Hz. Unreliable, superseded each tick.</summary>
        internal const float LOBBY_UPDATE_TICK_RATE = 60f;

        /// <summary>
        /// Host enemy delta broadcast, Hz. The dominant bandwidth cost (budget x rate, per client).
        /// Also the <c>EnemyDamageAggregator</c> flush cadence. Try <see cref="ENEMY_POSITION_THRESHOLD"/> first.
        /// </summary>
        internal const float ENEMY_UPDATE_TICK_RATE = 40f;

        /// <summary>Host projectile snapshots, Hz.</summary>
        internal const float PROJECTILE_UPDATE_TICK_RATE = 20f;

        /// <summary>Host tumbleweed state, Hz (Desert only).</summary>
        internal const float TUMBLEWEED_UPDATE_TICK_RATE = 20f;

        #endregion

        #region Enemy delta stream

        /// <summary>Min move, world units, to include an enemy in the delta. Cheapest, risk-free bandwidth lever.</summary>
        internal const float ENEMY_POSITION_THRESHOLD = 0.1f;

        /// <summary>Min rotation, degrees, to include an enemy in the delta.</summary>
        internal const float ENEMY_YAW_THRESHOLD = 5.0f;

        /// <summary>Min HP change to include an enemy. Do not raise: suppressed changes look like enemies that won't die.</summary>
        internal const ushort ENEMY_HP_THRESHOLD = 1;

        /// <summary>Measured serialized size of one <c>EnemyModel</c>, bytes. Re-measure if its shape changes.</summary>
        internal const int ENEMY_MODEL_WIRE_BYTES = 19;

        /// <summary>Byte budget for one enemy tick, before splitting.</summary>
        internal const int MAX_ENEMY_TICK_BYTES = 3600;

        /// <summary>Derived enemy cap per tick (currently 189), regardless of how many are alive.</summary>
        internal const int MAX_ENEMIES_PER_TICK = MAX_ENEMY_TICK_BYTES / ENEMY_MODEL_WIRE_BYTES;

        /// <summary>Ticks before an unchanged enemy is re-sent. Correctness bound for lost unreliable packets.</summary>
        internal const int ENEMY_REFRESH_PERIOD_TICKS = 40;

        /// <summary>Queued enemy spawns a client applies per frame.</summary>
        internal const int MAX_ENEMY_SPAWNS_PER_FRAME = 32;

        #endregion

        #region Transport

        /// <summary>
        /// Max payload per datagram before splitting, bytes. Keep below MTU: unreliable sends over it
        /// are silently lost (LiteNetLib fragments reliable only).
        /// </summary>
        internal const int MAX_PACKET_SIZE_BYTES = 1000;

        /// <summary>Steam transport promotes unreliable sends above this to reliable, bytes. Backstop; logs when it fires.</summary>
        internal const int MAX_UNRELIABLE_BYTES = 1200;

        /// <summary>
        /// Steam per-connection send rate, bytes/s, set as both <c>SendRateMin</c> and <c>SendRateMax</c>.
        /// Steam does not estimate bandwidth, so this is the pacing rate, not a target; its header documents
        /// a 256 KB/s default, which a 479-enemy swarm's host exceeded (docs/netplay/04-performance-and-gc.md).
        /// A ceiling on bursts, not added usage. <b>UNVERIFIED</b> in play; the per-connection log line says what Steam applied.
        /// </summary>
        internal const int STEAM_SEND_RATE_BYTES_PER_SECOND = 512 * 1024;

        #endregion

        #region Distance throttling

        /// <summary>Within this distance an entity updates every tick.</summary>
        internal const float DISTANCE_CLOSE = 25f;

        /// <summary>
        /// Beyond this an entity is <c>Far</c>: client drops updates but still draws it (OB-30).
        /// Between this and <see cref="DISTANCE_CLOSE"/> it is <c>Medium</c>.
        /// </summary>
        internal const float DISTANCE_MEDIUM = 60f;

        /// <summary><c>Medium</c>-distance update interval, seconds.</summary>
        internal const float MEDIUM_DISTANCE_UPDATE_INTERVAL_SECONDS = 1f;

        #endregion

        #region Session timing

        /// <summary>Delay between readiness re-reports, seconds. Needed even on clean links.</summary>
        internal const float READY_RETRY_SECONDS = 2f;

        /// <summary>Readiness re-report attempts. Lowering this turns hiccups into hangs.</summary>
        internal const int READY_RETRY_ATTEMPTS = 15;

        #endregion

        #region Spawn fairness (OB-11)

        /// <summary>
        /// Min re-rolled miniboss ring radius, world units; covers the fallback-to-own-position case.
        /// <b>UNVERIFIED</b> — the game's own distances live in constants that were not decompiled.
        /// </summary>
        internal const float MIN_BOSS_RING_RADIUS = 20f;

        /// <summary>Max ring radius, world units. Coffin spawns otherwise derive radii of hundreds.</summary>
        internal const float MAX_BOSS_RING_RADIUS = 60f;

        /// <summary>Ring width as a fraction of radius.</summary>
        internal const float BOSS_RING_BAND = 0.35f;

        /// <summary>Sanity bound on world coordinates; rejects free-falling avatars as anchors.</summary>
        internal const float MAX_PLAUSIBLE_WORLD_COORDINATE = 100000f;

        /// <summary>Placement attempts; matches what the game passes.</summary>
        internal const int BOSS_RING_ATTEMPTS = 50;

        #endregion

        #region Balance

        /// <summary>Extra enemy HP per additional player, fraction. Read on both peers, so builds must match.</summary>
        internal const float HP_SCALING_PER_ADDITIONAL_PLAYER = 0.1f;

        #endregion

        #region Diagnostics

        /// <summary>Bandwidth/allocation report cadence, seconds.</summary>
        internal const float DIAGNOSTIC_REPORT_INTERVAL_SECONDS = 10f;

        #endregion

        #region Endless Run scaling (Phase 4)

        /// <summary>Compounding enemy HP/damage multipliers per boss loop cleared. All peers must run the same build.</summary>
        internal const float ENDLESS_LOOP_HP_MULTIPLIER = 1.5f;
        internal const float ENDLESS_LOOP_DAMAGE_MULTIPLIER = 1.25f;

        #endregion
    }
}

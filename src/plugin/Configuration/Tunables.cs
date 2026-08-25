namespace MegabonkTogether.Configuration
{
    /// <summary>
    /// Every number in the mod that is worth tuning, in one place.
    ///
    /// <para><b>What belongs here.</b> A value you might deliberately change to trade bandwidth,
    /// CPU, smoothness or fairness against each other. What does <i>not</i> belong here: UI layout
    /// numbers (<c>NetPlayerCard</c>, <c>NetPlayersDisplayer</c>, <c>LobbyPanel</c>), Steam struct
    /// field offsets (<c>SteamService</c>, <c>SteamNetLayout</c>) and disconnect reason codes. Those
    /// are implementation details that happen to be numbers — collecting them here would bury the
    /// dozen values that actually matter under fifty that do not.</para>
    ///
    /// <para><b>Why <c>const</c> and not <see cref="ModConfig"/>.</b> These are compile-time
    /// constants on purpose, and moving them into the player-facing <c>.cfg</c> would be a mistake
    /// for three reasons. The JIT folds a <c>const</c>; a <c>ConfigEntry&lt;T&gt;.Value</c> read is
    /// a live property access, and several of these sit in a 40 Hz send loop. A player who lowers
    /// <see cref="READY_RETRY_ATTEMPTS"/> or raises a tick rate produces hangs and desyncs that
    /// arrive as unreproducible bug reports. And <c>ModConfig</c> is a user-settings surface —
    /// renaming or restructuring it silently resets everyone's settings, which is a cost worth
    /// paying only for things a player should genuinely choose.</para>
    ///
    /// <para><b>Read the annotations before changing anything.</b> Each value says which way is
    /// cheaper and what it trades against. Two things that are never safe: raising a value into a
    /// larger payload without checking the MTU note on <see cref="MAX_PACKET_SIZE_BYTES"/>, and
    /// assuming a change is measurable at two players when it scales with player count.</para>
    ///
    /// <para><b>Nothing here changes the wire format.</b> MemoryPack union tags are append-only and
    /// live in <c>src/common/Messages/GameNetworkMessages/GameNetworkMessage.cs</c>. No value in
    /// this file can be changed in a way that makes two peers disagree about how to <i>decode</i> a
    /// message — every one of them is a sender-side decision, which is what makes them safe to
    /// tune without a protocol bump.</para>
    /// </summary>
    internal static class Tunables
    {
        #region Send rates

        /// <summary>
        /// How often a peer sends its own player state to the host, in Hz.
        ///
        /// <para>Client-to-host, unreliable, superseded every tick. The most frequent stream in the
        /// mod but also the smallest — one player, not N enemies. Lowering it makes remote players
        /// visibly steppier before it saves meaningful bandwidth; <c>PlayerInterpolator</c> already
        /// smooths between samples.</para>
        /// </summary>
        internal const float LOBBY_UPDATE_TICK_RATE = 60f;

        /// <summary>
        /// How often the host broadcasts the enemy delta, in Hz.
        ///
        /// <para><b>The dominant cost in the mod.</b> Combined with
        /// <see cref="MAX_ENEMY_TICK_BYTES"/> this sets the bandwidth ceiling: 3600 B x 40 Hz is
        /// ~144 KB/s, and <c>SendToAllClients</c> sends that to <i>each</i> client — so the host's
        /// upload is ~144 KB/s at two players and ~720 KB/s at six.</para>
        ///
        /// <para>Lowering this is the bluntest bandwidth lever and costs smoothness everywhere at
        /// once. Prefer <see cref="ENEMY_POSITION_THRESHOLD"/> first: it sheds the enemies that are
        /// barely moving instead of degrading all of them.</para>
        /// </summary>
        internal const float ENEMY_UPDATE_TICK_RATE = 40f;

        /// <summary>How often the host broadcasts projectile snapshots, in Hz. Host only.</summary>
        internal const float PROJECTILE_UPDATE_TICK_RATE = 20f;

        /// <summary>How often the host broadcasts tumbleweed state, in Hz. Host only, Desert only.</summary>
        internal const float TUMBLEWEED_UPDATE_TICK_RATE = 20f;

        #endregion

        #region Enemy delta stream

        /// <summary>
        /// How far an enemy must move, in world units, to be included in the delta.
        ///
        /// <para><b>The cheapest bandwidth lever available, and the one to try first.</b> Raising it
        /// carries <i>zero</i> correctness risk: it is a sender-side filter, clients interpolate
        /// between whatever they receive, and <see cref="ENEMY_REFRESH_PERIOD_TICKS"/> bounds how
        /// stale a skipped enemy can get regardless. The only cost is visual smoothness.</para>
        ///
        /// <para>At 0.1 and swarm density essentially every enemy qualifies every tick, so the
        /// "delta" is effectively a full snapshot. It is also measurable at two players, which
        /// makes it the one performance change you can actually validate in-house — unlike per-peer
        /// culling, whose benefit only appears at player counts you cannot test.</para>
        /// </summary>
        internal const float ENEMY_POSITION_THRESHOLD = 0.1f;

        /// <summary>How far an enemy must rotate, in degrees, to be included in the delta.</summary>
        internal const float ENEMY_YAW_THRESHOLD = 5.0f;

        /// <summary>
        /// How much an enemy's HP must change to be included in the delta.
        ///
        /// <para>Lowest meaningful value. Do not raise this to save bandwidth: HP drives the health
        /// bar and the death transition, and a suppressed change reads as an enemy that will not
        /// die.</para>
        /// </summary>
        internal const ushort ENEMY_HP_THRESHOLD = 1;

        /// <summary>
        /// Serialized size of one <c>EnemyModel</c>, in bytes.
        ///
        /// <para><b>This is a measurement, not a preference.</b> It only exists to turn
        /// <see cref="MAX_ENEMY_TICK_BYTES"/> into a count. If the shape of <c>EnemyModel</c>
        /// changes, this must be re-measured or the byte budget silently stops meaning what it
        /// says.</para>
        ///
        /// <para><b>Corrected from 15 to 19 on 2026-08-17, and the old value is exactly the failure
        /// this warns about.</b> A final-swarm capture put the enemy stream at 166–181 KB/s, which
        /// at the 40 Hz tick is 4,250–4,644 B per tick against a stated budget of
        /// <see cref="MAX_ENEMY_TICK_BYTES"/> = 3,600. Dividing by the 240 models the old constant
        /// allowed gives 17.7–19.3 B actually on the wire per model. The budget was therefore
        /// overshooting by about a quarter while reporting that it was holding — and the same
        /// capture logged 63 <c>k_EResultLimitExceeded</c> send failures on the host, on a link at
        /// 100% quality.</para>
        ///
        /// <para>At 19 the tick lands near 3,600 B as intended and
        /// <see cref="MAX_ENEMIES_PER_TICK"/> drops from 240 to 189.</para>
        /// </summary>
        internal const int ENEMY_MODEL_WIRE_BYTES = 19;

        /// <summary>
        /// Byte ceiling for one enemy tick, before splitting.
        ///
        /// <para>The budget that keeps a swarm from producing an unbounded packet. See
        /// <see cref="ENEMY_UPDATE_TICK_RATE"/> for what this multiplies out to.</para>
        /// </summary>
        internal const int MAX_ENEMY_TICK_BYTES = 3600;

        /// <summary>
        /// Derived: how many enemies fit in one tick's budget. Currently 189.
        ///
        /// <para><b>This caps the stream regardless of how many enemies are alive</b>, which is why
        /// allocation predictions based on raw enemy count are wrong — see
        /// <c>Services/AllocationDiagnostics</c>.</para>
        /// </summary>
        internal const int MAX_ENEMIES_PER_TICK = MAX_ENEMY_TICK_BYTES / ENEMY_MODEL_WIRE_BYTES;

        /// <summary>
        /// How many ticks before an unchanged enemy is re-sent anyway.
        ///
        /// <para><b>This is a correctness bound, not an optimisation.</b> These sends are
        /// fire-and-forget on an unreliable channel, so without a periodic refresh an enemy whose
        /// final packet was lost stays wrong on that client forever. Raising it widens that window;
        /// lowering it costs bandwidth. At 40 Hz the current value is a one-second ceiling on
        /// staleness.</para>
        /// </summary>
        internal const int ENEMY_REFRESH_PERIOD_TICKS = 40;

        /// <summary>
        /// How many queued enemy spawns a client applies per frame.
        ///
        /// <para>Spreads a burst of spawns across frames instead of hitching on one. Raising it
        /// makes a swarm's arrival sharper and its frame spike worse.</para>
        /// </summary>
        internal const int MAX_ENEMY_SPAWNS_PER_FRAME = 32;

        #endregion

        #region Transport

        /// <summary>
        /// Largest payload the sender will put in one datagram before splitting, in bytes.
        ///
        /// <para><b>Do not raise this to the MTU.</b> It is deliberately below it: LiteNetLib
        /// fragments <i>reliable</i> channels only, so an unreliable send above the real MTU does
        /// not fragment and does not error — it simply never arrives. The margin covers transport
        /// and IP header overhead that is not visible from here.</para>
        /// </summary>
        internal const int MAX_PACKET_SIZE_BYTES = 1000;

        /// <summary>
        /// Payload size above which the Steam transport promotes an unreliable send to reliable.
        ///
        /// <para>A backstop against a future oversized message, not a live threshold — nothing
        /// currently reaches it, because the per-entity streams are already split to
        /// <see cref="MAX_PACKET_SIZE_BYTES"/> and the two sends that bypass the splitter are
        /// bounded by shape. It logs when it fires, so a run that never prints it is the confirming
        /// result rather than a missing one.</para>
        /// </summary>
        internal const int MAX_UNRELIABLE_BYTES = 1200;

        #endregion

        #region Distance throttling

        /// <summary>Within this distance of the local player, an entity updates every tick.</summary>
        internal const float DISTANCE_CLOSE = 25f;

        /// <summary>
        /// Beyond this distance an entity is <c>Far</c>: updates are dropped and its renderer is
        /// disabled. Between this and <see cref="DISTANCE_CLOSE"/> it is <c>Medium</c> and throttled
        /// to <see cref="MEDIUM_DISTANCE_UPDATE_INTERVAL_SECONDS"/>.
        /// </summary>
        internal const float DISTANCE_MEDIUM = 60f;

        /// <summary>How often a <c>Medium</c>-distance entity updates, in seconds.</summary>
        internal const float MEDIUM_DISTANCE_UPDATE_INTERVAL_SECONDS = 1f;

        #endregion

        #region Session timing

        /// <summary>
        /// Delay between readiness re-reports, in seconds.
        ///
        /// <para>The retry is not decorative: a clean-link run needed attempt 2 before the host
        /// acknowledged round 2, with quality at 99–100%. A first readiness report can be lost or
        /// can race the round opening with no packet loss at all.</para>
        /// </summary>
        internal const float READY_RETRY_SECONDS = 2f;

        /// <summary>
        /// How many times a peer re-reports readiness before giving up.
        ///
        /// <para><b>Lowering this is how you turn a recoverable hiccup into a permanent hang.</b>
        /// The readiness barrier is the path this project's history records as having broken four
        /// times. The product of this and <see cref="READY_RETRY_SECONDS"/> is the real budget —
        /// currently 30 seconds.</para>
        /// </summary>
        internal const int READY_RETRY_ATTEMPTS = 15;

        #endregion

        #region Spawn fairness (OB-11)

        /// <summary>
        /// Floor for the re-rolled miniboss ring radius, in world units.
        ///
        /// <para>Only reached in the degenerate case: when every placement attempt fails, the game
        /// falls back to the local player's own position, making the derived radius ~0. Without
        /// this floor the correction would faithfully reproduce the bug on somebody else.
        /// <b>UNVERIFIED</b> — the game's own distances live in constants that were not
        /// decompiled.</para>
        /// </summary>
        internal const float MIN_BOSS_RING_RADIUS = 20f;

        /// <summary>
        /// Ceiling for the re-rolled ring radius, in world units.
        ///
        /// <para><b>Added 2026-08-17 because the derivation has no upper bound and needs one.</b>
        /// The radius is taken from how far the game's own chosen position sits from the host — which
        /// is a sensible ring distance on the <c>EventMiniboss</c> path, and meaningless on every
        /// other one. A coffin passes its own transform as the spawn position, so the "radius" became
        /// the distance from the host to that coffin: the run logged 287, 424, 428, 443, 534, 657 and
        /// 658 units. Searching a band that wide around a player finds nothing, and all of those
        /// placements failed.</para>
        ///
        /// <para>60 matches <see cref="DISTANCE_MEDIUM"/>, which is the distance at which this
        /// project already considers an entity far enough away to stop updating every tick.</para>
        /// </summary>
        internal const float MAX_BOSS_RING_RADIUS = 60f;

        /// <summary>
        /// Width of the re-rolled ring, as a fraction of its radius.
        ///
        /// <para>Widened from 0.1 to 0.35 after the 2026-08-17 run failed 9 of 9 placements. The
        /// dominant cause there was a too-strict layer mask, fixed separately — but a +/-10% annulus
        /// is also very little area for the search to find footing in, and a wider band costs only
        /// some variance in how far the miniboss lands from its player.</para>
        /// </summary>
        internal const float BOSS_RING_BAND = 0.35f;

        /// <summary>Placement attempts for the re-rolled ring. Matches what the game itself passes.</summary>
        internal const int BOSS_RING_ATTEMPTS = 50;

        #endregion

        #region Balance

        /// <summary>
        /// Extra enemy HP per additional player, as a fraction.
        ///
        /// <para><b>The only value in this file that changes how the game plays</b> rather than how
        /// it is transmitted. Host-authoritative, so it takes effect from the host's copy — but it
        /// is read on both peers for prediction, so a mismatched build produces disagreeing health
        /// bars rather than a clean error.</para>
        /// </summary>
        internal const float HP_SCALING_PER_ADDITIONAL_PLAYER = 0.1f;

        #endregion

        #region Diagnostics

        /// <summary>
        /// Cadence of the throttled diagnostic reports, in seconds.
        ///
        /// <para>Shared by the bandwidth and allocation samplers so their output interleaves at the
        /// same rate and can be read side by side from one log. Both are off unless the matching
        /// <see cref="ModConfig"/> flag is set.</para>
        /// </summary>
        internal const float DIAGNOSTIC_REPORT_INTERVAL_SECONDS = 10f;

        #endregion
    }
}

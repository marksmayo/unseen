namespace Unseen.Net
{
    /// <summary>
    /// Whether this server is well, and what it does when told to stop.
    ///
    /// Both halves of "a server update must not kill matches in progress". An orchestrator decides
    /// when to replace a pod, and it can only act on what the process tells it - so the two ways to
    /// get this wrong are symmetrical and both are bad. A server that reports itself alive while
    /// its simulation has stopped is left in the fleet forever, with sixty-four people frozen in a
    /// town. A server that exits the moment it is asked ends a match for everybody in it, and the
    /// update arrives as a crash from the player's side.
    ///
    /// Plain state, no Kubernetes. The decisions are about when a server is unwell and when it may
    /// leave; the sidecar or the signal handler that calls these is a translation layer.
    /// </summary>
    public sealed class ServerLifecycle
    {
        /// <summary>
        /// How long without a simulation tick before the server is considered stalled.
        ///
        /// Generous against a slow frame and mean against a hung one. The base tick is 20 Hz, so
        /// three seconds is sixty missed ticks: nothing that is merely busy misses that many, and
        /// anything that has missed them is not coming back on its own.
        /// </summary>
        public const float StallSeconds = 3f;

        /// <summary>
        /// How long a server may take to start before health means anything.
        ///
        /// Generating the town takes most of a minute. A health check that failed during startup
        /// would have the orchestrator kill and restart every server it ever launched, and the
        /// fleet would never come up at all - a liveness probe that guarantees nothing is ever
        /// alive.
        /// </summary>
        public const float StartupGraceSeconds = 90f;

        /// <summary>
        /// Longest a draining server will wait for its match to finish.
        ///
        /// A match nobody can win - two players who never find each other, a mist that stops
        /// closing - would otherwise hold a pod open against a deploy indefinitely. Kubernetes
        /// answers that with SIGKILL once its grace period expires, which is exactly the abrupt
        /// ending draining exists to avoid, so the server should leave on its own terms first.
        ///
        /// Keep this under the pod's terminationGracePeriodSeconds or the two disagree and the
        /// kinder deadline never applies.
        /// </summary>
        public const float DrainSeconds = 25f;

        private float _lastTick = -1f;
        private float _drainingSince = -1f;

        /// <summary>The simulation stepped. Called every tick, from the loop that does the work.</summary>
        public void Ticked(float now)
        {
            _lastTick = now;
        }

        /// <summary>Whether this process is on its way out.</summary>
        public bool IsDraining => _drainingSince >= 0f;

        /// <summary>
        /// Whether new players should be sent here.
        ///
        /// False the moment draining starts. Seating somebody on a server that is about to go is
        /// worse than making them wait: they load for a minute, play for two, and are dropped by a
        /// deploy they had no part in.
        /// </summary>
        public bool AcceptsPlayers => !IsDraining;

        /// <summary>
        /// Whether the orchestrator should consider this server well.
        ///
        /// A stalled simulation is unhealthy even though the process is fine, because "the process
        /// exists" is not a useful thing to report about a game server. The port still accepts, the
        /// pod still looks alive, and the players are standing still.
        /// </summary>
        public bool IsHealthy(float now)
        {
            // Never ticked: still starting. Judging it now would fail every server during the
            // minute it spends generating a town.
            if (_lastTick < 0f) return now < StartupGraceSeconds;

            return now - _lastTick <= StallSeconds;
        }

        /// <summary>
        /// Asked to stop: a rolling deploy, a scale-down, a SIGTERM.
        ///
        /// Repeat requests do not move the deadline. An orchestrator repeating itself, or two
        /// signals arriving, would otherwise keep a server alive indefinitely by trying to shut it
        /// down.
        /// </summary>
        public void Drain(float now)
        {
            if (IsDraining) return;

            _drainingSince = now;
        }

        /// <summary>
        /// Whether the process should now exit.
        ///
        /// Only once draining, and then either because the match has finished - the good case, and
        /// the whole point - or because it has waited as long as it is going to.
        /// </summary>
        public bool ShouldExit(float now, bool matchInProgress)
        {
            if (!IsDraining) return false;
            if (!matchInProgress) return true;

            return now - _drainingSince >= DrainSeconds;
        }

        /// <summary>One line for a log or a health endpoint.</summary>
        public string Describe(float now)
        {
            if (!IsDraining) return IsHealthy(now) ? "healthy" : "stalled";

            return $"draining for {now - _drainingSince:0} s";
        }
    }
}

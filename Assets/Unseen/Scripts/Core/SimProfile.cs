namespace Unseen.Core
{
    /// <summary>
    /// What a process is responsible for simulating.
    ///
    /// Every mode used to run every system. A pure client stepped its own MatchDirector, spawned
    /// its own sixty-four bots, ran its own interest management and drew mist from its own zone
    /// controller - a complete second game on the player's machine, agreeing with the server's only
    /// by coincidence and drifting from it the moment anything was decided by a random number.
    ///
    /// The cost was not the wasted frame time, though there was plenty of that. It was that a
    /// client which simulates its own world has no reason to believe the one it is sent, and
    /// prediction has nothing to predict: the local ninja was already moving under local authority,
    /// so there was no round trip to hide and no correction to reconcile against.
    ///
    /// Named by responsibility rather than by mode. The bootstrap then reads as a description of
    /// what this process does, instead of twenty enum comparisons that each have to be got right,
    /// and the answer for a given mode lives in one place where it can be tested.
    /// </summary>
    public readonly struct SimProfile
    {
        /// <summary>
        /// Runs the match itself: phases, spawning, bots, the closing mist, the world's own life.
        /// The things that decide what happens rather than describe it.
        /// </summary>
        public readonly bool OwnsTheMatch;

        /// <summary>
        /// Decides who can perceive whom - line of sight, stealth, sound propagation, interest.
        ///
        /// Never a client's answer. Working out locally what you are allowed to see is the exact
        /// shape of a wallhack; the server sends a snapshot containing only what it proved you
        /// perceived, and that is the whole anti-cheat posture at the packet layer.
        /// </summary>
        public readonly bool ResolvesPerception;

        /// <summary>
        /// Steps agents through the world. True everywhere, including on a client, because a client
        /// must move its own ninja immediately rather than wait out the round trip.
        ///
        /// It is the same motor on both sides, deliberately. A prediction written as a second
        /// implementation of movement will disagree with the first, and then every correction the
        /// server sends snaps the player somewhere they did not expect.
        /// </summary>
        public readonly bool MovesAgents;

        /// <summary>Encodes and sends snapshots. Only something with clients attached.</summary>
        public readonly bool Replicates;

        private SimProfile(bool ownsTheMatch, bool resolvesPerception, bool movesAgents, bool replicates)
        {
            OwnsTheMatch = ownsTheMatch;
            ResolvesPerception = resolvesPerception;
            MovesAgents = movesAgents;
            Replicates = replicates;
        }

        public static SimProfile For(LaunchMode mode)
        {
            switch (mode)
            {
                // Offline practice is a server with nobody connected to it, not the opposite of
                // one. It runs the whole authoritative loop with a single human in it, and puts
                // its own snapshots through the full replication path - which is most of what it
                // is for, because a bug that only appears once replication is involved is one you
                // want to meet on your own machine rather than in a match.
                case LaunchMode.OfflinePractice:
                case LaunchMode.DedicatedServer:
                case LaunchMode.ListenServer:
                    return new SimProfile(true, true, true, true);

                case LaunchMode.Client:
                    return new SimProfile(false, false, true, false);

                // Failing closed. A mode this does not recognise came from a launch argument, and
                // granting authority by default hands the simulation to whatever a command line
                // happened to say.
                default:
                    return new SimProfile(false, false, true, false);
            }
        }
    }
}

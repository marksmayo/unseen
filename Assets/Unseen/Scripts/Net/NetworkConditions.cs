namespace Unseen.Net
{
    /// <summary>
    /// What the link between two machines is like, for pretending it is worse than loopback.
    ///
    /// Not a test fixture. Every netcode decision in this project is an argument about a network
    /// nobody here has played on: the resend timer, the ack bitfield, the prediction backlog, the
    /// refusal to fragment a snapshot. Loopback delivers everything, instantly, in order, which
    /// means the code that exists to survive an ordinary domestic connection has never once been
    /// asked to.
    ///
    /// Asking for none of this costs nothing: the transport wraps its socket only when conditions
    /// are actually passed, so a shipping build has the same call path it always had. Passing a
    /// blank one is different from passing none - it is a simulated link that is currently
    /// flawless, and can be degraded later.
    /// </summary>
    public readonly struct NetworkConditions
    {
        /// <summary>Fraction of datagrams that never arrive, 0 to 1.</summary>
        public readonly float Loss;

        /// <summary>One-way delay added to every datagram, in seconds.</summary>
        public readonly float Latency;

        /// <summary>
        /// How much the delay varies, in seconds, either side of <see cref="Latency"/>.
        ///
        /// This is where reordering comes from, and reordering is the interesting part: two packets
        /// sent in one order and delivered in the other is what the sequence window exists for, and
        /// it is not something a fixed delay can produce.
        /// </summary>
        public readonly float Jitter;

        /// <summary>
        /// Fraction of datagrams that arrive twice.
        ///
        /// Real, and worth simulating rather than dismissing: it comes out of route changes and
        /// misbehaving middleboxes, and a protocol that acts on a duplicate acts twice.
        /// </summary>
        public readonly float Duplication;

        /// <summary>Seeds the randomness, so a failure can be run again exactly.</summary>
        public readonly int Seed;

        public NetworkConditions(float loss = 0f, float latency = 0f, float jitter = 0f,
            float duplication = 0f, int seed = 0)
        {
            Loss = loss;
            Latency = latency;
            Jitter = jitter;
            Duplication = duplication;
            Seed = seed;
        }

        /// <summary>
        /// An ordinary evening on domestic broadband: 2% loss, 75 ms each way, 15 ms of jitter.
        ///
        /// Not a worst case. This is the condition the game is expected to be pleasant on, so it is
        /// the one to develop against rather than the one to survive.
        /// </summary>
        public static NetworkConditions Domestic =>
            new NetworkConditions(loss: 0.02f, latency: 0.075f, jitter: 0.015f, seed: 1);

        /// <summary>
        /// A bad mobile connection: 8% loss, 150 ms each way, 60 ms of jitter, and the occasional
        /// duplicate. The game should still be playable rather than merely not crash.
        /// </summary>
        public static NetworkConditions Mobile =>
            new NetworkConditions(loss: 0.08f, latency: 0.15f, jitter: 0.06f,
                duplication: 0.01f, seed: 2);
    }
}

using System.Collections.Generic;

namespace Unseen.Net
{
    /// <summary>
    /// Decides which strangers are allowed to become connections.
    ///
    /// This is the only part of the transport an unauthenticated peer on the internet can reach, so
    /// it is kept free of sockets and of the rest of the game: everything it does is a function of
    /// an endpoint, a cookie and a time, which means every hostile case can be written as a test.
    ///
    /// The scheme is a stateless cookie. A peer asking to connect is sent a value derived from the
    /// server's secret and its own address, and is admitted only when it echoes that value back.
    /// The server remembers nothing in between, so a flood of requests from forged addresses costs
    /// it no memory and no connection slots - and the echo proves the peer really is at the address
    /// it claims, because that is where the cookie was sent.
    /// </summary>
    public sealed class ConnectionGatekeeper
    {
        /// <summary>
        /// How long a cookie stays usable. Long enough to cross a slow link and come back, short
        /// enough that a cookie observed on the wire is worthless by the time it is replayed.
        /// </summary>
        public const float CookieLifetimeSeconds = 20f;

        /// <summary>A full match. Matches the sixty-four the battle royale is built around.</summary>
        public const int DefaultMaxConnections = 64;

        /// <summary>
        /// What the challenge reply costs: a message id, a protocol version and the cookie.
        /// </summary>
        public const int ChallengeReplyBytes = 12;

        /// <summary>
        /// How large a connect request must be before it is worth replying to.
        ///
        /// Padding is the entire defence against being used as an amplifier, and the number only
        /// has to beat <see cref="ChallengeReplyBytes"/> to remove the leverage. A kilobyte is far
        /// past that - roughly eighty-five to one against the sender - which leaves no margin for
        /// the reply to grow later and quietly become profitable to abuse again.
        /// </summary>
        public const int MinimumRequestBytes = 1024;

        /// <summary>
        /// Slots in the rate-limit table. Fixed at construction and never grown: a limiter that
        /// allocated per address would just move the attack from bandwidth to memory, and an
        /// attacker can mint addresses far faster than a server can afford to remember them.
        ///
        /// The cost of a fixed table is collisions. Two unrelated peers can land in the same slot
        /// and share a budget, so under a heavy flood a legitimate player may be refused for
        /// traffic that was never theirs. They retry and the bucket has refilled. Bounded memory is
        /// worth an occasional unfair refusal; unbounded memory is worth nothing at all.
        /// </summary>
        public const int RateLimitSlots = 4096;

        /// <summary>Requests answered back-to-back before a slot runs dry.</summary>
        public const int HandshakeBurst = 8;

        /// <summary>How quickly a slot earns the right to be answered again.</summary>
        public const float HandshakeRefillPerSecond = 4f;

        private readonly ulong _secret;
        private readonly int _maxConnections;

        private readonly float[] _tokens = new float[RateLimitSlots];
        private readonly float[] _tokensAt = new float[RateLimitSlots];

        public ConnectionGatekeeper(ulong secret, int maxConnections = DefaultMaxConnections)
        {
            _secret = secret;
            _maxConnections = maxConnections;

            for (int i = 0; i < RateLimitSlots; i++) _tokens[i] = HandshakeBurst;
        }

        /// <summary>
        /// How many peers the server is holding connection state for.
        ///
        /// Zero throughout the handshake, by design and under test. An unproven peer is a source
        /// address a stranger typed into a packet, and allocating anything at all for one means the
        /// server's memory is chosen by whoever sends it the most traffic.
        ///
        /// This deliberately does not count the rate limiter's table, which is a fixed-size array
        /// sized at construction and cannot grow with traffic. Conflating the two would make one of
        /// these defences look like a violation of the other.
        /// </summary>
        public int TrackedPeerCount => _established.Count;

        /// <summary>
        /// Peers that have proved they can receive at the address they claim. Only Accept may add
        /// to this, and only after the cookie has checked out - which is the whole security
        /// property, stated as one line of code.
        /// </summary>
        private readonly Dictionary<NetEndpoint, float> _established =
            new Dictionary<NetEndpoint, float>();

        private readonly List<NetEndpoint> _expired = new List<NetEndpoint>();

        /// <summary>
        /// How long a connection survives without being heard from.
        ///
        /// A disconnect is a courtesy, not a guarantee: a crash, a closed laptop or a pulled cable
        /// all leave silently, so silence has to be what costs a peer its seat. Long enough to ride
        /// out a bad patch of a mobile connection, short enough that a full server recovers on a
        /// timescale a player will wait through.
        /// </summary>
        public const float ConnectionTimeoutSeconds = 10f;

        /// <summary>
        /// Whether a connect request deserves a reply at all. Checked before <see cref="Challenge"/>,
        /// because the cheapest packet to handle is the one never answered.
        /// </summary>
        public bool ShouldAnswer(NetEndpoint peer, int requestBytes, float atSeconds)
        {
            if (requestBytes < MinimumRequestBytes) return false;

            // A token bucket, keyed by a hash of the address into a fixed table. Per slot rather
            // than global: one address running out must never close the tap on everybody else,
            // which would hand a single attacker the power to lock the whole server out.
            int slot = Slot(peer);

            float elapsed = atSeconds - _tokensAt[slot];
            if (elapsed > 0f)
            {
                _tokens[slot] = System.Math.Min(
                    HandshakeBurst, _tokens[slot] + elapsed * HandshakeRefillPerSecond);
                _tokensAt[slot] = atSeconds;
            }

            if (_tokens[slot] < 1f) return false;

            _tokens[slot] -= 1f;
            return true;
        }

        /// <summary>
        /// Which rate-limit slot an address falls in. Mixed rather than masked: consecutive
        /// addresses are exactly what a scan looks like, and sending a whole subnet to neighbouring
        /// slots would let one attacker sweep the table in order.
        /// </summary>
        private static int Slot(NetEndpoint peer)
        {
            ulong h = peer.Address * 0x9E3779B97F4A7C15UL;
            h ^= h >> 29;
            h *= 0xBF58476D1CE4E5B9UL;
            h ^= h >> 32;
            return (int)(h % RateLimitSlots);
        }

        /// <summary>The cookie to send back to a peer that has asked to connect.</summary>
        public ulong Challenge(NetEndpoint peer, float atSeconds)
        {
            return Cookie(peer, Window(atSeconds));
        }

        /// <summary>Whether a peer echoing this cookie should be admitted.</summary>
        public bool Accept(NetEndpoint peer, ulong cookie, float atSeconds)
        {
            // The previous slice counts too.
            //
            // Time is bucketed so the server can check a cookie without having stored it, but a
            // bucket boundary is an arbitrary instant that means nothing to a player: a cookie
            // minted just before one would be dead before it could possibly be answered. Accepting
            // the slice before as well means the usable life of a cookie is somewhere between one
            // and two lifetimes rather than between nothing and one - still bounded, and no longer
            // dependent on where in the bucket a player happened to arrive.
            long now = Window(atSeconds);
            if (cookie != Cookie(peer, now) && cookie != Cookie(peer, now - 1)) return false;

            // Already in: a repeated response is not a new connection. Checked before the cap so a
            // player whose acknowledgement was duplicated by the network is not refused from their
            // own seat once the server fills up.
            if (_established.ContainsKey(peer))
            {
                // Counted as hearing from them, not merely tolerated. This is the only traffic a
                // client sends between asking to join and being told it is in, so a repeat that
                // did not refresh the clock would leave a connection unrefreshed at exactly the
                // moment it is most fragile.
                _established[peer] = atSeconds;
                return true;
            }

            // Full servers refuse rather than grow.
            //
            // The cookie proves who a peer is, not that there is room for them. Every handshake
            // here can be entirely honest and the set would still grow until the process died, so
            // the ceiling is what makes memory a function of the server's size rather than of how
            // many people turn up. Refusing is right for a match in progress - the alternative,
            // evicting somebody already playing to seat a newcomer, is a worse answer to a full
            // server than "come back later".
            if (_established.Count >= _maxConnections) return false;

            _established.Add(peer, atSeconds);
            return true;
        }

        /// <summary>
        /// Packets a connection may send in a burst, and how fast that allowance refills.
        ///
        /// The client sends sixty inputs a second by design, so the refill sits well above that and
        /// the burst well above a frame's worth. Both halves matter: a limiter that clips honest
        /// traffic is worse than the flooding it prevents, because a dropped input is the game
        /// ignoring a player who did nothing wrong.
        /// </summary>
        public const float TrafficBurst = 90f;

        public const float TrafficRefillPerSecond = 180f;

        private readonly Dictionary<NetEndpoint, float> _trafficTokens =
            new Dictionary<NetEndpoint, float>();

        private readonly Dictionary<NetEndpoint, float> _trafficAt =
            new Dictionary<NetEndpoint, float>();

        /// <summary>
        /// Whether a packet from an established connection is worth decoding.
        ///
        /// Getting in was rate limited; staying in was not. An admitted client could send as fast
        /// as its link allowed and every packet cost a decode before anything judged it nonsense -
        /// so the cheapest attack on this server was to be a real player sending a thousand inputs
        /// a second. Tracked per connection rather than in the hashed table the handshake uses,
        /// because these are peers that have proved who they are and there are only ever sixty-four.
        /// </summary>
        public bool ShouldAcceptTraffic(NetEndpoint peer, float atSeconds)
        {
            if (!_established.ContainsKey(peer)) return false;

            if (!_trafficTokens.TryGetValue(peer, out float tokens))
            {
                tokens = TrafficBurst;
                _trafficAt[peer] = atSeconds;
            }

            float last = _trafficAt.TryGetValue(peer, out float at) ? at : atSeconds;
            float elapsed = atSeconds - last;

            if (elapsed > 0f)
            {
                tokens = System.Math.Min(TrafficBurst, tokens + elapsed * TrafficRefillPerSecond);
                _trafficAt[peer] = atSeconds;
            }

            if (tokens < 1f)
            {
                _trafficTokens[peer] = tokens;
                return false;
            }

            _trafficTokens[peer] = tokens - 1f;
            return true;
        }

        /// <summary>Notes that an established peer is still talking.</summary>
        public void Heard(NetEndpoint peer, float atSeconds)
        {
            if (_established.ContainsKey(peer)) _established[peer] = atSeconds;
        }

        /// <summary>
        /// Drops peers that have gone quiet, and reports which. Call once a tick.
        ///
        /// Reports rather than evicting silently. Anything else tracking connections - the transport
        /// keeps its own ids and its own list for the game to read - would otherwise go on believing
        /// in a player this has already forgotten, and two components holding the same fact with
        /// only one of them right is a bug that presents as a seat nobody can use.
        ///
        /// The returned list is reused between calls, so read it before the next tick.
        /// </summary>
        public IReadOnlyList<NetEndpoint> Expire(float atSeconds)
        {
            _expired.Clear();

            foreach (KeyValuePair<NetEndpoint, float> entry in _established)
                if (atSeconds - entry.Value > ConnectionTimeoutSeconds)
                    _expired.Add(entry.Key);

            // Collected first, removed after: a dictionary cannot be modified while it is being
            // walked, and the reusable list keeps a per-tick sweep from allocating.
            for (int i = 0; i < _expired.Count; i++)
            {
                _established.Remove(_expired[i]);

                // The traffic budget goes with the connection, or a long-running server keeps a
                // token count for every address that ever played on it.
                _trafficTokens.Remove(_expired[i]);
                _trafficAt.Remove(_expired[i]);
            }

            return _expired;
        }

        /// <summary>
        /// Which slice of time a cookie belongs to. Folding the time into the cookie rather than
        /// storing an issue date keeps the server stateless between the two halves of a handshake.
        /// </summary>
        private static long Window(float atSeconds)
        {
            return (long)(atSeconds / CookieLifetimeSeconds);
        }

        private ulong Cookie(NetEndpoint peer, long window)
        {
            // Mixed rather than concatenated: the peer must not be able to read its own address
            // back out of the cookie, or infer anything about the secret from a pair of them.
            ulong value = _secret;
            value ^= peer.Address * 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value ^= peer.Port * 0x94D049BB133111EBUL;
            value ^= unchecked((ulong)window) * 0xD6E8FEB86659FD93UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}

using System;
using System.Collections.Generic;

namespace Unseen.Net
{
    /// <summary>
    /// A socket with a bad network in front of it.
    ///
    /// Wraps a real one and mistreats what goes through: drops some of it, holds the rest for a
    /// while, hands a few over twice, and lets the ones held longest arrive after ones sent later.
    ///
    /// All of it on the way out, deliberately. Delaying on the way in would model only the local
    /// end's connection, and half the interesting failures are about what the far end does with
    /// what it did not receive. Applied at the sender, both ends of a test can be given their own
    /// conditions and the packet is already late by the time anyone sees it - which is what a real
    /// network does.
    /// </summary>
    public sealed class SimulatedSocket : IDatagramSocket
    {
        /// <summary>A datagram the network is still carrying.</summary>
        private struct InFlight
        {
            public NetEndpoint To;
            public byte[] Payload;
            public float ArrivesAt;
        }

        private readonly IDatagramSocket _inner;
        private readonly Random _random;

        /// <summary>
        /// Ordered by arrival time, earliest first, so a walk from the front emits in the order the
        /// far end will see. Inserting in order is what makes reordering real rather than nominal:
        /// a packet given a shorter delay genuinely overtakes one sent before it.
        /// </summary>
        private readonly List<InFlight> _inFlight = new List<InFlight>();

        private float _now;

        public SimulatedSocket(IDatagramSocket inner, NetworkConditions conditions)
        {
            _inner = inner;
            Conditions = conditions;
            _random = new Random(conditions.Seed);
        }

        /// <summary>
        /// The link's behaviour, which can be changed while it is running.
        ///
        /// Settable so a test or a playtest can start on a clean connection and then take the floor
        /// out from under it, which is the interesting moment and is otherwise impossible to reach:
        /// a link that was always broken never establishes anything to break.
        /// </summary>
        public NetworkConditions Conditions { get; set; }

        public NetEndpoint LocalEndpoint => _inner.LocalEndpoint;

        /// <summary>Datagrams this link has thrown away.</summary>
        public int Dropped { get; private set; }

        /// <summary>Datagrams it has sent a second copy of.</summary>
        public int Duplicated { get; private set; }

        /// <summary>Datagrams sent and not yet delivered, because the link is still carrying them.</summary>
        public int InFlightCount => _inFlight.Count;

        /// <summary>
        /// Datagrams handed over, whatever became of them. With <see cref="Dropped"/> this is what
        /// makes the simulation auditable: a test that believes it ran under loss can check that
        /// loss actually happened, rather than passing because nothing was ever dropped.
        /// </summary>
        public int Offered { get; private set; }

        public bool Send(NetEndpoint to, byte[] payload, int length)
        {
            // The size rule belongs to the path, not to the simulation, so it is still enforced and
            // still reported. A caller handing over more than the path can carry has made a mistake
            // that no amount of network weather explains.
            if (length > UdpSocket.MaxDatagramBytes) return false;

            NetworkConditions conditions = Conditions;

            Offered++;

            if (_random.NextDouble() < conditions.Loss)
            {
                // Counted here rather than reported to the caller.
                //
                // The return value stays faithful to what a socket knows: it hands the bytes to the
                // operating system and says yes, and nothing ever comes back to say they died three
                // hops later. Returning false would give the protocol above information no network
                // offers, and then every mechanism built on top would be tested against a kindness
                // it cannot rely on in production - the resend timer would never fire, because the
                // sender would always know.
                //
                // But the information is not lost either. It belongs here, as something observable
                // about the link, rather than as a falsehood in an answer the sender is given.
                Dropped++;
                return true;
            }

            var kept = new byte[length];
            Buffer.BlockCopy(payload, 0, kept, 0, length);

            Hold(to, kept, conditions);

            // A duplicate takes its own trip and gets its own delay, so it does not necessarily
            // arrive immediately after the original - which is the case worth simulating, because a
            // copy that arrives much later is the one a naive protocol acts on twice.
            if (_random.NextDouble() < conditions.Duplication)
            {
                Duplicated++;
                Hold(to, kept, conditions);
            }

            return true;
        }

        private void Hold(NetEndpoint to, byte[] payload, NetworkConditions conditions)
        {
            float jitter = conditions.Jitter > 0f
                ? (float)(_random.NextDouble() * 2.0 - 1.0) * conditions.Jitter
                : 0f;

            // Never before now: a negative total delay would deliver a packet before it was sent,
            // which is the one thing a network does not do.
            float arrivesAt = _now + Math.Max(0f, conditions.Latency + jitter);

            var flight = new InFlight { To = to, Payload = payload, ArrivesAt = arrivesAt };

            int at = _inFlight.Count;
            while (at > 0 && _inFlight[at - 1].ArrivesAt > arrivesAt) at--;

            _inFlight.Insert(at, flight);
        }

        public void Poll(float deltaTime)
        {
            _now += deltaTime;

            int due = 0;
            while (due < _inFlight.Count && _inFlight[due].ArrivesAt <= _now) due++;

            for (int i = 0; i < due; i++)
                _inner.Send(_inFlight[i].To, _inFlight[i].Payload, _inFlight[i].Payload.Length);

            if (due > 0) _inFlight.RemoveRange(0, due);

            _inner.Poll(deltaTime);
        }

        public bool TryReceive(out byte[] payload, out NetEndpoint from)
        {
            return _inner.TryReceive(out payload, out from);
        }

        public void Dispose()
        {
            _inFlight.Clear();
            _inner.Dispose();
        }
    }
}

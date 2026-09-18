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

        public bool Send(NetEndpoint to, byte[] payload, int length)
        {
            // The size rule belongs to the path, not to the simulation, so it is still enforced and
            // still reported. A caller handing over more than the path can carry has made a mistake
            // that no amount of network weather explains.
            if (length > UdpSocket.MaxDatagramBytes) return false;

            NetworkConditions conditions = Conditions;

            // Dropped, and reported as sent.
            //
            // A real socket hands the datagram to the operating system and says yes; nothing ever
            // tells the sender it was lost three hops later. Returning false here would give the
            // caller information no real network offers, and then the protocol above would be
            // tested against a kindness it will never actually receive.
            if (_random.NextDouble() < conditions.Loss) return true;

            var kept = new byte[length];
            Buffer.BlockCopy(payload, 0, kept, 0, length);

            Hold(to, kept, conditions);

            // A duplicate takes its own trip and gets its own delay, so it does not necessarily
            // arrive immediately after the original - which is the case worth simulating, because a
            // copy that arrives much later is the one a naive protocol acts on twice.
            if (_random.NextDouble() < conditions.Duplication) Hold(to, kept, conditions);

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

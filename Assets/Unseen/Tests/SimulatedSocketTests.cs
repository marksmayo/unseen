using System.Collections.Generic;
using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The bad network, tested on its own.
    ///
    /// This matters more than it looks. Several tests now assert that the transport survives loss,
    /// reordering and duplication, and every one of them would pass just as cheerfully if this
    /// class quietly delivered everything perfectly - a simulator that does not simulate turns a
    /// suite of hard-won guarantees into a suite of vacuous ones. So the weather is tested before
    /// anything is tested against the weather.
    /// </summary>
    public sealed class SimulatedSocketTests
    {
        /// <summary>Stands in for the real socket, and records what actually reached it.</summary>
        private sealed class Spy : IDatagramSocket
        {
            public readonly List<byte> Sent = new List<byte>();

            public NetEndpoint LocalEndpoint => default;

            public bool Send(NetEndpoint to, byte[] payload, int length)
            {
                Sent.Add(payload[0]);
                return true;
            }

            public void Poll(float deltaTime) { }

            public bool TryReceive(out byte[] payload, out NetEndpoint from)
            {
                payload = null;
                from = default;
                return false;
            }

            public void Dispose() { }
        }

        private static void Pump(SimulatedSocket socket, int ticks)
        {
            for (int i = 0; i < ticks; i++) socket.Poll(1f / 60f);
        }

        [Test]
        public void EverythingArrivesOnAPerfectLink()
        {
            var spy = new Spy();
            var link = new SimulatedSocket(spy, new NetworkConditions());

            for (byte n = 1; n <= 10; n++) link.Send(default, new[] { n }, 1);
            Pump(link, 2);

            Assert.AreEqual(10, spy.Sent.Count, "a blank link is not a broken one");
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, spy.Sent,
                "and delivers in the order it was given");
        }

        [Test]
        public void LostDatagramsNeverArrive()
        {
            var spy = new Spy();
            var link = new SimulatedSocket(spy, new NetworkConditions(loss: 1f, seed: 3));

            for (byte n = 1; n <= 10; n++) link.Send(default, new[] { n }, 1);
            Pump(link, 60);

            Assert.IsEmpty(spy.Sent, "nothing crosses a link that drops everything");
        }

        [Test]
        public void ALostDatagramIsStillReportedAsSent()
        {
            var link = new SimulatedSocket(new Spy(), new NetworkConditions(loss: 1f, seed: 3));

            // A real socket hands the datagram to the operating system and says yes. Nothing ever
            // comes back to tell the sender it was dropped three hops later, so reporting the loss
            // here would give the protocol above information no network will ever offer it - and
            // then it would be tested against a kindness it cannot rely on.
            Assert.IsTrue(link.Send(default, new byte[] { 1 }, 1),
                "the send succeeded; it is the delivery that did not");
        }

        [Test]
        public void LatencyHoldsADatagramBackBeforeLettingItThrough()
        {
            var spy = new Spy();
            var link = new SimulatedSocket(spy, new NetworkConditions(latency: 0.1f, seed: 5));

            link.Send(default, new byte[] { 1 }, 1);

            Pump(link, 3);
            Assert.IsEmpty(spy.Sent, "fifty milliseconds is not a hundred");

            Pump(link, 10);
            Assert.AreEqual(1, spy.Sent.Count, "and then it arrives");
        }

        [Test]
        public void JitterActuallyReordersThings()
        {
            var spy = new Spy();
            var link = new SimulatedSocket(spy, new NetworkConditions(latency: 0.05f, jitter: 0.04f, seed: 7));

            // Sent one per tick, in order, exactly as the transport sends snapshots.
            for (byte n = 1; n <= 40; n++)
            {
                link.Send(default, new[] { n }, 1);
                link.Poll(1f / 60f);
            }

            Pump(link, 60);

            Assert.AreEqual(40, spy.Sent.Count, "nothing is lost here, only shuffled");

            // The assertion this whole file exists for. Without it every "survives reordering" test
            // elsewhere could be passing because nothing was ever reordered.
            bool outOfOrder = false;
            for (int i = 1; i < spy.Sent.Count; i++)
                if (spy.Sent[i] < spy.Sent[i - 1]) { outOfOrder = true; break; }

            Assert.IsTrue(outOfOrder,
                "jitter wider than the sending interval must let packets overtake each other");
        }

        [Test]
        public void DuplicatesArriveTwice()
        {
            var spy = new Spy();
            var link = new SimulatedSocket(spy, new NetworkConditions(duplication: 1f, seed: 9));

            link.Send(default, new byte[] { 1 }, 1);
            Pump(link, 10);

            Assert.AreEqual(2, spy.Sent.Count, "every datagram duplicated means two of each");
            Assert.AreEqual(1, spy.Sent[0]);
            Assert.AreEqual(1, spy.Sent[1]);
        }

        [Test]
        public void TheSameSeedProducesTheSameWeather()
        {
            // A failure found under simulated loss is worth nothing if it cannot be run again. The
            // seed is the difference between a reproducible bug and an intermittent one.
            var first = new Spy();
            var second = new Spy();

            var a = new SimulatedSocket(first, new NetworkConditions(loss: 0.5f, seed: 4242));
            var b = new SimulatedSocket(second, new NetworkConditions(loss: 0.5f, seed: 4242));

            for (byte n = 1; n <= 50; n++)
            {
                a.Send(default, new[] { n }, 1);
                b.Send(default, new[] { n }, 1);
            }

            Pump(a, 5);
            Pump(b, 5);

            CollectionAssert.AreEqual(first.Sent, second.Sent, "same seed, same losses, every time");
            Assert.Greater(first.Sent.Count, 0, "and it did not simply drop everything");
            Assert.Less(first.Sent.Count, 50, "nor keep everything");
        }

        [Test]
        public void AnOversizeDatagramIsStillRefused()
        {
            var link = new SimulatedSocket(new Spy(), new NetworkConditions(loss: 1f, seed: 1));

            // The size rule belongs to the path rather than to the weather, so it survives being
            // wrapped. A caller handing over more than the link can carry has made a mistake that
            // no amount of simulated conditions explains, and it should hear about it here.
            Assert.IsFalse(link.Send(default, new byte[UdpSocket.MaxDatagramBytes + 1],
                UdpSocket.MaxDatagramBytes + 1));
        }
    }
}

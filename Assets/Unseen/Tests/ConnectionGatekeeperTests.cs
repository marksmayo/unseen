using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The connection handshake, which is the only part of the transport an unauthenticated
    /// stranger on the internet can reach. Tested without sockets: the gatekeeper is pure logic
    /// over an endpoint and a clock, so every hostile case is expressible as a plain call.
    /// </summary>
    public sealed class ConnectionGatekeeperTests
    {
        private const ulong Secret = 0xA5A51234DEADBEEFUL;

        private static NetEndpoint Peer(byte last = 1, ushort port = 40000)
        {
            return new NetEndpoint(0x7F000000u | last, port);
        }

        [Test]
        public void ChallengeCookieAdmitsTheSamePeer()
        {
            var gate = new ConnectionGatekeeper(Secret);
            NetEndpoint peer = Peer();

            ulong cookie = gate.Challenge(peer, 10f);

            Assert.IsTrue(gate.Accept(peer, cookie, 10.2f),
                "a peer echoing the cookie it was issued should be admitted");
        }

        [Test]
        public void AStaleCookieIsRefused()
        {
            var gate = new ConnectionGatekeeper(Secret);
            NetEndpoint peer = Peer();

            ulong cookie = gate.Challenge(peer, 10f);

            // A cookie that never expires is a permanent key to the server for anyone who observes
            // one once - and because the handshake is the part strangers can reach, that is the
            // difference between a replayed packet being useless and being an admission ticket.
            Assert.IsFalse(gate.Accept(peer, cookie, 10f + 600f),
                "a cookie replayed long after it was issued must not admit anyone");
        }

        [Test]
        public void AHandshakeStraddlingAWindowBoundaryIsStillAdmitted()
        {
            var gate = new ConnectionGatekeeper(Secret);
            NetEndpoint peer = Peer();

            // Issued a hair before the cookie's time slice rolls over and answered a hair after it.
            // That is an ordinary round trip, not an attack, and it is the case a naive expiry gets
            // wrong: bucket the clock and a cookie minted at the end of a bucket is dead on
            // arrival. The player sees a connection that refuses them for no reason and works when
            // they try again, which is the worst kind of bug to be told about second hand.
            float issued = ConnectionGatekeeper.CookieLifetimeSeconds - 0.01f;
            ulong cookie = gate.Challenge(peer, issued);

            Assert.IsTrue(gate.Accept(peer, cookie, issued + 0.05f),
                "a 50 ms round trip must not fail because it crossed a window boundary");
        }

        [Test]
        public void AFloodOfForgedRequestsCostsTheServerNothing()
        {
            var gate = new ConnectionGatekeeper(Secret);

            // Fifty thousand connect requests, every one from a different forged source address -
            // which is free for an attacker to produce and is what a public UDP port actually gets.
            for (int i = 0; i < 50_000; i++)
                gate.Challenge(new NetEndpoint((uint)i, (ushort)(1024 + (i % 60000))), i * 0.0001f);

            // The whole point of answering with a derived cookie instead of remembering who asked:
            // if the server tracks anything here, its memory is a function of how much traffic a
            // stranger chooses to send it, and the port becomes the cheapest way to kill the
            // process. Nothing may be retained until a peer has proved it can receive at the
            // address it claims.
            Assert.AreEqual(0, gate.TrackedPeerCount,
                "an unproven peer must leave no trace on the server");
        }

        [Test]
        public void AProvenPeerIsTrackedAndAForgedOneIsNot()
        {
            var gate = new ConnectionGatekeeper(Secret);
            NetEndpoint honest = Peer(1);
            NetEndpoint forger = Peer(2);

            gate.Accept(honest, gate.Challenge(honest, 5f), 5.1f);

            // The forger never received a cookie - it cannot, the reply went to the address it
            // forged - so the best it can do is guess. This is the pair that makes the flood test
            // above mean something: state exists, Accept creates it, and Challenge does not.
            gate.Accept(forger, 0x1234567890ABCDEFUL, 5.1f);

            Assert.AreEqual(1, gate.TrackedPeerCount,
                "exactly the peer that proved its address should be holding a connection");
        }

        [Test]
        public void ConnectionsAreCappedEvenWhenEveryHandshakeIsHonest()
        {
            var gate = new ConnectionGatekeeper(Secret, maxConnections: 64);

            // Every one of these completes a real handshake, so the cookie defence is satisfied and
            // has nothing more to say. An attacker willing to pay for a genuine round trip can do
            // exactly this, and without a ceiling the set of established peers grows until the
            // process dies - a slower, more expensive attack than a spoofed flood, and still fatal
            // to a server that is meant to hold sixty-four players.
            for (int i = 0; i < 500; i++)
            {
                var peer = new NetEndpoint(0x0A000000u | (uint)i, 5000);
                gate.Accept(peer, gate.Challenge(peer, 1f), 1.05f);
            }

            Assert.AreEqual(64, gate.TrackedPeerCount,
                "the server must stop accepting once it is full rather than growing");
        }

        [Test]
        public void AnEstablishedPeerKeepsItsSeatOnAFullServer()
        {
            var gate = new ConnectionGatekeeper(Secret, maxConnections: 2);

            NetEndpoint first = Peer(1);
            NetEndpoint second = Peer(2);
            ulong firstCookie = gate.Challenge(first, 1f);

            gate.Accept(first, firstCookie, 1.1f);
            gate.Accept(second, gate.Challenge(second, 1f), 1.1f);

            // UDP duplicates packets as a matter of course, so a player's handshake response
            // arriving twice is ordinary traffic rather than an attack. On a server with no room
            // left, a naive cap check sees the second copy as a new arrival and refuses it - and
            // the refusal lands on somebody who is already connected and playing.
            Assert.IsTrue(gate.Accept(first, firstCookie, 1.2f),
                "a duplicated response must not evict or refuse the peer that already holds a seat");
            Assert.AreEqual(2, gate.TrackedPeerCount, "and must not consume a second seat");
        }

        [Test]
        public void AnUndersizedConnectRequestIsNotWorthAnswering()
        {
            var gate = new ConnectionGatekeeper(Secret);
            NetEndpoint peer = Peer();

            // The danger here is not to this server, it is to a third party.
            //
            // Anyone can put a victim's address in the source field of a UDP packet. If a small
            // request draws a larger reply, an attacker sends a trickle of forged requests and the
            // server pours the amplified difference onto whoever they named - the server becomes
            // the weapon, and the traffic is genuinely from it. Requiring the request to be at
            // least as large as the reply removes the leverage: the attacker must spend more
            // bandwidth than the victim receives, at which point they may as well skip the server.
            Assert.IsFalse(gate.ShouldAnswer(peer, requestBytes: 24, atSeconds: 1f),
                "a tiny request must not draw a reply");
            Assert.IsTrue(gate.ShouldAnswer(peer, ConnectionGatekeeper.MinimumRequestBytes, 1f),
                "a properly padded request is answered normally");
        }

        [Test]
        public void OneAddressCannotMonopoliseTheHandshake()
        {
            var gate = new ConnectionGatekeeper(Secret);
            NetEndpoint noisy = Peer(1);
            NetEndpoint quiet = Peer(2);

            // A hundred properly padded requests in the same instant, all from one address. Each is
            // individually legitimate; the pattern is not.
            int answered = 0;
            for (int i = 0; i < 100; i++)
                if (gate.ShouldAnswer(noisy, ConnectionGatekeeper.MinimumRequestBytes, 2f))
                    answered++;

            Assert.Less(answered, 100, "a single address must not be answered without limit");

            // And the limit must be per address, not a global tap the noisy peer can close on
            // everybody else. Getting this wrong turns a rate limiter into a denial of service that
            // one attacker can aim at the whole server.
            Assert.IsTrue(gate.ShouldAnswer(quiet, ConnectionGatekeeper.MinimumRequestBytes, 2f),
                "an unrelated peer must still be served while another floods");
        }

        [Test]
        public void SilentPeersLoseTheirSeat()
        {
            var gate = new ConnectionGatekeeper(Secret, maxConnections: 2);

            NetEndpoint stayed = Peer(1);
            NetEndpoint vanished = Peer(2);

            gate.Accept(stayed, gate.Challenge(stayed, 1f), 1.1f);
            gate.Accept(vanished, gate.Challenge(vanished, 1f), 1.1f);

            // One keeps talking; the other is gone - crashed, unplugged, or never intended to stay.
            // Without eviction the cap that bounds memory becomes the thing that kills the server:
            // seats fill with peers that will never speak again and nobody can join. A pulled cable
            // sends no goodbye, so absence is the only signal there is.
            gate.Heard(stayed, 30f);
            gate.Expire(30f);

            Assert.AreEqual(1, gate.TrackedPeerCount, "the silent peer should have been dropped");
            Assert.IsTrue(gate.Accept(Peer(3), gate.Challenge(Peer(3), 30f), 30.1f),
                "and its seat should be available to somebody new");
        }
    }
}

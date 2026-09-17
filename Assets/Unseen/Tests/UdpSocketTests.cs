using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The socket itself, on loopback.
    ///
    /// Everything up to here was testable without a network because the policy and the format were
    /// kept apart from the transport. This is the part that cannot be: at some point a packet has
    /// to leave a real socket and arrive at another one, and that is the claim nothing else in the
    /// suite makes.
    ///
    /// Driven by polling in a bounded loop rather than by waiting. A test that sleeps for a fixed
    /// time either wastes it or fails on a slow machine; a test that spins until a deadline does
    /// neither, and is the same shape as the game's own Poll-once-per-frame loop.
    /// </summary>
    public sealed class UdpSocketTests
    {
        /// <summary>Pumps both ends until the condition holds or the attempt budget runs out.</summary>
        private static bool PumpUntil(UdpSocket a, UdpSocket b, System.Func<bool> condition,
            int attempts = 200)
        {
            for (int i = 0; i < attempts; i++)
            {
                a.Poll();
                b.Poll();
                if (condition()) return true;
                System.Threading.Thread.Sleep(1);
            }

            return false;
        }

        [Test]
        public void APacketCrossesARealSocket()
        {
            using (var server = new UdpSocket(0))
            using (var client = new UdpSocket(0))
            {
                var payload = new byte[] { 1, 2, 3, 4, 5 };
                client.Send(server.LocalEndpoint, payload, payload.Length);

                byte[] got = null;
                PumpUntil(server, client, () => server.TryReceive(out got, out _));

                Assert.IsNotNull(got, "the server should have received the datagram");
                Assert.AreEqual(5, got.Length);
                Assert.AreEqual(3, got[2]);
            }
        }

        [Test]
        public void AWholeHandshakeCompletesOverRealSockets()
        {
            var gate = new ConnectionGatekeeper(0xC0FFEEUL);

            using (var server = new UdpSocket(0))
            using (var client = new UdpSocket(0))
            {
                // 1. The client asks, padded.
                var request = new NetWriter();
                HandshakePackets.WriteConnectRequest(request);
                client.Send(server.LocalEndpoint, request.Buffer, request.Length);

                // 2. The server answers with a cookie, having stored nothing about who asked.
                byte[] inbound = null;
                NetEndpoint asker = default;
                Assert.IsTrue(PumpUntil(server, client, () => server.TryReceive(out inbound, out asker)),
                    "the request should have arrived");

                var readRequest = new NetReader();
                readRequest.Attach(inbound, inbound.Length);
                Assert.AreEqual(HandshakeMessage.ConnectRequest, HandshakePackets.ReadMessage(readRequest));
                Assert.IsTrue(gate.ShouldAnswer(asker, inbound.Length, 1f));
                Assert.AreEqual(0, gate.TrackedPeerCount, "still nothing held for an unproven peer");

                var challenge = new NetWriter();
                HandshakePackets.WriteChallenge(challenge, gate.Challenge(asker, 1f));
                server.Send(asker, challenge.Buffer, challenge.Length);

                // 3. The client echoes it back, with the name it would like.
                byte[] fromServer = null;
                Assert.IsTrue(PumpUntil(server, client, () => client.TryReceive(out fromServer, out _)),
                    "the challenge should have arrived");

                var readChallenge = new NetReader();
                readChallenge.Attach(fromServer, fromServer.Length);
                Assert.AreEqual(HandshakeMessage.Challenge, HandshakePackets.ReadMessage(readChallenge));

                var response = new NetWriter();
                HandshakePackets.WriteChallengeResponse(
                    response, HandshakePackets.ReadCookie(readChallenge), "Mark");
                client.Send(server.LocalEndpoint, response.Buffer, response.Length);

                // 4. And only now is anybody admitted.
                byte[] echoed = null;
                NetEndpoint prover = default;
                Assert.IsTrue(PumpUntil(server, client, () => server.TryReceive(out echoed, out prover)),
                    "the response should have arrived");

                var readResponse = new NetReader();
                readResponse.Attach(echoed, echoed.Length);
                Assert.AreEqual(HandshakeMessage.ChallengeResponse, HandshakePackets.ReadMessage(readResponse));

                Assert.IsTrue(gate.Accept(prover, HandshakePackets.ReadCookie(readResponse), 1.1f),
                    "a peer that echoed the cookie from its own address is admitted");
                Assert.AreEqual(1, gate.TrackedPeerCount);

                var roster = new PlayerRoster();
                Assert.AreEqual("Mark", roster.Claim(readResponse.ReadString()));
            }
        }

        [Test]
        public void PacketsArrivingTogetherAreAllDelivered()
        {
            using (var server = new UdpSocket(0))
            using (var client = new UdpSocket(0))
            {
                // Sixty-four players at sixty hertz do not take turns. Packets arrive in bursts, and
                // a socket that can hold only the most recent one silently discards the rest - which
                // does not look like a bug, it looks like packet loss, and it would be blamed on the
                // network for a long time before anybody suspected the receive path.
                for (int i = 0; i < 8; i++)
                    client.Send(server.LocalEndpoint, new byte[] { (byte)i }, 1);

                // Let all eight land in the kernel buffer before anybody looks.
                System.Threading.Thread.Sleep(50);

                // Then poll exactly once.
                //
                // The property at risk is throughput, not eventual delivery, so the assertion has to
                // be about how much one poll yields. A test that retries until the count is right
                // absorbs any rate bug there is: at one datagram per poll these eight still arrive,
                // just over eight frames - and so would sixty-four players' worth, over the several
                // seconds it took the kernel buffer to overflow and start discarding them.
                server.Poll();

                var seen = new System.Collections.Generic.List<byte>();
                while (server.TryReceive(out byte[] payload, out _)) seen.Add(payload[0]);

                Assert.AreEqual(8, seen.Count,
                    "a single poll must surface everything that is waiting, not one datagram");
            }
        }
    }
}

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
                    response, HandshakePackets.ReadCookie(readChallenge), "Mark", 0ul);
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

        [Test]
        public void AnOversizedDatagramIsRefusedRatherThanVanishing()
        {
            using (var server = new UdpSocket(0))
            using (var client = new UdpSocket(0))
            {
                // Beyond what a path will carry in one piece. A datagram larger than the path MTU is
                // fragmented by IP and, if any fragment is lost, the whole thing disappears - so on
                // a bad connection large snapshots stop arriving while small ones still do. That
                // reads as one player being invisible to another rather than as a network problem,
                // and it gets worse exactly when the match is busiest and the snapshots are biggest.
                var huge = new byte[UdpSocket.MaxDatagramBytes + 1];

                Assert.IsFalse(client.Send(server.LocalEndpoint, huge, huge.Length),
                    "the socket should refuse to send what the path cannot carry whole");

                var fits = new byte[UdpSocket.MaxDatagramBytes];
                Assert.IsTrue(client.Send(server.LocalEndpoint, fits, fits.Length),
                    "and should still send what fits");
            }
        }

        [Test]
        public void SendingToAClosedPortDoesNotKillTheReceiveLoop()
        {
            // The Windows behaviour the constructor's SIO_UDP_CONNRESET call exists to suppress.
            //
            // A datagram sent to a port nobody is listening on provokes an ICMP unreachable, and
            // Windows reports that back on the *next receive* of the sending socket as a connection
            // reset - on a connectionless socket, about a packet that was never part of a
            // connection. Left alone it surfaces as an exception that ends the receive loop.
            //
            // Which makes it a server-killer rather than a curiosity: a client quitting is an
            // ordinary event, the server keeps sending snapshots for a moment afterwards, and the
            // reply to those is exactly this. One player closing the game would stop the server
            // hearing from everybody else.
            //
            // What this pins is the guarantee, not the mechanism, and the difference was worth
            // finding out. Removing the IOControl call leaves this test passing - because Poll
            // catches SocketException and returns, so a reset merely ends one drain early and the
            // next poll carries on. The catch is doing the protective work; the IOControl is a
            // second layer that stops the error being raised in the first place.
            //
            // So the call itself is not pinned by anything, and no reasonable test would pin it:
            // the only observable difference is one poll cycle of delay, which is exactly the kind
            // of timing assertion that fails on a loaded machine for no reason. Recorded in TODO
            // rather than covered by a flaky test pretending to.
            using (var server = new UdpSocket(0))
            using (var client = new UdpSocket(0))
            {
                NetEndpoint nobody = ClosedPort();

                // Several, because the error arrives asynchronously and one might race past the
                // receive that follows it.
                for (int i = 0; i < 8; i++) client.Send(nobody, new byte[] { 1 }, 1);

                for (int i = 0; i < 20; i++)
                {
                    client.Poll();
                    System.Threading.Thread.Sleep(1);
                }

                // The socket must still work. Not "must not have thrown" - the throw is caught
                // inside Poll, and a caught exception that quietly ends the drain leaves a socket
                // that looks alive and hears nothing.
                server.Send(client.LocalEndpoint, new byte[] { 42 }, 1);

                byte[] arrived = null;
                Assert.IsTrue(
                    PumpUntil(server, client, () =>
                        client.TryReceive(out arrived, out NetEndpoint _)),
                    "the client should still receive after provoking an ICMP unreachable");

                Assert.AreEqual(42, arrived[0], "and receive the right bytes");
            }
        }

        /// <summary>
        /// An endpoint with certainly nothing on it: bound, read back, then closed.
        ///
        /// Better than picking a number and hoping. A hard-coded high port is usually free and
        /// occasionally is not, and a test that fails on one machine a month is worse than no test.
        /// </summary>
        private static NetEndpoint ClosedPort()
        {
            using (var doomed = new UdpSocket(0))
            {
                return doomed.LocalEndpoint;
            }
        }
    }
}

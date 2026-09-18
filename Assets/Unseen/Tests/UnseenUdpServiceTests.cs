using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The transport as the game sees it: an <see cref="INetworkService"/>, over real sockets.
    ///
    /// Everything below this has been tested in pieces - the gatekeeper without a network, the
    /// packets without a socket, the socket without a protocol. This is where they have to work
    /// together as the one interface the whole simulation is written against.
    /// </summary>
    public sealed class UnseenUdpServiceTests
    {
        /// <summary>Pumps both ends until the condition holds or the budget runs out.</summary>
        private static bool PumpUntil(UnseenUdpService a, UnseenUdpService b,
            System.Func<bool> condition, int attempts = 400)
        {
            for (int i = 0; i < attempts; i++)
            {
                a.Poll(1f / 60f);
                b.Poll(1f / 60f);
                if (condition()) return true;
                System.Threading.Thread.Sleep(1);
            }

            return false;
        }

        [Test]
        public void AClientConnectsAndTheServerIsToldAboutIt()
        {
            using (var server = UnseenUdpService.Host(0))
            using (var client = UnseenUdpService.Join(server.LocalEndpoint, "Mark"))
            {
                int connected = -1;
                server.ClientConnected += id => connected = id;

                Assert.IsTrue(PumpUntil(server, client, () => connected >= 0),
                    "the handshake should have completed and raised ClientConnected");

                Assert.AreEqual(NetRole.Server, server.Role);
                Assert.AreEqual(NetRole.Client, client.Role);
                Assert.AreEqual(1, server.Connections.Count, "one player is connected");
                Assert.IsTrue(client.IsConnected, "and the client knows it got in");
            }
        }

        [Test]
        public void TrafficFlowsBothWaysOnceConnected()
        {
            using (var server = UnseenUdpService.Host(0))
            using (var client = UnseenUdpService.Join(server.LocalEndpoint, "Mark"))
            {
                int id = -1;
                server.ClientConnected += c => id = c;
                Assert.IsTrue(PumpUntil(server, client, () => id >= 0), "connected");

                // Client to server: the shape of an input packet.
                byte[] fromClient = null;
                int fromConnection = -1;
                server.ServerReceived += (conn, payload, length) =>
                {
                    fromConnection = conn;
                    fromClient = payload;
                };

                client.SendToServer(new byte[] { 9, 8, 7 }, 3, false);
                Assert.IsTrue(PumpUntil(server, client, () => fromClient != null),
                    "the server should have received the client's packet");
                Assert.AreEqual(id, fromConnection, "attributed to the right connection");
                Assert.AreEqual(9, fromClient[0]);

                // Server to client: the shape of a snapshot.
                byte[] fromServer = null;
                client.ClientReceived += (payload, length) => fromServer = payload;

                server.SendToClient(id, new byte[] { 1, 2, 3, 4 }, 4, false);
                Assert.IsTrue(PumpUntil(server, client, () => fromServer != null),
                    "the client should have received the server's packet");
                Assert.AreEqual(4, fromServer.Length);
            }
        }

        [Test]
        public void AReliableMessageIsHeldUntilTheFarEndConfirmsIt()
        {
            using (var server = UnseenUdpService.Host(0))
            using (var client = UnseenUdpService.Join(server.LocalEndpoint, "Mark"))
            {
                int id = -1;
                server.ClientConnected += c => id = c;
                Assert.IsTrue(PumpUntil(server, client, () => id >= 0), "connected");

                int received = 0;
                byte[] body = null;
                client.ClientReceived += (payload, length) => { received++; body = payload; };

                // The shape of a match-start or a zone-stage message: something that happens once
                // and has no successor. The reliable flag has been on this interface since it was
                // written and has been ignored by every implementation of it, so a message sent
                // this way was exactly as droppable as a snapshot - and unlike a snapshot, nothing
                // comes along afterwards to put the client right.
                server.SendToClient(id, new byte[] { 42, 7 }, 2, reliable: true);

                Assert.IsTrue(PumpUntil(server, client, () => received > 0),
                    "a reliable message should reach the client");

                Assert.AreEqual(42, body[0], "with its bytes intact");

                // Pumped again rather than asserted straight away: the confirmation is a round
                // trip of its own, and the loop above stops the instant the message lands - before
                // the server has polled again to hear about it.
                Assert.IsTrue(PumpUntil(server, client, () => server.UnacknowledgedMessages(id) == 0),
                    "and should be retired once the client confirms it, not resent for ever");
            }
        }

        [Test]
        public void AVanishedClientIsEventuallyDeclaredGone()
        {
            using (var server = UnseenUdpService.Host(0))
            {
                int connected = -1;
                int disconnected = -1;
                server.ClientConnected += id => connected = id;
                server.ClientDisconnected += id => disconnected = id;

                using (var client = UnseenUdpService.Join(server.LocalEndpoint, "Mark"))
                {
                    Assert.IsTrue(PumpUntil(server, client, () => connected >= 0), "connected");
                }

                // The client is disposed - its socket is gone, exactly as if the process had been
                // killed or the cable pulled. No goodbye is sent, because a crash does not send one,
                // and a server that only notices tidy departures fills up with players who left.
                for (int i = 0; i < 400 && disconnected < 0; i++)
                    server.Poll(1f);

                Assert.AreEqual(connected, disconnected,
                    "the silent connection should have been declared gone and announced");
                Assert.AreEqual(0, server.Connections.Count, "and its seat given up");
            }
        }

        [Test]
        public void RoundTripTimeIsMeasuredRatherThanAssumed()
        {
            using (var server = UnseenUdpService.Host(0))
            using (var client = UnseenUdpService.Join(server.LocalEndpoint, "Mark"))
            {
                int id = -1;
                server.ClientConnected += c => id = c;
                Assert.IsTrue(PumpUntil(server, client, () => id >= 0), "connected");

                // Pumped long enough for a heartbeat to go out and come back.
                Assert.IsTrue(PumpUntil(server, client, () => server.RoundTripTime(id) > 0f),
                    "the server should have measured a round trip by now");

                // On loopback this is a fraction of a millisecond, so the assertion is that it was
                // measured at all and is sane - not that it hit a particular number. Your parry
                // window is clamp(150ms + rtt/2, 150, 200), so an rtt that stays zero silently
                // disables latency compensation for everybody, and one that reads wildly high hands
                // a laggy player the maximum window.
                float rtt = server.RoundTripTime(id);
                Assert.Greater(rtt, 0f, "a measured round trip is not zero");
                Assert.Less(rtt, 1f, "and a loopback round trip is not a second");
            }
        }
    }
}

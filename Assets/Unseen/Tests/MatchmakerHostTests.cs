using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The matchmaker as a client actually reaches it: over a socket.
    ///
    /// Everything below this has been tested without a network - the queue is arithmetic over a
    /// list, the protocol is text. This is where they have to work together across a connection,
    /// which is the one claim nothing else makes.
    ///
    /// Hand-rolled on TcpListener rather than HttpListener. The latter wants a URL reservation on
    /// Windows and refuses with an access error for anybody who has not run a command as
    /// administrator - a test that fails on somebody else's machine for a reason that has nothing
    /// to do with the code. The HTTP needed here is a request line, a few headers and a body.
    /// </summary>
    public sealed class MatchmakerHostTests
    {
        [Test]
        public void AClientQueuesAndIsSentSomewhere()
        {
            var matchmaker = new Matchmaker(playersPerServer: 4);
            matchmaker.ServerReady("tokyo-1", "10.0.0.7:7770");

            using (var host = new MatchmakerHost(matchmaker, port: 0))
            {
                host.Start();

                var client = new MatchmakerClient($"127.0.0.1:{host.Port}");

                Assert.IsTrue(client.TryQueue(out string ticket), "queued over a real socket");
                Assert.IsNotNull(ticket, "and given a ticket to ask about");

                Assert.IsTrue(client.TryStatus(ticket, out MatchmakerReply reply), "and answered");
                Assert.AreEqual(MatchmakerStatus.Assigned, reply.Status);
                Assert.AreEqual("10.0.0.7:7770", reply.Address);
            }
        }

        [Test]
        public void AQueuedPlayerIsToldHowManyAreAhead()
        {
            var matchmaker = new Matchmaker(playersPerServer: 1);
            matchmaker.ServerReady("only-one", "10.0.0.7:7770");

            using (var host = new MatchmakerHost(matchmaker, port: 0))
            {
                host.Start();

                var first = new MatchmakerClient($"127.0.0.1:{host.Port}");
                var second = new MatchmakerClient($"127.0.0.1:{host.Port}");

                Assert.IsTrue(first.TryQueue(out string _), "first in");
                Assert.IsTrue(second.TryQueue(out string secondTicket), "second in");

                Assert.IsTrue(second.TryStatus(secondTicket, out MatchmakerReply reply));

                // A wait with no number on it is a spinner, and a spinner is where a player decides
                // the game is broken. The number is nearly free and it is the whole difference.
                Assert.AreEqual(MatchmakerStatus.Waiting, reply.Status);
                Assert.AreEqual(1, reply.QueueLength);
            }
        }

        [Test]
        public void ARequestForNothingInParticularIsRefusedPolitely()
        {
            var matchmaker = new Matchmaker(playersPerServer: 4);

            using (var host = new MatchmakerHost(matchmaker, port: 0))
            {
                host.Start();

                var client = new MatchmakerClient($"127.0.0.1:{host.Port}");

                // A health checker, a browser, a port scanner, somebody's monitoring. A public port
                // gets all of it, and none of it should be able to make the service throw or hand
                // out a server.
                Assert.IsTrue(client.TryRaw("GET /nonsense HTTP/1.1", out string body),
                    "an unknown path still gets an answer rather than a hang");

                Assert.IsTrue(body.Contains("status=Unknown"), "and the answer is nothing useful");
                Assert.IsTrue(body.Contains("queue=0"), "with nobody queued by having asked");
            }
        }
    }
}

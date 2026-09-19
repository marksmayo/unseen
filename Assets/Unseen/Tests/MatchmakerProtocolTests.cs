using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// What the matchmaker and a client say to each other.
    ///
    /// Tested as text rather than through a socket, for the same reason the snapshot format is
    /// tested without one: the wire is where two programs agree, and an agreement is exactly the
    /// thing that can drift silently. A reply the client cannot read is indistinguishable from a
    /// matchmaker that is down, and the player is told "could not find a match" either way.
    ///
    /// Deliberately not JSON. A queue reply is three short fields and the project already has no
    /// JSON dependency; a line-oriented format is readable in a log, greppable in a capture, and
    /// cannot go wrong in a way that needs a parser to explain.
    /// </summary>
    public sealed class MatchmakerProtocolTests
    {
        [Test]
        public void AnAssignmentSurvivesTheRoundTrip()
        {
            string reply = MatchmakerProtocol.WriteStatus(
                MatchmakerStatus.Assigned, "t7", "10.0.0.7:7770", queueLength: 0);

            Assert.IsTrue(MatchmakerProtocol.ReadStatus(reply, out MatchmakerReply parsed));

            Assert.AreEqual(MatchmakerStatus.Assigned, parsed.Status);
            Assert.AreEqual("t7", parsed.Ticket);
            Assert.AreEqual("10.0.0.7:7770", parsed.Address, "the whole point of the exchange");
        }

        [Test]
        public void WaitingCarriesHowManyAreAhead()
        {
            string reply = MatchmakerProtocol.WriteStatus(
                MatchmakerStatus.Waiting, "t7", null, queueLength: 12);

            Assert.IsTrue(MatchmakerProtocol.ReadStatus(reply, out MatchmakerReply parsed));

            Assert.AreEqual(MatchmakerStatus.Waiting, parsed.Status);

            // A queue with no number on it is a spinner, and a spinner is where players decide the
            // game is broken and close it. Cheap to send and it is the only thing that makes
            // waiting tolerable.
            Assert.AreEqual(12, parsed.QueueLength);
            Assert.IsTrue(string.IsNullOrEmpty(parsed.Address), "nowhere to go yet");
        }

        [Test]
        public void RubbishIsRefusedRatherThanGuessedAt()
        {
            // A proxy error page, a truncated response, a half-written body, or somebody pointing
            // the game at a web server that is not this one. Every one of those is likelier than a
            // correct reply the day a matchmaker's address is typed in wrong.
            Assert.IsFalse(MatchmakerProtocol.ReadStatus("", out MatchmakerReply _));
            Assert.IsFalse(MatchmakerProtocol.ReadStatus(null, out MatchmakerReply _));
            Assert.IsFalse(MatchmakerProtocol.ReadStatus("<html>502 Bad Gateway</html>", out MatchmakerReply _));
            Assert.IsFalse(MatchmakerProtocol.ReadStatus("status=Assigned", out MatchmakerReply _),
                "a reply with no ticket in it is not about anybody");
        }

        [Test]
        public void AnUnknownStatusWordIsNotQuietlyTreatedAsAnAssignment()
        {
            // The failure that matters. Falling back to a default on a word this build does not
            // recognise would let a newer matchmaker say something cautious - "Rejected", say - and
            // have an older client read it as permission to connect.
            Assert.IsFalse(MatchmakerProtocol.ReadStatus(
                "status=Banished\nticket=t7\n", out MatchmakerReply _));
        }

        [Test]
        public void AnAddressWithNoPortIsNotAnAddress()
        {
            // The client is going to hand this straight to the transport, which wants host and
            // port. Catching it here means one clear failure instead of a connection attempt that
            // times out and looks like the server being down.
            string reply = MatchmakerProtocol.WriteStatus(
                MatchmakerStatus.Assigned, "t7", "10.0.0.7", queueLength: 0);

            Assert.IsFalse(MatchmakerProtocol.ReadStatus(reply, out MatchmakerReply _));
        }
    }
}

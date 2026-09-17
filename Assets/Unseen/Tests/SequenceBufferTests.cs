using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// Sequence numbers and acknowledgement, which is what turns a stream of datagrams into
    /// something a game can reason about.
    ///
    /// UDP delivers packets late, twice, or not at all, and says nothing about which. Every one of
    /// those needs a different answer: a stale snapshot must be discarded rather than applied over a
    /// newer one, a duplicate must not be processed twice, and a lost input must be noticed by the
    /// sender. All three need the same thing underneath - a number on every packet and a record of
    /// which numbers arrived.
    /// </summary>
    public sealed class SequenceBufferTests
    {
        [Test]
        public void ANewerPacketIsAccepted()
        {
            var window = new SequenceWindow();

            Assert.IsTrue(window.Accept(1));
            Assert.IsTrue(window.Accept(2));
            Assert.IsTrue(window.Accept(3));
            Assert.AreEqual(3, window.Latest);
        }

        [Test]
        public void AStalePacketIsRejected()
        {
            var window = new SequenceWindow();

            window.Accept(10);

            // Out-of-order delivery is routine, not exotic. Applying a snapshot that left the server
            // before the one already shown drags every position backwards for a frame, and a player
            // watching that sees rubber-banding and blames their connection.
            Assert.IsFalse(window.Accept(9), "a packet older than the latest is stale");
            Assert.IsFalse(window.Accept(1), "however much older");
            Assert.AreEqual(10, window.Latest, "and must not move the mark backwards");

            Assert.IsTrue(window.Accept(11), "while a newer one still gets through");
        }

        [Test]
        public void ADuplicateIsRejected()
        {
            var window = new SequenceWindow();

            window.Accept(5);

            // UDP duplicates packets on its own, and a duplicated input applied twice is a step
            // taken twice - a player who moved once and arrived somewhere they did not choose.
            Assert.IsFalse(window.Accept(5), "the same packet arriving twice is processed once");
        }

        [Test]
        public void SequenceNumbersSurviveWrappingAround()
        {
            var window = new SequenceWindow();

            window.Accept(65534);
            Assert.IsTrue(window.Accept(65535), "still climbing");

            // Sixteen bits roll over every 65,536 packets. At twenty snapshots a second that is
            // under an hour, and at sixty inputs a second it is about eighteen minutes - so this
            // happens in ordinary matches, not in theory. Compared naively, the step from 65535 to
            // 0 looks like an enormous jump backwards and every packet after it is rejected: the
            // connection freezes for good, part-way through a game, for no visible reason.
            Assert.IsTrue(window.Accept(0), "zero follows 65535");
            Assert.IsTrue(window.Accept(1), "and the count carries on");
            Assert.AreEqual(1, window.Latest);

            // And the other direction still has to work: 65535 is now the stale one.
            Assert.IsFalse(window.Accept(65535), "a packet from before the wrap is still stale");
        }

        [Test]
        public void TheWindowReportsWhichRecentPacketsArrived()
        {
            var window = new SequenceWindow();

            window.Accept(1);
            window.Accept(2);
            // 3 never arrives.
            window.Accept(4);

            // One integer describes the last thirty-two packets: bit n is set when the packet n
            // before the latest arrived. That is what lets a sender learn what was lost without the
            // receiver sending a message per packet - and it is how the server will tell a client
            // which of its inputs were actually processed.
            Assert.AreEqual(4, window.Latest);
            Assert.IsTrue(window.WasReceived(2), "two arrived");
            Assert.IsFalse(window.WasReceived(3), "three did not");
            Assert.IsTrue(window.WasReceived(1), "one arrived");
        }

        [Test]
        public void ALatePacketIsStillRecordedAsArrived()
        {
            var window = new SequenceWindow();

            window.Accept(10);

            // Rejected for processing - it is stale, and applying it would drag state backwards -
            // but it did arrive, and the sender is entitled to know that. Conflating "too old to
            // use" with "never turned up" would have the sender resending something already in
            // hand, which on a congested link makes the congestion worse.
            window.Accept(8);

            Assert.IsTrue(window.WasReceived(8),
                "a packet too late to apply is still a packet that arrived");
        }
    }
}

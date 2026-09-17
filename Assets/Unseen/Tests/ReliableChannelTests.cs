using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The messages that must arrive.
    ///
    /// Snapshots and inputs are deliberately unreliable: a lost one is replaced by the next, and a
    /// position from a hundred milliseconds ago is worse than no position. But some things happen
    /// once and have no successor - a match starting, a zone closing, a player being eliminated.
    /// Lose one of those and the client is wrong for the rest of the match with nothing to correct
    /// it, which is a different and far worse failure than a dropped frame of movement.
    /// </summary>
    public sealed class ReliableChannelTests
    {
        [Test]
        public void AMessageIsResentUntilItIsAcknowledged()
        {
            var channel = new ReliableChannel();

            channel.Queue(new byte[] { 1, 2, 3 });

            // First send, then nothing until the resend timer is up - a channel that repeated every
            // tick would multiply a busy moment into a flood of its own making.
            Assert.AreEqual(1, channel.Due(0f).Count, "sent once immediately");
            Assert.AreEqual(0, channel.Due(0.01f).Count, "and not again straight away");

            Assert.AreEqual(1, channel.Due(ReliableChannel.ResendSeconds + 0.001f).Count,
                "but again once the wait is over, because nothing has confirmed it arrived");
        }

        [Test]
        public void AnAcknowledgedMessageStopsBeingSent()
        {
            var channel = new ReliableChannel();

            ushort id = channel.Queue(new byte[] { 9 });
            channel.Due(0f);

            channel.Acknowledge(id);

            // Without this the channel resends for the life of the connection: the far end has the
            // message, has acted on it, and keeps being told about it every quarter second for the
            // rest of the match.
            Assert.AreEqual(0, channel.Due(ReliableChannel.ResendSeconds + 0.001f).Count,
                "a confirmed message is finished with");
            Assert.AreEqual(0, channel.OutstandingCount);
        }

        [Test]
        public void OneMessageBeingAcknowledgedDoesNotRetireTheOthers()
        {
            var channel = new ReliableChannel();

            channel.Queue(new byte[] { 1 });
            ushort second = channel.Queue(new byte[] { 2 });
            channel.Queue(new byte[] { 3 });

            channel.Acknowledge(second);

            // Acknowledgements arrive out of order, so confirming the middle one must not be read
            // as confirming everything before it. Getting that wrong loses messages silently, and
            // the ones lost are the ones that were already struggling to get through.
            Assert.AreEqual(2, channel.OutstandingCount,
                "only the message that was acknowledged is finished with");
        }

        [Test]
        public void TheBacklogIsBoundedWhenNothingIsEverAcknowledged()
        {
            var channel = new ReliableChannel();

            // A client that stops answering. What clears this queue is an acknowledgement, so
            // without a ceiling the server's memory becomes a function of how long somebody stays
            // silent - the same shape as a connection nothing evicts, or a prediction backlog
            // nothing retires.
            for (int i = 0; i < 500; i++) channel.Queue(new byte[] { (byte)i });

            Assert.LessOrEqual(channel.OutstandingCount, ReliableChannel.MaxOutstanding,
                "the queue must not grow without bound when acknowledgements never come");
        }
    }
}

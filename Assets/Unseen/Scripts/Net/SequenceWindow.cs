namespace Unseen.Net
{
    /// <summary>
    /// Decides whether an arriving packet is new, stale or a repeat.
    ///
    /// UDP delivers datagrams late, twice, or not at all, and says nothing about which. Each needs a
    /// different answer, and all three need the same thing underneath: a number on every packet and
    /// a record of which numbers have been seen.
    ///
    /// Nothing here retransmits. For snapshots and inputs a lost packet is better replaced by the
    /// next one than resent late - a position from a hundred milliseconds ago is worse than no
    /// position at all - so the sequence exists to let a receiver throw away what is stale, not to
    /// let a sender send it again. That is the whole reason this is not TCP.
    /// </summary>
    public sealed class SequenceWindow
    {
        /// <summary>The highest sequence seen so far.</summary>
        public ushort Latest { get; private set; }

        private bool _any;

        /// <summary>
        /// Which of the last thirty-two packets arrived. Bit zero is <see cref="Latest"/>, bit n is
        /// the packet n before it.
        ///
        /// One integer describes the whole recent history, which is what lets a receiver tell a
        /// sender exactly what was lost without sending a message per packet. Thirty-two at twenty
        /// snapshots a second is about a second and a half of history - far longer than any round
        /// trip worth waiting for, so anything older than the window is a packet nobody should
        /// still be asking about.
        /// </summary>
        private uint _received;

        /// <summary>How many packets of history the window keeps.</summary>
        public const int HistoryBits = 32;

        /// <summary>
        /// Takes a packet's sequence number, and says whether it is worth processing.
        ///
        /// True means newer than anything seen. False means stale or a duplicate, and the caller
        /// should drop it rather than apply it over something fresher.
        /// </summary>
        public bool Accept(ushort sequence)
        {
            if (!_any)
            {
                _any = true;
                Latest = sequence;
                _received = 1u;
                return true;
            }

            if (IsNewerThan(sequence, Latest))
            {
                // The window slides forward by the gap, so anything skipped is left as a zero bit -
                // which is precisely the record of what went missing.
                int step = (ushort)(sequence - Latest);
                _received = step >= HistoryBits ? 1u : (_received << step) | 1u;
                Latest = sequence;
                return true;
            }

            // Stale, but it did arrive.
            //
            // Two different questions, deliberately not collapsed into one. This one is too old to
            // apply - putting it over fresher state would drag positions backwards - and the sender
            // is still entitled to know it got here. Reporting a late packet as lost has the sender
            // resending what the receiver already holds, which on a congested link is exactly the
            // wrong response to congestion.
            int back = (ushort)(Latest - sequence);
            if (back < HistoryBits) _received |= 1u << back;

            return false;
        }

        /// <summary>
        /// Whether a packet arrived, regardless of whether it was fresh enough to be processed.
        /// Answers a different question from <see cref="Accept"/>: did this get here, not should I
        /// act on it.
        /// </summary>
        public bool WasReceived(ushort sequence)
        {
            if (!_any) return false;
            if (sequence == Latest) return true;
            if (IsNewerThan(sequence, Latest)) return false;

            int back = (ushort)(Latest - sequence);
            return back < HistoryBits && (_received & (1u << back)) != 0u;
        }

        /// <summary>The history as it goes on the wire, alongside <see cref="Latest"/>.</summary>
        public uint AckBits => _received;

        /// <summary>
        /// Whether one sequence number comes after another, allowing for the count wrapping.
        ///
        /// Sixteen bits roll over every 65,536 packets - under an hour at twenty snapshots a second,
        /// about eighteen minutes at sixty inputs - so this happens in ordinary matches rather than
        /// in theory. Compared as plain numbers, the step from 65535 to 0 reads as an enormous jump
        /// backwards and everything after it is rejected: the connection freezes for good, part-way
        /// through a game, with nothing in the log to say why.
        ///
        /// Subtracting and casting to signed is the whole trick. The difference wraps exactly as the
        /// sequence does, so 0 - 65535 comes out as +1 rather than -65535. It holds as long as two
        /// packets are never more than half the space apart, which here means nine minutes of
        /// unbroken loss - by which time the connection has long since timed out.
        /// </summary>
        public static bool IsNewerThan(ushort sequence, ushort than)
        {
            return (short)(sequence - than) > 0;
        }
    }
}

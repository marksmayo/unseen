using System.Collections.Generic;

namespace Unseen.Net
{
    /// <summary>
    /// Carries the messages that must arrive, over a transport that guarantees nothing.
    ///
    /// Snapshots and inputs are deliberately unreliable: a lost one is replaced by the next, and a
    /// position from a hundred milliseconds ago is worse than no position at all. But some things
    /// happen once and have no successor - a match starting, a zone stage closing, an elimination.
    /// Lose one of those and the client is wrong for the rest of the match with nothing to put it
    /// right: somebody who missed "stage three began" keeps drawing the old circle and walks into
    /// damage they cannot see coming.
    ///
    /// So: keep them, send them, and keep sending until the far end says it has them.
    /// </summary>
    public sealed class ReliableChannel
    {
        /// <summary>
        /// How long to wait before assuming a message did not arrive.
        ///
        /// Comfortably beyond any round trip worth playing on. Resending every tick would turn one
        /// unacknowledged message into sixty a second, which lands hardest when the network is
        /// already congested and the acknowledgement is merely late - the same self-worsening
        /// mistake as treating a late packet as a lost one.
        /// </summary>
        public const float ResendSeconds = 0.25f;

        /// <summary>
        /// Most messages held at once.
        ///
        /// A bound is needed because the thing that clears this - an acknowledgement - may never
        /// come. Without one, a client that stops answering makes the server's memory a function of
        /// how long it stays silent. At the point this fills, the connection is beyond saving and
        /// the timeout will take it.
        /// </summary>
        public const int MaxOutstanding = 64;

        /// <summary>
        /// A message on its way out, with the id the far end will name when it acknowledges.
        ///
        /// The id has to travel with the bytes. A queue that hands back anonymous payloads cannot
        /// make anything reliable: the receiver has nothing to acknowledge, so nothing is ever
        /// retired and the same message goes out every quarter second for the rest of the match.
        /// </summary>
        public readonly struct Message
        {
            public readonly ushort Id;
            public readonly byte[] Payload;

            public Message(ushort id, byte[] payload)
            {
                Id = id;
                Payload = payload;
            }
        }

        private struct Outgoing
        {
            public ushort Id;
            public byte[] Payload;
            public float SentAt;
            public bool Sent;
        }

        private readonly List<Outgoing> _outstanding = new List<Outgoing>();
        private readonly List<Message> _due = new List<Message>();

        private ushort _nextId;

        /// <summary>How many messages are still waiting to be acknowledged.</summary>
        public int OutstandingCount => _outstanding.Count;

        /// <summary>Adds a message that has to arrive.</summary>
        public ushort Queue(byte[] payload)
        {
            ushort id;
            unchecked { id = ++_nextId; }

            if (_outstanding.Count >= MaxOutstanding) _outstanding.RemoveAt(0);

            _outstanding.Add(new Outgoing { Id = id, Payload = payload, Sent = false });
            return id;
        }

        /// <summary>
        /// The messages that should go out now: those never sent, and those whose wait has expired.
        ///
        /// The returned list is reused between calls, so read it before calling again.
        /// </summary>
        public IReadOnlyList<Message> Due(float atSeconds)
        {
            _due.Clear();

            for (int i = 0; i < _outstanding.Count; i++)
            {
                Outgoing message = _outstanding[i];

                if (message.Sent && atSeconds - message.SentAt < ResendSeconds) continue;

                message.Sent = true;
                message.SentAt = atSeconds;
                _outstanding[i] = message;

                _due.Add(new Message(message.Id, message.Payload));
            }

            return _due;
        }

        /// <summary>Forgets a message the far end has confirmed.</summary>
        public void Acknowledge(ushort id)
        {
            for (int i = 0; i < _outstanding.Count; i++)
            {
                if (_outstanding[i].Id != id) continue;

                _outstanding.RemoveAt(i);
                return;
            }
        }
    }
}

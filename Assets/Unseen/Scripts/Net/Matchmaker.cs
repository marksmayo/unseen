using System.Collections.Generic;

namespace Unseen.Net
{
    /// <summary>What has become of a ticket.</summary>
    public enum MatchmakerStatus : byte
    {
        /// <summary>No such ticket: never issued, cancelled, or long since collected.</summary>
        Unknown = 0,

        /// <summary>In the queue. Nothing has room yet.</summary>
        Waiting = 1,

        /// <summary>A server has been picked and held. The address is where to go.</summary>
        Assigned = 2
    }

    /// <summary>
    /// The queue in front of the fleet.
    ///
    /// <see cref="ServerAllocator"/> answers which server a player should be sent to, and packs
    /// them so a fleet does not fill with matches that can never start. It cannot answer what
    /// happens when every server is full, and the honest answer is that the player waits - so
    /// somebody has to hold them, in order, and hand them an address when one appears.
    ///
    /// Tickets rather than a blocking call. The wait is unbounded - it ends when a match ends or
    /// the autoscaler brings a server up - and the thing waiting is a game that has to keep drawing
    /// frames. So a player asks once, is given a ticket, and asks about the ticket until it turns
    /// into an address.
    ///
    /// No network in here on purpose. This is the part with the decisions in it, and it should be
    /// testable without a socket; the HTTP in front of it is a thin translation of these calls.
    /// </summary>
    public sealed class Matchmaker
    {
        private readonly ServerAllocator _fleet;
        private readonly Dictionary<string, string> _addressById = new Dictionary<string, string>();

        /// <summary>Tickets still waiting, oldest first.</summary>
        private readonly List<string> _queue = new List<string>();

        /// <summary>Tickets that have been given a server, and where.</summary>
        private readonly Dictionary<string, string> _assigned = new Dictionary<string, string>();

        /// <summary>Which server each assigned ticket is occupying, so leaving can give it back.</summary>
        private readonly Dictionary<string, string> _serverByTicket = new Dictionary<string, string>();

        private int _nextTicket;

        public Matchmaker(int playersPerServer)
        {
            _fleet = new ServerAllocator(playersPerServer);
        }

        /// <summary>How many players are waiting for a server.</summary>
        public int Waiting => _queue.Count;

        /// <summary>A server has come up and is ready for players.</summary>
        public void ServerReady(string id, string address)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(address)) return;

            _addressById[id] = address;
            _fleet.Register(id);
        }

        /// <summary>Joins the queue. The ticket is how the player asks about it afterwards.</summary>
        public string Enqueue()
        {
            string ticket = "t" + (++_nextTicket);
            _queue.Add(ticket);
            return ticket;
        }

        /// <summary>
        /// Hands out servers to whoever has been waiting longest, while there is room.
        ///
        /// Called rather than done inside Enqueue, so that a server coming up also moves the queue.
        /// Both are the same event from the queue's point of view - something changed, see who can
        /// go now - and having one path for it means a player cannot be left waiting in front of a
        /// server with space because nobody asked again.
        /// </summary>
        public void Pump()
        {
            while (_queue.Count > 0)
            {
                string server = _fleet.Allocate();
                if (server == null) return;

                string ticket = _queue[0];
                _queue.RemoveAt(0);

                _assigned[ticket] = _addressById[server];
                _serverByTicket[ticket] = server;
            }
        }

        /// <summary>
        /// A player is finished with their ticket: they left the match, or the queue.
        ///
        /// One call for both, because from here they are the same event - a player who is no longer
        /// waiting and no longer playing - and two calls would mean a caller choosing between them,
        /// which is a choice they can get wrong.
        /// </summary>
        public void Leave(string ticket)
        {
            if (string.IsNullOrEmpty(ticket)) return;

            _queue.Remove(ticket);

            // Occupancy only ever climbing is how a fleet ends up reporting every server full while
            // every match is empty. Leaving is silent, so this is the only thing that notices.
            if (_serverByTicket.TryGetValue(ticket, out string server))
            {
                _fleet.Release(server);
                _serverByTicket.Remove(ticket);
            }

            _assigned.Remove(ticket);
        }

        /// <summary>
        /// A server has gone: evicted, crashed, drained, or lost with its node.
        ///
        /// Everybody it was holding goes back into the queue rather than being left with an address
        /// that will never answer. They keep their place at the front - they were assigned before
        /// anybody still waiting, and losing a server is not a reason to go to the back of a line
        /// they had already left.
        /// </summary>
        public void ServerGone(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            _addressById.Remove(id);

            // Out of the allocator too. Without this the fleet goes on packing players onto a
            // machine that no longer exists, which is worse than having no server at all: the
            // queue drains into nothing and nobody waits.
            _fleet.Remove(id);

            var stranded = new List<string>();

            foreach (KeyValuePair<string, string> kv in _serverByTicket)
                if (kv.Value == id) stranded.Add(kv.Key);

            for (int i = 0; i < stranded.Count; i++)
            {
                string ticket = stranded[i];

                _serverByTicket.Remove(ticket);
                _assigned.Remove(ticket);
                _queue.Insert(i, ticket);
            }
        }

        /// <summary>Where a ticket stands, and the address if it has one.</summary>
        public MatchmakerStatus StatusOf(string ticket, out string address)
        {
            address = null;
            if (string.IsNullOrEmpty(ticket)) return MatchmakerStatus.Unknown;

            if (_assigned.TryGetValue(ticket, out address)) return MatchmakerStatus.Assigned;

            return _queue.Contains(ticket) ? MatchmakerStatus.Waiting : MatchmakerStatus.Unknown;
        }
    }
}

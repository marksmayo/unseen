using System.Collections.Generic;

namespace Unseen.Net
{
    /// <summary>
    /// Chooses which game server a joining player is sent to.
    ///
    /// Plain logic over a set of known servers rather than a service: the fleet manifest in
    /// Server/k8s already knows how to start and stop machines, and what it needs from matchmaking
    /// is this one decision. Keeping the decision free of HTTP and of Agones means it can be tested
    /// without deploying anything, which is the difference between a rule that is checked and a
    /// rule that is hoped for.
    /// </summary>
    public sealed class ServerAllocator
    {
        private readonly int _capacity;
        private readonly List<string> _ids = new List<string>();
        private readonly Dictionary<string, int> _occupancy = new Dictionary<string, int>();

        public ServerAllocator(int capacity)
        {
            _capacity = capacity;
        }

        /// <summary>Adds a server the fleet has started.</summary>
        public void Register(string id)
        {
            if (_occupancy.ContainsKey(id)) return;

            _ids.Add(id);
            _occupancy[id] = 0;
        }

        /// <summary>
        /// The server a joining player should be sent to, or null when the fleet is full.
        ///
        /// Packs rather than spreads: of the servers with room, the one nearest full wins. Handing
        /// each new player an empty server is the obvious implementation and produces a fleet of
        /// matches that can never start, each one costing money while nobody can play.
        /// </summary>
        public string Allocate()
        {
            string best = null;
            int bestOccupancy = -1;

            for (int i = 0; i < _ids.Count; i++)
            {
                string id = _ids[i];
                int taken = _occupancy[id];

                if (taken >= _capacity) continue;
                if (taken <= bestOccupancy) continue;

                best = id;
                bestOccupancy = taken;
            }

            // Nothing had room. Null rather than an exception: a full fleet is an ordinary state on
            // a busy evening, not a failure, and the caller's answer is to start another server.
            if (best == null) return null;

            _occupancy[best] = bestOccupancy + 1;
            return best;
        }

        /// <summary>
        /// Gives a slot back when a player leaves that server.
        ///
        /// Without it occupancy only ever climbs, and a fleet that has been up for a day reports
        /// every server full while every match is empty - the same shape as a connection that is
        /// never evicted, and for the same reason: leaving is silent, so something has to notice.
        ///
        /// Floored at zero. A double release is a bookkeeping mistake somewhere upstream, and the
        /// wrong response is to let the count go negative and quietly invent capacity that does not
        /// exist - that turns a small accounting bug into players being sent to a full server.
        /// </summary>
        public void Release(string id)
        {
            // Unknown ids are left alone entirely rather than written with a negative count.
            //
            // Not only to avoid the negative: Register returns early for any id it already has an
            // entry for, so writing one here for a server that has not registered yet would drop
            // that server on the floor for good - it would boot, report healthy, and never be
            // allocated a single player.
            if (!_occupancy.TryGetValue(id, out int taken)) return;

            _occupancy[id] = taken > 0 ? taken - 1 : 0;
        }
    }
}

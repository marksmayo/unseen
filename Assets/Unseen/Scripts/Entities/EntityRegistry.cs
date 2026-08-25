using System;
using System.Collections.Generic;
using Unseen.Core;

namespace Unseen.Entities
{
    /// <summary>
    /// Dense slot table for every simulated agent. Slots stay compact so the Burst jobs can
    /// address agents by index, while EntityIds remain unique for the whole match.
    /// </summary>
    public sealed class EntityRegistry
    {
        private readonly List<AgentEntity> _agents;
        private readonly Dictionary<int, AgentEntity> _byId;
        private readonly Dictionary<int, AgentEntity> _byConnection = new Dictionary<int, AgentEntity>();

        private int _nextId;

        public event Action<AgentEntity> Registered;
        public event Action<AgentEntity> Unregistered;

        public EntityRegistry(int capacity)
        {
            capacity = Math.Max(8, capacity);
            _agents = new List<AgentEntity>(capacity);
            _byId = new Dictionary<int, AgentEntity>(capacity);
        }

        public int Count => _agents.Count;
        public IReadOnlyList<AgentEntity> All => _agents;

        public int AliveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _agents.Count; i++)
                    if (_agents[i].IsAlive) n++;
                return n;
            }
        }

        public int AlivePlayerCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _agents.Count; i++)
                    if (_agents[i].IsAlive && !_agents[i].IsBot) n++;
                return n;
            }
        }

        public int BotCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _agents.Count; i++)
                    if (_agents[i].IsBot) n++;
                return n;
            }
        }

        public AgentId Register(AgentEntity agent)
        {
            if (agent == null) throw new ArgumentNullException(nameof(agent));
            if (agent.Slot >= 0) return agent.Id;

            agent.Id = new AgentId(++_nextId);
            agent.Slot = _agents.Count;
            _agents.Add(agent);
            _byId[agent.Id.Value] = agent;
            if (agent.ConnectionId >= 0) _byConnection[agent.ConnectionId] = agent;

            Registered?.Invoke(agent);
            return agent.Id;
        }

        public void Unregister(AgentEntity agent)
        {
            if (agent == null || agent.Slot < 0) return;

            int slot = agent.Slot;
            int last = _agents.Count - 1;
            if (slot != last)
            {
                AgentEntity moved = _agents[last];
                _agents[slot] = moved;
                moved.Slot = slot;
            }

            _agents.RemoveAt(last);
            _byId.Remove(agent.Id.Value);
            if (agent.ConnectionId >= 0 && _byConnection.TryGetValue(agent.ConnectionId, out AgentEntity owner) && owner == agent)
                _byConnection.Remove(agent.ConnectionId);

            agent.Slot = -1;
            Unregistered?.Invoke(agent);
        }

        public bool TryGet(AgentId id, out AgentEntity agent)
        {
            return _byId.TryGetValue(id.Value, out agent);
        }

        public AgentEntity Get(AgentId id)
        {
            return _byId.TryGetValue(id.Value, out AgentEntity a) ? a : null;
        }

        public AgentEntity BySlot(int slot)
        {
            return slot >= 0 && slot < _agents.Count ? _agents[slot] : null;
        }

        public AgentEntity ByConnection(int connectionId)
        {
            return _byConnection.TryGetValue(connectionId, out AgentEntity a) ? a : null;
        }

        /// <summary>Rebinds a slot from a bot to an arriving human, or back again on disconnect.</summary>
        public void SetConnection(AgentEntity agent, int connectionId)
        {
            if (agent.ConnectionId >= 0) _byConnection.Remove(agent.ConnectionId);
            agent.ConnectionId = connectionId;
            if (connectionId >= 0) _byConnection[connectionId] = agent;
        }

        public void Clear()
        {
            for (int i = _agents.Count - 1; i >= 0; i--)
                _agents[i].Slot = -1;
            _agents.Clear();
            _byId.Clear();
            _byConnection.Clear();
        }
    }
}

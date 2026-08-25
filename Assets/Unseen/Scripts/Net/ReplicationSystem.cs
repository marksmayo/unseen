using System.Collections.Generic;
using Unseen.Core;
using Unseen.Entities;

namespace Unseen.Net
{
    /// <summary>
    /// Fans per-observer snapshots out to clients. Cold clients get one snapshot per base tick;
    /// a client whose agent is in a combat pocket gets one every combat tick. Event queues carry a
    /// per-connection cursor so a hot client never sees the same clash twice and a cold client
    /// never misses one.
    /// </summary>
    public sealed class ReplicationSystem : SimSystem
    {
        private readonly NetWriter _writer = new NetWriter(4096);
        private readonly Dictionary<int, int> _combatCursor = new Dictionary<int, int>(64);
        private readonly Dictionary<int, int> _worldCursor = new Dictionary<int, int>(64);

        public override int Order => SimOrder.Replication;
        public override SimRate Rate => SimRate.Combat;

        public int SnapshotsLastTick { get; private set; }
        public int BytesLastTick { get; private set; }
        public long TotalBytesSent { get; private set; }

        public override void Tick(in SimFrame frame)
        {
            INetworkService net = Ctx.Net;
            if (!net.IsServer) return;

            IReadOnlyList<int> connections = net.Connections;
            int snapshots = 0;
            int bytes = 0;

            for (int i = 0; i < connections.Count; i++)
            {
                int connection = connections[i];
                AgentEntity self = Ctx.Entities.ByConnection(connection);
                if (self == null) continue;

                bool send = frame.IsBaseTick || self.IsHot;
                if (!send) continue;

                int combatCursor = _combatCursor.TryGetValue(connection, out int cc) ? cc : 0;
                int worldCursor = _worldCursor.TryGetValue(connection, out int wc) ? wc : 0;

                _writer.Reset();
                SnapshotProtocol.EncodeSnapshot(
                    _writer, Ctx, self, frame.Tick, frame.Time,
                    Ctx.Combat.Events, combatCursor,
                    Ctx.Destructibles.PendingEvents, worldCursor);

                net.SendToClient(connection, _writer.Buffer, _writer.Length, false);

                _combatCursor[connection] = Ctx.Combat.Events.Count;
                _worldCursor[connection] = Ctx.Destructibles.PendingEvents.Count;

                // Heard sounds are consumed by the send: they are one-shot pings, not state.
                self.Heard.Clear();

                snapshots++;
                bytes += _writer.Length;
            }

            SnapshotsLastTick = snapshots;
            BytesLastTick = bytes;
            TotalBytesSent += bytes;

            // Every connection is guaranteed a snapshot on a base tick, so that is the safe point
            // to retire the event queues.
            if (!frame.IsBaseTick) return;

            Ctx.Combat.ClearEvents();
            Ctx.Destructibles.ClearEvents();
            _combatCursor.Clear();
            _worldCursor.Clear();
        }

        /// <summary>Rough outbound bandwidth in kilobits per second, for the server console.</summary>
        public float KilobitsPerSecond => BytesLastTick * 8f * Ctx.Config.Network.BaseTickRate / 1000f;
    }

    /// <summary>Applies input received from clients to their agents, clamping anything hostile.</summary>
    public sealed class ServerInputSystem : SimSystem
    {
        private readonly Dictionary<int, MoveIntent> _pending = new Dictionary<int, MoveIntent>(64);
        private readonly Dictionary<int, uint> _lastSequence = new Dictionary<int, uint>(64);
        private readonly NetReader _reader = new NetReader();

        public override int Order => SimOrder.Input;
        public override SimRate Rate => SimRate.Combat;

        protected override void OnInitialize()
        {
            Ctx.Net.ServerReceived += OnServerReceived;
        }

        private void OnServerReceived(int connection, byte[] payload, int length)
        {
            if (length < 1) return;

            _reader.Attach(payload, length);
            if (!SnapshotProtocol.DecodeInput(_reader, out MoveIntent intent)) return;

            // Drop out-of-order input rather than letting a client rewind its own state.
            if (_lastSequence.TryGetValue(connection, out uint last) && intent.Sequence <= last && last - intent.Sequence < 1000)
                return;

            _lastSequence[connection] = intent.Sequence;
            _pending[connection] = Sanitise(intent);
        }

        private static MoveIntent Sanitise(MoveIntent intent)
        {
            float x = Unity.Mathematics.math.clamp(intent.Move.x, -1f, 1f);
            float y = Unity.Mathematics.math.clamp(intent.Move.y, -1f, 1f);
            intent.Move = new Unity.Mathematics.float2(x, y);
            intent.Pitch = Unity.Mathematics.math.clamp(intent.Pitch, -80f, 80f);
            if (intent.UseUtility > 3) intent.UseUtility = 0;
            return intent;
        }

        public override void Tick(in SimFrame frame)
        {
            foreach (KeyValuePair<int, MoveIntent> kv in _pending)
            {
                AgentEntity agent = Ctx.Entities.ByConnection(kv.Key);
                if (agent == null || !agent.IsAlive) continue;
                agent.Intent = kv.Value;
            }
        }

        public override void Shutdown()
        {
            Ctx.Net.ServerReceived -= OnServerReceived;
            _pending.Clear();
        }
    }
}

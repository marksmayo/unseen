using System;
using System.Collections.Generic;

namespace Unseen.Net
{
    /// <summary>
    /// Loopback transport. Server and client live in one process and packets are handed straight
    /// across, but they are still serialised and still filtered by the interest manager. That means
    /// offline practice exercises the real replication path - a bug in snapshot encoding shows up
    /// in single player, not for the first time on a dedicated server.
    /// </summary>
    public sealed class OfflineNetworkService : INetworkService
    {
        private readonly List<int> _connections = new List<int> { 0 };
        private readonly Queue<byte[]> _toClient = new Queue<byte[]>();
        private readonly Queue<byte[]> _toServer = new Queue<byte[]>();
        private readonly Queue<int> _toClientLengths = new Queue<int>();
        private readonly Queue<int> _toServerLengths = new Queue<int>();

        /// <summary>Artificial latency in seconds, applied in each direction. Useful for testing the parry window.</summary>
        public float SimulatedLatency;

        public NetRole Role => NetRole.Offline;
        public bool IsServer => true;
        public bool IsClient => true;
        public int LocalConnectionId => 0;
        public IReadOnlyList<int> Connections => _connections;

        public event Action<int> ClientConnected;
        public event Action<int> ClientDisconnected;
        public event Action<int, byte[], int> ServerReceived;
        public event Action<byte[], int> ClientReceived;

        public float RoundTripTime(int connectionId) => SimulatedLatency * 2f;

        public void Start()
        {
            ClientConnected?.Invoke(0);
        }

        public void SendToClient(int connectionId, byte[] payload, int length, bool reliable)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(payload, 0, copy, 0, length);
            _toClient.Enqueue(copy);
            _toClientLengths.Enqueue(length);
        }

        public void SendToServer(byte[] payload, int length, bool reliable)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(payload, 0, copy, 0, length);
            _toServer.Enqueue(copy);
            _toServerLengths.Enqueue(length);
        }

        public void Poll(float deltaTime)
        {
            while (_toServer.Count > 0)
            {
                byte[] payload = _toServer.Dequeue();
                int length = _toServerLengths.Dequeue();
                ServerReceived?.Invoke(0, payload, length);
            }

            while (_toClient.Count > 0)
            {
                byte[] payload = _toClient.Dequeue();
                int length = _toClientLengths.Dequeue();
                ClientReceived?.Invoke(payload, length);
            }
        }

        public void Shutdown()
        {
            ClientDisconnected?.Invoke(0);
            _toClient.Clear();
            _toServer.Clear();
            _toClientLengths.Clear();
            _toServerLengths.Clear();
        }
    }
}

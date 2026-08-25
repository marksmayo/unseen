// Fish-Net transport adapter.
//
// This file only compiles when UNSEEN_FISHNET is added to the scripting define symbols, and its
// assembly definition carries the same constraint, so a project without Fish-Net installed is
// unaffected. Written against Fish-Net 4.x broadcasts; if your version renames something, this file
// is the only place that has to change - everything above it talks to INetworkService.
#if UNSEEN_FISHNET
using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;
using Unseen.Core;
using Unseen.Net;

namespace Unseen.Integrations.FishNet
{
    /// <summary>Opaque Unseen payload. The snapshot format is ours, not Fish-Net's.</summary>
    public struct UnseenPayload : IBroadcast
    {
        public byte[] Data;
    }

    /// <summary>Round-trip probe. Feeds the latency-compensated parry window.</summary>
    public struct UnseenPing : IBroadcast
    {
        public float ServerTime;
    }

    public struct UnseenPong : IBroadcast
    {
        public float ServerTime;
    }

    public sealed class UnseenFishNetService : INetworkService
    {
        private const float PingInterval = 1f;

        private readonly NetworkManager _manager;
        private readonly List<int> _connections = new List<int>(64);
        private readonly Dictionary<int, float> _roundTrip = new Dictionary<int, float>(64);
        private readonly Dictionary<int, NetworkConnection> _byId = new Dictionary<int, NetworkConnection>(64);

        private float _nextPingAt;
        private float _elapsed;

        public NetRole Role { get; }
        public bool IsServer { get; private set; }
        public bool IsClient { get; private set; }
        public int LocalConnectionId { get; private set; } = -1;
        public IReadOnlyList<int> Connections => _connections;

        public event Action<int> ClientConnected;
        public event Action<int> ClientDisconnected;
        public event Action<int, byte[], int> ServerReceived;
        public event Action<byte[], int> ClientReceived;

        public UnseenFishNetService(NetworkManager manager, LaunchMode mode)
        {
            _manager = manager;

            switch (mode)
            {
                case LaunchMode.DedicatedServer:
                    Role = NetRole.Server;
                    break;
                case LaunchMode.Client:
                    Role = NetRole.Client;
                    break;
                default:
                    Role = NetRole.Host;
                    break;
            }
        }

        public void Start()
        {
            if (Role == NetRole.Server || Role == NetRole.Host)
            {
                _manager.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
                _manager.ServerManager.RegisterBroadcast<UnseenPayload>(OnServerPayload);
                _manager.ServerManager.RegisterBroadcast<UnseenPong>(OnServerPong);
                _manager.ServerManager.StartConnection();
                IsServer = true;
            }

            if (Role == NetRole.Client || Role == NetRole.Host)
            {
                _manager.ClientManager.RegisterBroadcast<UnseenPayload>(OnClientPayload);
                _manager.ClientManager.RegisterBroadcast<UnseenPing>(OnClientPing);
                _manager.ClientManager.StartConnection();
                IsClient = true;
            }
        }

        private void OnRemoteConnectionState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            int id = connection.ClientId;

            if (args.ConnectionState == RemoteConnectionState.Started)
            {
                if (!_connections.Contains(id)) _connections.Add(id);
                _byId[id] = connection;
                _roundTrip[id] = 0f;
                ClientConnected?.Invoke(id);
                return;
            }

            _connections.Remove(id);
            _byId.Remove(id);
            _roundTrip.Remove(id);
            ClientDisconnected?.Invoke(id);
        }

        private void OnServerPayload(NetworkConnection connection, UnseenPayload payload, Channel channel)
        {
            if (payload.Data == null) return;
            ServerReceived?.Invoke(connection.ClientId, payload.Data, payload.Data.Length);
        }

        private void OnClientPayload(UnseenPayload payload, Channel channel)
        {
            if (payload.Data == null) return;
            if (LocalConnectionId < 0 && _manager.ClientManager.Connection != null)
                LocalConnectionId = _manager.ClientManager.Connection.ClientId;

            ClientReceived?.Invoke(payload.Data, payload.Data.Length);
        }

        private void OnClientPing(UnseenPing ping, Channel channel)
        {
            // Bounce it straight back; the server measures the round trip.
            _manager.ClientManager.Broadcast(new UnseenPong { ServerTime = ping.ServerTime }, Channel.Unreliable);
        }

        private void OnServerPong(NetworkConnection connection, UnseenPong pong, Channel channel)
        {
            float rtt = Mathf.Max(0f, _elapsed - pong.ServerTime);
            _roundTrip[connection.ClientId] = rtt;
        }

        public float RoundTripTime(int connectionId)
        {
            return _roundTrip.TryGetValue(connectionId, out float rtt) ? rtt : 0f;
        }

        public void SendToClient(int connectionId, byte[] payload, int length, bool reliable)
        {
            if (!_byId.TryGetValue(connectionId, out NetworkConnection connection)) return;

            var copy = new byte[length];
            Buffer.BlockCopy(payload, 0, copy, 0, length);
            _manager.ServerManager.Broadcast(connection, new UnseenPayload { Data = copy },
                true, reliable ? Channel.Reliable : Channel.Unreliable);
        }

        public void SendToServer(byte[] payload, int length, bool reliable)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(payload, 0, copy, 0, length);
            _manager.ClientManager.Broadcast(new UnseenPayload { Data = copy },
                reliable ? Channel.Reliable : Channel.Unreliable);
        }

        public void Poll(float deltaTime)
        {
            // Fish-Net pumps its own transport; all this has to do is drive the RTT probe.
            _elapsed += deltaTime;
            if (!IsServer || _elapsed < _nextPingAt) return;

            _nextPingAt = _elapsed + PingInterval;
            for (int i = 0; i < _connections.Count; i++)
            {
                if (!_byId.TryGetValue(_connections[i], out NetworkConnection connection)) continue;
                _manager.ServerManager.Broadcast(connection, new UnseenPing { ServerTime = _elapsed },
                    true, Channel.Unreliable);
            }
        }

        public void Shutdown()
        {
            if (IsServer)
            {
                _manager.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
                _manager.ServerManager.UnregisterBroadcast<UnseenPayload>(OnServerPayload);
                _manager.ServerManager.UnregisterBroadcast<UnseenPong>(OnServerPong);
                _manager.ServerManager.StopConnection(true);
            }

            if (IsClient)
            {
                _manager.ClientManager.UnregisterBroadcast<UnseenPayload>(OnClientPayload);
                _manager.ClientManager.UnregisterBroadcast<UnseenPing>(OnClientPing);
                _manager.ClientManager.StopConnection();
            }

            _connections.Clear();
            _byId.Clear();
            _roundTrip.Clear();
        }
    }

    /// <summary>Registers the adapter with the transport factory before any scene loads.</summary>
    public static class UnseenFishNetBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            UnseenTransport.Factory = mode =>
            {
                NetworkManager manager = InstanceFinder.NetworkManager;
                if (manager == null)
                {
                    Debug.LogWarning("[Unseen] UNSEEN_FISHNET is defined but no NetworkManager is in " +
                                     "the scene; falling back to the loopback transport.");
                    return null;
                }

                var service = new UnseenFishNetService(manager, mode);
                service.Start();
                return service;
            };
        }
    }
}
#endif

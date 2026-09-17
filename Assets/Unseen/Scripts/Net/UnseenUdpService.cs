using System;
using System.Collections.Generic;

namespace Unseen.Net
{
    /// <summary>
    /// The real transport: a UDP socket, the connection gatekeeper and the handshake packets,
    /// presented to the game as an <see cref="INetworkService"/>.
    ///
    /// Thin on purpose. Every decision worth getting wrong lives in a piece below this that can be
    /// tested without a network - who may connect, what a packet means, how fast an address may
    /// ask. What is left here is bookkeeping: which endpoint is which connection, and whose turn it
    /// is to speak during a handshake.
    /// </summary>
    public sealed class UnseenUdpService : INetworkService, IDisposable
    {
        private readonly UdpSocket _socket;
        private readonly ConnectionGatekeeper _gate;
        private readonly PlayerRoster _roster = new PlayerRoster();

        private readonly Dictionary<NetEndpoint, int> _idByPeer = new Dictionary<NetEndpoint, int>();
        private readonly Dictionary<int, NetEndpoint> _peerById = new Dictionary<int, NetEndpoint>();
        private readonly Dictionary<int, string> _nameById = new Dictionary<int, string>();
        private readonly Dictionary<int, float> _rttById = new Dictionary<int, float>();

        /// <summary>
        /// How often a heartbeat goes out. Frequent enough that a round trip stays current and a
        /// quiet connection is never mistaken for a dead one, rare enough to be nothing next to
        /// twenty snapshots a second.
        /// </summary>
        public const float HeartbeatIntervalSeconds = 1f;

        private float _nextHeartbeatAt;
        private readonly List<int> _connections = new List<int>();

        private readonly NetWriter _scratch = new NetWriter();
        private readonly NetReader _reader = new NetReader();

        private readonly NetEndpoint _serverEndpoint;
        private readonly string _requestedName;

        private float _now;
        private float _nextConnectAttemptAt;
        private int _nextConnectionId = 1;

        private UnseenUdpService(NetRole role, int port, NetEndpoint server, string requestedName)
        {
            Role = role;
            _socket = new UdpSocket(port);
            _serverEndpoint = server;
            _requestedName = requestedName;

            // A secret per process, not a constant. A cookie is only unforgeable while the value it
            // is derived from is unknown, and a secret compiled into a build that anybody can
            // download is not a secret at all.
            _gate = new ConnectionGatekeeper(unchecked((ulong)Guid.NewGuid().GetHashCode() * 0x9E3779B97F4A7C15UL));
        }

        /// <summary>Opens a server on a port, or on one the OS picks when given zero.</summary>
        public static UnseenUdpService Host(int port)
        {
            return new UnseenUdpService(NetRole.Server, port, default, null);
        }

        /// <summary>Opens a client and begins asking to join the given server.</summary>
        public static UnseenUdpService Join(NetEndpoint server, string requestedName)
        {
            return new UnseenUdpService(NetRole.Client, 0, server, requestedName);
        }

        public NetRole Role { get; }
        public bool IsServer => Role == NetRole.Server || Role == NetRole.Host;
        public bool IsClient => Role == NetRole.Client || Role == NetRole.Host;

        /// <summary>Where this service can be reached. Meaningful on a server.</summary>
        public NetEndpoint LocalEndpoint => _socket.LocalEndpoint;

        /// <summary>
        /// Whether a client has actually been admitted.
        ///
        /// Not on INetworkService, because its previous implementations were loopback where joining
        /// was instantaneous and unconditional. A real client has a meaningful in-between state -
        /// asked, not yet answered - and reporting "connected" from the moment Join was called would
        /// make every check of it a lie during the one window where the answer matters.
        /// </summary>
        public bool IsConnected { get; private set; }

        public int LocalConnectionId { get; private set; } = -1;

        public IReadOnlyList<int> Connections => _connections;

        public event Action<int> ClientConnected;
        public event Action<int> ClientDisconnected;
        public event Action<int, byte[], int> ServerReceived;
        public event Action<byte[], int> ClientReceived;

        /// <summary>The name the server settled on for a connection.</summary>
        public string NameOf(int connectionId)
        {
            return _nameById.TryGetValue(connectionId, out string name) ? name : null;
        }

        public float RoundTripTime(int connectionId)
        {
            return _rttById.TryGetValue(connectionId, out float rtt) ? rtt : 0f;
        }

        public void SendToClient(int connectionId, byte[] payload, int length, bool reliable)
        {
            if (!_peerById.TryGetValue(connectionId, out NetEndpoint peer)) return;

            SequenceWindow inbound = WindowFor(connectionId);

            _scratch.Reset();
            HandshakePackets.WritePayload(_scratch, payload, length,
                NextSequence(connectionId), inbound.Latest, inbound.AckBits);
            _socket.Send(peer, _scratch.Buffer, _scratch.Length);
        }

        public void SendToServer(byte[] payload, int length, bool reliable)
        {
            if (!IsConnected) return;

            _scratch.Reset();
            HandshakePackets.WritePayload(_scratch, payload, length,
                NextSequence(ServerConnectionId), _inbound.Latest, _inbound.AckBits);
            _socket.Send(_serverEndpoint, _scratch.Buffer, _scratch.Length);
        }

        /// <summary>The id a client files the server's own connection state under.</summary>
        private const int ServerConnectionId = 0;

        private readonly Dictionary<int, ushort> _outgoing = new Dictionary<int, ushort>();
        private readonly Dictionary<int, SequenceWindow> _windows = new Dictionary<int, SequenceWindow>();
        private readonly SequenceWindow _inbound = new SequenceWindow();

        private ushort NextSequence(int connectionId)
        {
            _outgoing.TryGetValue(connectionId, out ushort current);

            // Wraps on its own. Unchecked because rolling over is the design, not an overflow.
            unchecked { current++; }

            _outgoing[connectionId] = current;
            return current;
        }

        private SequenceWindow WindowFor(int connectionId)
        {
            if (_windows.TryGetValue(connectionId, out SequenceWindow window)) return window;

            window = new SequenceWindow();
            _windows[connectionId] = window;
            return window;
        }

        public void Poll(float deltaTime)
        {
            _now += deltaTime;

            if (IsClient && !IsConnected && _now >= _nextConnectAttemptAt) SendConnectRequest();

            _socket.Poll();

            while (_socket.TryReceive(out byte[] payload, out NetEndpoint from))
                Handle(payload, from);

            if (IsServer)
            {
                SendHeartbeats();
                ExpireSilentPeers();
            }
        }

        /// <summary>
        /// Pings every connection on an interval.
        ///
        /// Sent by the server rather than the client so the measurement belongs to the side that
        /// uses it - the parry window is decided server-side, and a round trip reported by a client
        /// would be a number the client chooses. A laggy connection is worth compensating for; a
        /// client claiming to be laggy is worth nothing.
        /// </summary>
        private void SendHeartbeats()
        {
            if (_now < _nextHeartbeatAt) return;
            _nextHeartbeatAt = _now + HeartbeatIntervalSeconds;

            for (int i = 0; i < _connections.Count; i++)
            {
                if (!_peerById.TryGetValue(_connections[i], out NetEndpoint peer)) continue;

                _scratch.Reset();
                HandshakePackets.WritePing(_scratch, _now);
                _socket.Send(peer, _scratch.Buffer, _scratch.Length);
            }
        }

        private void SendConnectRequest()
        {
            // Retried rather than sent once. The request is a datagram like any other and may simply
            // not arrive; a client that asked once and waited for ever would look like a hang.
            _nextConnectAttemptAt = _now + 0.5f;

            _scratch.Reset();
            HandshakePackets.WriteConnectRequest(_scratch);
            _socket.Send(_serverEndpoint, _scratch.Buffer, _scratch.Length);
        }

        private void Handle(byte[] payload, NetEndpoint from)
        {
            _reader.Attach(payload, payload.Length);
            HandshakeMessage message = HandshakePackets.ReadMessage(_reader);

            switch (message)
            {
                case HandshakeMessage.ConnectRequest when IsServer:
                    HandleConnectRequest(payload, from);
                    break;

                case HandshakeMessage.Challenge when IsClient:
                    HandleChallenge();
                    break;

                case HandshakeMessage.ChallengeResponse when IsServer:
                    HandleChallengeResponse(from);
                    break;

                case HandshakeMessage.ConnectAccepted when IsClient:
                    HandleAccepted();
                    break;

                case HandshakeMessage.Payload:
                    HandlePayload(payload, from);
                    break;

                case HandshakeMessage.Ping when IsClient:
                    HandlePing();
                    break;

                case HandshakeMessage.Pong when IsServer:
                    HandlePong(from);
                    break;

                default:
                    // Not ours, or a handshake packet aimed at the wrong role - a challenge sent to
                    // a server, say. A public port receives a great deal of traffic that was never
                    // meant for it, and the right response to all of it is silence: replying would
                    // confirm something is listening here.
                    break;
            }
        }

        private void HandleConnectRequest(byte[] payload, NetEndpoint from)
        {
            if (!_gate.ShouldAnswer(from, payload.Length, _now)) return;

            _scratch.Reset();
            HandshakePackets.WriteChallenge(_scratch, _gate.Challenge(from, _now));
            _socket.Send(from, _scratch.Buffer, _scratch.Length);
        }

        private void HandleChallenge()
        {
            ulong cookie = HandshakePackets.ReadCookie(_reader);

            _scratch.Reset();
            HandshakePackets.WriteChallengeResponse(_scratch, cookie, _requestedName);
            _socket.Send(_serverEndpoint, _scratch.Buffer, _scratch.Length);
        }

        private void HandlePing()
        {
            // Echoed with the server's own timestamp untouched. The client neither knows nor needs
            // to know what that number means.
            float theirClock = _reader.ReadFloat();

            _scratch.Reset();
            HandshakePackets.WritePong(_scratch, theirClock);
            _socket.Send(_serverEndpoint, _scratch.Buffer, _scratch.Length);
        }

        private void HandlePong(NetEndpoint from)
        {
            if (!_idByPeer.TryGetValue(from, out int id)) return;

            float sentAt = _reader.ReadFloat();
            float sample = _now - sentAt;

            // Discarded rather than trusted if it is impossible. The timestamp came back from
            // somewhere else, and a peer that returns a doctored one would otherwise be choosing
            // the latency the server compensates it for - which is the parry window handed to the
            // person it is meant to be fair to.
            if (sample < 0f || sample > 5f) return;

            _gate.Heard(from, _now);

            // Smoothed, because one sample is a coin toss on a real network and the parry window
            // should not swing on a single unlucky packet.
            _rttById[id] = _rttById.TryGetValue(id, out float previous) && previous > 0f
                ? previous * 0.8f + sample * 0.2f
                : sample;
        }

        private void HandleAccepted()
        {
            int id = _reader.ReadInt();
            string granted = _reader.ReadString();

            // Idempotent: the server re-sends its acceptance whenever a response arrives again, so a
            // client will often see this more than once and must not announce itself twice.
            if (IsConnected) return;

            LocalConnectionId = id;
            _nameById[id] = granted;
            IsConnected = true;

            ClientConnected?.Invoke(id);
        }

        private void HandleChallengeResponse(NetEndpoint from)
        {
            ulong cookie = HandshakePackets.ReadCookie(_reader);
            string requested = _reader.ReadString();

            if (!_gate.Accept(from, cookie, _now)) return;

            // Already known: a duplicated response, which UDP produces as a matter of course. The
            // gatekeeper has already refreshed the peer's last-heard time, so there is nothing left
            // to do but not announce them twice.
            if (_idByPeer.TryGetValue(from, out int existing))
            {
                // Re-sent because our acceptance did not arrive, so send it again rather than
                // ignoring them. Silence here is how a client that is already admitted sits
                // retrying for ever against a server that considers the matter settled.
                AcknowledgeAdmission(from, existing);
                return;
            }

            int id = _nextConnectionId++;

            _idByPeer[from] = id;
            _peerById[id] = from;
            _nameById[id] = _roster.Claim(requested);
            _connections.Add(id);

            AcknowledgeAdmission(from, id);
            ClientConnected?.Invoke(id);
        }

        private void AcknowledgeAdmission(NetEndpoint peer, int connectionId)
        {
            _scratch.Reset();
            HandshakePackets.WriteConnectAccepted(_scratch, connectionId, _nameById[connectionId]);
            _socket.Send(peer, _scratch.Buffer, _scratch.Length);
        }

        private void HandlePayload(byte[] payload, NetEndpoint from)
        {
            // The reader is already past the protocol id and message type, so this is the ordering
            // header sitting in front of the game's own bytes.
            PayloadHeader header = HandshakePackets.ReadPayloadHeader(_reader);

            // The game's bytes start after our header. Copied out rather than passed with an offset
            // because INetworkService hands the simulation a buffer and a length, and every reader
            // above this has always started at zero.
            int length = payload.Length - HandshakePackets.PayloadHeaderBytes;
            if (length <= 0) return;

            var body = new byte[length];
            Buffer.BlockCopy(payload, HandshakePackets.PayloadHeaderBytes, body, 0, length);

            if (IsServer)
            {
                // Only from somebody who completed a handshake. A payload from an unknown address
                // is either a stray or somebody hoping the simulation will parse it for them.
                if (!_idByPeer.TryGetValue(from, out int id)) return;

                _gate.Heard(from, _now);

                // Checked before the sequence window, because the point is to stop paying for
                // traffic at all - a packet refused here costs a dictionary lookup rather than a
                // decode and a dispatch into the simulation.
                if (!_gate.ShouldAcceptTraffic(from, _now)) return;

                // Stale or repeated: the packet arrived, and the window has recorded that, but
                // handing it up would apply an older input over a newer one - a player taking a
                // step they had already taken, or taking one backwards.
                if (!WindowFor(id).Accept(header.Sequence)) return;

                ServerReceived?.Invoke(id, body, length);
                return;
            }

            // Same on the client, where the cost is worse: a snapshot that left the server before
            // the one already drawn drags every position backwards for a frame, and what a player
            // sees is rubber-banding they will blame on their connection.
            if (!_inbound.Accept(header.Sequence)) return;

            ClientReceived?.Invoke(body, length);
        }

        private void ExpireSilentPeers()
        {
            IReadOnlyList<NetEndpoint> gone = _gate.Expire(_now);
            if (gone.Count == 0) return;

            for (int i = 0; i < gone.Count; i++)
            {
                NetEndpoint peer = gone[i];
                if (!_idByPeer.TryGetValue(peer, out int id)) continue;

                _idByPeer.Remove(peer);
                _peerById.Remove(id);
                _connections.Remove(id);

                // The name goes back to the pool, or a server that has been up all evening starts
                // handing out mark-2 to the only person called Mark.
                if (_nameById.TryGetValue(id, out string name))
                {
                    _roster.Release(name);
                    _nameById.Remove(id);
                }

                ClientDisconnected?.Invoke(id);
            }
        }

        public void Shutdown()
        {
            Dispose();
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }
}

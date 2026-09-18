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
        private readonly IDatagramSocket _socket;
        private readonly SimulatedSocket _simulated;
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

        /// <summary>The token issued to each live connection, so a returning peer can be matched.</summary>
        private readonly Dictionary<int, ulong> _tokenById = new Dictionary<int, ulong>();

        /// <summary>A name being kept for a player who might come back, and until when.</summary>
        private struct Reservation
        {
            public string Name;
            public float ExpiresAt;
        }

        private readonly Dictionary<ulong, Reservation> _reserved = new Dictionary<ulong, Reservation>();
        private readonly List<ulong> _expiredTokens = new List<ulong>();

        /// <summary>
        /// How long a dropped player's name is kept for them.
        ///
        /// Must outlast any grace period the game keeps their body for, or the body is still
        /// standing when the name it is held against has already been given to somebody else.
        /// PlayerSeatSystem holds a body for sixty seconds; this is deliberately longer, because
        /// the cost of being generous here is one name unavailable for half a minute and the cost
        /// of being mean is a stranger walking into somebody else's ninja.
        /// </summary>
        public const float NameReservationSeconds = 90f;

        private float _nextHeartbeatAt;
        private readonly List<int> _connections = new List<int>();

        private readonly NetWriter _scratch = new NetWriter();
        private readonly NetReader _reader = new NetReader();

        private readonly NetEndpoint _serverEndpoint;
        private readonly string _requestedName;

        /// <summary>
        /// What this client presents to say it is the same player as before, or zero for somebody
        /// arriving for the first time.
        ///
        /// Read back after a connection as <see cref="SessionToken"/> and handed to the next
        /// <see cref="Join"/>. Keeping it is the caller's job, because how long a player's identity
        /// should outlive a process is a question about the game rather than about a socket.
        /// </summary>
        private readonly ulong _presentedToken;

        private float _now;
        private float _nextConnectAttemptAt;
        private int _nextConnectionId = 1;

        private UnseenUdpService(NetRole role, int port, NetEndpoint server, string requestedName,
            NetworkConditions? conditions, ulong presentedToken)
        {
            _presentedToken = presentedToken;
            Role = role;

            // Nullable rather than a perfect-link check, because "no simulation" and "a simulated
            // link that currently happens to be flawless" are different things and only one of
            // them can be degraded later. Asked for nothing, nothing is wrapped: no allocation, no
            // queue, no clock, and the same call path the transport always had.
            var real = new UdpSocket(port);
            _simulated = conditions.HasValue ? new SimulatedSocket(real, conditions.Value) : null;
            _socket = (IDatagramSocket)_simulated ?? real;

            _serverEndpoint = server;
            _requestedName = requestedName;

            // A secret per process, not a constant. A cookie is only unforgeable while the value it
            // is derived from is unknown, and a secret compiled into a build that anybody can
            // download is not a secret at all.
            _gate = new ConnectionGatekeeper(unchecked((ulong)Guid.NewGuid().GetHashCode() * 0x9E3779B97F4A7C15UL));
        }

        /// <summary>
        /// Opens a server on a port, or on one the OS picks when given zero.
        ///
        /// The conditions describe the link to pretend this machine is on. Left out, it is the one
        /// it is actually on.
        /// </summary>
        public static UnseenUdpService Host(int port, NetworkConditions? conditions = null)
        {
            return new UnseenUdpService(NetRole.Server, port, default, null, conditions, 0ul);
        }

        /// <summary>Opens a client and begins asking to join the given server.</summary>
        public static UnseenUdpService Join(NetEndpoint server, string requestedName,
            NetworkConditions? conditions = null, ulong token = 0ul)
        {
            return new UnseenUdpService(NetRole.Client, 0, server, requestedName, conditions, token);
        }

        /// <summary>
        /// The simulated link this end is on, if it was opened with one.
        ///
        /// Settable so a connection can be established cleanly and then degraded, which is the
        /// interesting moment and otherwise unreachable: a link that was always broken never
        /// establishes anything for the breakage to matter to. Setting it on a service opened
        /// without conditions does nothing - there is no simulation in the path to configure.
        /// </summary>
        /// <summary>
        /// Datagrams the simulated link threw away, or zero when there is no simulation.
        ///
        /// So a test that believes it ran under loss can check that loss actually happened. A
        /// suite full of "survives 50% loss" assertions is worth nothing if the weather quietly
        /// stopped working, and that failure is invisible from the assertions themselves.
        /// </summary>
        public int DroppedByLink => _simulated != null ? _simulated.Dropped : 0;

        public NetworkConditions Conditions
        {
            get => _simulated != null ? _simulated.Conditions : default;
            set { if (_simulated != null) _simulated.Conditions = value; }
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

        /// <summary>
        /// What this client should present if it comes back. Zero until the server has admitted it.
        ///
        /// A name is not an identity - anybody can ask to be called Mark - and the game holds an
        /// abandoned body against a name for a minute after its player drops. This is the thing
        /// only the real player has.
        /// </summary>
        public ulong SessionToken { get; private set; }

        public IReadOnlyList<int> Connections => _connections;

        public event Action<int> ClientConnected;
        public event Action<int> ClientDisconnected;
        public event Action<int, byte[], int> ServerReceived;
        public event Action<byte[], int> ClientReceived;

        /// <summary>
        /// Datagrams the path refused to carry - too large to send in one piece.
        ///
        /// Counted because the alternative is what this transport did until now: `UdpSocket.Send`
        /// has reported oversize refusals since it was written, and every one of the dozen call
        /// sites here threw the answer away. The guard that exists to stop a snapshot being
        /// fragmented was instead making it disappear in silence, which is the same failure it was
        /// added to prevent - a player becoming invisible, and nothing anywhere saying why.
        ///
        /// Note the asymmetry with simulated loss, which is deliberate. A dropped datagram is
        /// reported as sent because that is what a real socket does: it hands the bytes to the
        /// operating system and says yes, and nothing ever comes back to say they died three hops
        /// later. A refusal is different in kind - it is this machine declining, before anything
        /// left it, for a reason it knows. That is knowledge a real sender genuinely has, so
        /// throwing it away is a choice rather than a limitation.
        /// </summary>
        public int RefusedSends { get; private set; }

        /// <summary>
        /// Every datagram this service sends goes through here.
        ///
        /// One choke point rather than a dozen call sites each remembering to check a bool. The
        /// last arrangement relied on each of them doing it and all twelve of them did not.
        /// </summary>
        private bool Emit(NetEndpoint to, byte[] payload, int length)
        {
            if (_socket.Send(to, payload, length)) return true;

            RefusedSends++;

            // Logged once. A refusal repeats every tick that produces an oversize snapshot, and a
            // line per tick per player buries the log it was meant to write to.
            if (RefusedSends == 1)
            {
                UnityEngine.Debug.LogError(
                    $"[Unseen] a {length}-byte datagram was refused: the path carries " +
                    $"{UdpSocket.MaxDatagramBytes}. It has not been sent, and it will not arrive.");
            }

            return false;
        }

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

            if (reliable)
            {
                ChannelFor(connectionId).Queue(Copy(payload, length));

                // Sent from here rather than waiting for the next Poll. The first attempt at a
                // message that matters should not be held back by up to a frame for no reason;
                // Poll's job is the resends, which are the part that needs a clock.
                FlushReliable(connectionId, peer);
                return;
            }

            SequenceWindow inbound = WindowFor(connectionId);

            _scratch.Reset();
            HandshakePackets.WritePayload(_scratch, payload, length,
                NextSequence(connectionId), inbound.Latest, inbound.AckBits);
            Emit(peer, _scratch.Buffer, _scratch.Length);
        }

        public void SendToServer(byte[] payload, int length, bool reliable)
        {
            if (!IsConnected) return;

            if (reliable)
            {
                ChannelFor(ServerConnectionId).Queue(Copy(payload, length));
                FlushReliable(ServerConnectionId, _serverEndpoint);
                return;
            }

            _scratch.Reset();
            HandshakePackets.WritePayload(_scratch, payload, length,
                NextSequence(ServerConnectionId), _inbound.Latest, _inbound.AckBits);
            Emit(_serverEndpoint, _scratch.Buffer, _scratch.Length);
        }

        /// <summary>
        /// The caller's bytes, kept.
        ///
        /// A reliable message outlives the call that made it - that is the entire point - and every
        /// sender above this reuses one buffer for the next message. Holding the caller's array
        /// would mean resending whatever happened to be in it a quarter of a second later, which is
        /// a bug that only appears under loss, and then sends the wrong thing.
        /// </summary>
        private static byte[] Copy(byte[] payload, int length)
        {
            var kept = new byte[length];
            Buffer.BlockCopy(payload, 0, kept, 0, length);
            return kept;
        }

        /// <summary>How many messages to this connection are still unconfirmed.</summary>
        public int UnacknowledgedMessages(int connectionId)
        {
            return _reliable.TryGetValue(connectionId, out ReliableChannel channel)
                ? channel.OutstandingCount
                : 0;
        }

        private readonly Dictionary<int, ReliableChannel> _reliable = new Dictionary<int, ReliableChannel>();

        /// <summary>
        /// Reliable message ids seen from each peer, so a resend is not acted on twice.
        ///
        /// Separate from the packet sequence window, because a resend is a new datagram with a new
        /// sequence: the packet window has never seen it and will let it through, correctly. What
        /// must not happen twice is the thing the message describes - an elimination, a zone stage -
        /// and that is what this tracks.
        /// </summary>
        private readonly Dictionary<int, SequenceWindow> _seenReliable = new Dictionary<int, SequenceWindow>();

        private ReliableChannel ChannelFor(int connectionId)
        {
            if (_reliable.TryGetValue(connectionId, out ReliableChannel channel)) return channel;

            channel = new ReliableChannel();
            _reliable[connectionId] = channel;
            return channel;
        }

        private SequenceWindow SeenReliableFor(int connectionId)
        {
            if (_seenReliable.TryGetValue(connectionId, out SequenceWindow window)) return window;

            window = new SequenceWindow();
            _seenReliable[connectionId] = window;
            return window;
        }

        /// <summary>Sends everything this connection is owed: first attempts and expired waits.</summary>
        private void FlushReliable(int connectionId, NetEndpoint peer)
        {
            if (!_reliable.TryGetValue(connectionId, out ReliableChannel channel)) return;

            IReadOnlyList<ReliableChannel.Message> due = channel.Due(_now);
            if (due.Count == 0) return;

            SequenceWindow inbound = connectionId == ServerConnectionId && IsClient
                ? _inbound
                : WindowFor(connectionId);

            for (int i = 0; i < due.Count; i++)
            {
                _scratch.Reset();
                HandshakePackets.WriteReliablePayload(_scratch, due[i].Payload, due[i].Payload.Length,
                    NextSequence(connectionId), inbound.Latest, inbound.AckBits, due[i].Id);
                Emit(peer, _scratch.Buffer, _scratch.Length);
            }
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

            _socket.Poll(deltaTime);

            while (_socket.TryReceive(out byte[] payload, out NetEndpoint from))
                Handle(payload, from);

            ResendReliable();

            if (IsServer)
            {
                SendHeartbeats();
                ExpireSilentPeers();
            }
        }

        /// <summary>
        /// Sends again anything the far end has not confirmed and whose wait has expired.
        ///
        /// The channel decides what is due; this only knows where to put it. A message that is
        /// never acknowledged goes out every quarter second until the connection times out and
        /// takes the whole channel with it, which is the right end for it: at that point the peer
        /// is gone, not merely slow.
        /// </summary>
        private void ResendReliable()
        {
            if (IsServer)
            {
                for (int i = 0; i < _connections.Count; i++)
                {
                    if (_peerById.TryGetValue(_connections[i], out NetEndpoint peer))
                        FlushReliable(_connections[i], peer);
                }

                return;
            }

            if (IsConnected) FlushReliable(ServerConnectionId, _serverEndpoint);
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
                Emit(peer, _scratch.Buffer, _scratch.Length);
            }
        }

        private void SendConnectRequest()
        {
            // Retried rather than sent once. The request is a datagram like any other and may simply
            // not arrive; a client that asked once and waited for ever would look like a hang.
            _nextConnectAttemptAt = _now + 0.5f;

            _scratch.Reset();
            HandshakePackets.WriteConnectRequest(_scratch);
            Emit(_serverEndpoint, _scratch.Buffer, _scratch.Length);
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

                case HandshakeMessage.ReliablePayload:
                    HandleReliablePayload(payload, from);
                    break;

                case HandshakeMessage.ReliableAck:
                    HandleReliableAck(from);
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
            Emit(from, _scratch.Buffer, _scratch.Length);
        }

        private void HandleChallenge()
        {
            ulong cookie = HandshakePackets.ReadCookie(_reader);

            _scratch.Reset();
            HandshakePackets.WriteChallengeResponse(_scratch, cookie, _requestedName,
                SessionToken != 0ul ? SessionToken : _presentedToken);
            Emit(_serverEndpoint, _scratch.Buffer, _scratch.Length);
        }

        private void HandlePing()
        {
            // Echoed with the server's own timestamp untouched. The client neither knows nor needs
            // to know what that number means.
            float theirClock = _reader.ReadFloat();

            _scratch.Reset();
            HandshakePackets.WritePong(_scratch, theirClock);
            Emit(_serverEndpoint, _scratch.Buffer, _scratch.Length);
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
            ulong token = _reader.ReadULong();

            // Idempotent: the server re-sends its acceptance whenever a response arrives again, so a
            // client will often see this more than once and must not announce itself twice.
            if (IsConnected) return;

            LocalConnectionId = id;
            _nameById[id] = granted;
            SessionToken = token;
            IsConnected = true;

            ClientConnected?.Invoke(id);
        }

        private void HandleChallengeResponse(NetEndpoint from)
        {
            ulong cookie = HandshakePackets.ReadCookie(_reader);
            string requested = _reader.ReadString();
            ulong presented = _reader.ReadULong();

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
            _connections.Add(id);

            // A token that matches a name still being kept is the player coming back, and they get
            // their name rather than a fresh claim on it - which would hand them "Mark-2" while
            // their own body still stands in the street labelled "Mark".
            //
            // Anything else is somebody new, whatever they have asked to be called. This is the
            // whole difference between an identity and a request: a stranger typing the right name
            // during the window gets a suffix, because the name is not available to be claimed.
            if (presented != 0ul && _reserved.TryGetValue(presented, out Reservation held) &&
                _now < held.ExpiresAt)
            {
                _reserved.Remove(presented);
                _nameById[id] = held.Name;
                _tokenById[id] = presented;
            }
            else
            {
                _nameById[id] = _roster.Claim(requested);
                _tokenById[id] = NewSessionToken();
            }

            AcknowledgeAdmission(from, id);
            ClientConnected?.Invoke(id);
        }

        /// <summary>
        /// A value a client could not have guessed, and could not have been given by anything it
        /// typed. Derived from a fresh GUID rather than a counter or a hash of the name.
        /// </summary>
        private static ulong NewSessionToken()
        {
            Guid guid = Guid.NewGuid();
            byte[] bytes = guid.ToByteArray();
            return BitConverter.ToUInt64(bytes, 0) ^ BitConverter.ToUInt64(bytes, 8);
        }

        private void AcknowledgeAdmission(NetEndpoint peer, int connectionId)
        {
            _tokenById.TryGetValue(connectionId, out ulong token);

            _scratch.Reset();
            HandshakePackets.WriteConnectAccepted(_scratch, connectionId, _nameById[connectionId], token);
            Emit(peer, _scratch.Buffer, _scratch.Length);
        }

        /// <summary>
        /// A reliable payload: confirm it, then hand it up only if it has not been seen before.
        ///
        /// Acknowledged before the duplicate check, and deliberately. A copy arriving means the
        /// first acknowledgement was lost, not that the sender is confused - so the one thing it
        /// must not do is stay silent, or the sender goes on resending a message that arrived the
        /// first time for as long as the connection lasts.
        /// </summary>
        private void HandleReliablePayload(byte[] payload, NetEndpoint from)
        {
            PayloadHeader header = HandshakePackets.ReadPayloadHeader(_reader);
            ushort messageId = _reader.ReadUShort();

            // Only from somebody who completed a handshake, same as any other payload.
            int connectionId = ServerConnectionId;
            if (IsServer && !_idByPeer.TryGetValue(from, out connectionId)) return;

            _scratch.Reset();
            HandshakePackets.WriteReliableAck(_scratch, messageId);
            Emit(from, _scratch.Buffer, _scratch.Length);

            if (!SeenReliableFor(connectionId).Accept(messageId)) return;

            // Not run through the packet ordering window. A resend is a fresh datagram carrying a
            // message that is already old, so the window would reject it for being out of order -
            // discarding, for lateness, the one kind of message whose whole purpose is to arrive
            // eventually. Its own id has already established that this is not a repeat.
            Deliver(payload, from, HandshakePackets.ReliablePayloadHeaderBytes,
                header.Sequence, checkOrder: false);
        }

        private void HandleReliableAck(NetEndpoint from)
        {
            ushort messageId = _reader.ReadUShort();

            int connectionId = ServerConnectionId;
            if (IsServer && !_idByPeer.TryGetValue(from, out connectionId)) return;

            if (_reliable.TryGetValue(connectionId, out ReliableChannel channel))
                channel.Acknowledge(messageId);
        }

        private void HandlePayload(byte[] payload, NetEndpoint from)
        {
            // The reader is already past the protocol id and message type, so this is the ordering
            // header sitting in front of the game's own bytes.
            PayloadHeader header = HandshakePackets.ReadPayloadHeader(_reader);

            Deliver(payload, from, HandshakePackets.PayloadHeaderBytes,
                header.Sequence, checkOrder: true);
        }

        /// <summary>
        /// Hands a payload's game bytes up to the simulation, having established who sent it.
        ///
        /// <paramref name="checkOrder"/> is false only for reliable messages, which have already
        /// been deduplicated by their own id and must not then be dropped for arriving late.
        /// </summary>
        private void Deliver(byte[] payload, NetEndpoint from, int headerBytes,
            ushort sequence, bool checkOrder)
        {
            // The game's bytes start after our header. Copied out rather than passed with an offset
            // because INetworkService hands the simulation a buffer and a length, and every reader
            // above this has always started at zero.
            int length = payload.Length - headerBytes;
            if (length <= 0) return;

            var body = new byte[length];
            Buffer.BlockCopy(payload, headerBytes, body, 0, length);

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
                if (checkOrder && !WindowFor(id).Accept(sequence)) return;

                ServerReceived?.Invoke(id, body, length);
                return;
            }

            // Same on the client, where the cost is worse: a snapshot that left the server before
            // the one already drawn drags every position backwards for a frame, and what a player
            // sees is rubber-banding they will blame on their connection.
            if (checkOrder && !_inbound.Accept(sequence)) return;

            ClientReceived?.Invoke(body, length);
        }

        /// <summary>Gives up names nobody came back for.</summary>
        private void ExpireReservations()
        {
            if (_reserved.Count == 0) return;

            _expiredTokens.Clear();

            foreach (KeyValuePair<ulong, Reservation> kv in _reserved)
                if (_now >= kv.Value.ExpiresAt) _expiredTokens.Add(kv.Key);

            for (int i = 0; i < _expiredTokens.Count; i++)
            {
                _roster.Release(_reserved[_expiredTokens[i]].Name);
                _reserved.Remove(_expiredTokens[i]);
            }
        }

        private void ExpireSilentPeers()
        {
            ExpireReservations();

            IReadOnlyList<NetEndpoint> gone = _gate.Expire(_now);
            if (gone.Count == 0) return;

            for (int i = 0; i < gone.Count; i++)
            {
                NetEndpoint peer = gone[i];
                if (!_idByPeer.TryGetValue(peer, out int id)) continue;

                _idByPeer.Remove(peer);
                _peerById.Remove(id);
                _connections.Remove(id);

                // The name is kept for a while rather than released outright.
                //
                // Releasing it immediately was the hole under the game's grace period: a body is
                // held for a minute against the name its player was using, and the name was back in
                // the pool the instant they dropped. Anybody could then join, ask to be called
                // Mark, and be handed Mark's ninja.
                //
                // Still released in the end, or a server up all evening starts handing out mark-2
                // to the only person called Mark.
                if (_nameById.TryGetValue(id, out string name))
                {
                    if (_tokenById.TryGetValue(id, out ulong token) && token != 0ul)
                    {
                        _reserved[token] = new Reservation
                        {
                            Name = name,
                            ExpiresAt = _now + NameReservationSeconds
                        };
                    }
                    else
                    {
                        _roster.Release(name);
                    }

                    _nameById.Remove(id);
                }

                _tokenById.Remove(id);

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

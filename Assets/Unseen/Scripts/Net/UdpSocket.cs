using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Unseen.Net
{
    /// <summary>
    /// A non-blocking UDP socket, and nothing else.
    ///
    /// Deliberately shallow. Every decision worth getting wrong - who may connect, what a packet
    /// means, how fast one address may ask - lives above this in code that needs no network to
    /// test. What is left here is the part that genuinely cannot be tested without one, so the
    /// less of it there is, the better.
    /// </summary>
    public sealed class UdpSocket : IDisposable
    {
        private readonly Socket _socket;
        private readonly byte[] _receiveBuffer = new byte[2048];
        private EndPoint _from = new IPEndPoint(IPAddress.Any, 0);

        /// <summary>
        /// Datagrams drained from the socket and waiting to be handed out.
        ///
        /// A queue rather than a single slot. Holding one meant a poll surfaced one packet, so a
        /// caller polling once a frame processed sixty a second while sixty-four players sent
        /// thousands - the rest sat in the kernel buffer until it overflowed and the OS discarded
        /// them, which on the wire is indistinguishable from packet loss and would have been blamed
        /// on the network for a long time.
        /// </summary>
        private readonly Queue<Datagram> _received = new Queue<Datagram>();

        private struct Datagram
        {
            public byte[] Payload;
            public NetEndpoint From;
        }

        /// <summary>
        /// Most datagrams taken in one poll.
        ///
        /// Draining without a limit hands an attacker the frame time: a flood would keep the loop
        /// reading until it stopped, and the server would stall rather than drop. A cap means a
        /// burst is spread over a few frames and anything beyond what the machine can carry is
        /// discarded by the kernel, which is the right place for it to be discarded.
        /// </summary>
        public const int MaxReceivesPerPoll = 256;

        /// <summary>Opens on the given port, or on one the OS picks when given zero.</summary>
        public UdpSocket(int port)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                Blocking = false
            };

            // A datagram to a closed port provokes an ICMP unreachable, and Windows surfaces that on
            // the next receive as a connection-reset error - on a connectionless socket, for a
            // packet that was never part of a connection. Left alone it kills the receive loop of a
            // server whose client has quit, which is an ordinary event.
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (PlatformNotSupportedException)
            {
                // Not Windows. The behaviour being suppressed does not exist there.
            }

            _socket.Bind(new IPEndPoint(IPAddress.Any, port));

            var bound = (IPEndPoint)_socket.LocalEndPoint;
            LocalEndpoint = new NetEndpoint(
                ToAddress(IPAddress.Loopback), (ushort)bound.Port);
        }

        /// <summary>Where this socket can be reached, on loopback.</summary>
        public NetEndpoint LocalEndpoint { get; }

        /// <summary>Sends a datagram. Fire and forget; UDP promises nothing and neither does this.</summary>
        public void Send(NetEndpoint to, byte[] payload, int length)
        {
            var target = new IPEndPoint(ToIp(to.Address), to.Port);

            try
            {
                _socket.SendTo(payload, 0, length, SocketFlags.None, target);
            }
            catch (SocketException)
            {
                // A send that fails is a packet that was lost, which is a thing UDP does anyway.
            }
        }

        /// <summary>Drains everything waiting on the socket, up to the per-poll cap.</summary>
        public void Poll()
        {
            for (int i = 0; i < MaxReceivesPerPoll; i++)
            {
                try
                {
                    if (_socket.Available <= 0) return;

                    int read = _socket.ReceiveFrom(_receiveBuffer, ref _from);
                    if (read <= 0) return;

                    var payload = new byte[read];
                    Buffer.BlockCopy(_receiveBuffer, 0, payload, 0, read);

                    var sender = (IPEndPoint)_from;

                    _received.Enqueue(new Datagram
                    {
                        Payload = payload,
                        From = new NetEndpoint(ToAddress(sender.Address), (ushort)sender.Port)
                    });
                }
                catch (SocketException)
                {
                    // Nothing to read, or a stray error about a peer that has gone. Neither is
                    // fatal to a socket expected to keep serving everybody else - and the loop
                    // stops rather than spinning on a socket that is complaining.
                    return;
                }
            }
        }

        /// <summary>Takes the next datagram, if one is waiting.</summary>
        public bool TryReceive(out byte[] payload, out NetEndpoint from)
        {
            if (_received.Count == 0)
            {
                payload = null;
                from = default;
                return false;
            }

            Datagram next = _received.Dequeue();
            payload = next.Payload;
            from = next.From;
            return true;
        }

        public void Dispose()
        {
            try { _socket.Close(); }
            catch (SocketException) { }
        }

        private static uint ToAddress(IPAddress ip)
        {
            byte[] bytes = ip.MapToIPv4().GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                   ((uint)bytes[2] << 8) | bytes[3];
        }

        private static IPAddress ToIp(uint address)
        {
            return new IPAddress(new[]
            {
                (byte)(address >> 24), (byte)((address >> 16) & 0xFF),
                (byte)((address >> 8) & 0xFF), (byte)(address & 0xFF)
            });
        }
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Unseen.Net
{
    /// <summary>A server heard on the local network, and when it was last heard from.</summary>
    public struct FoundServer
    {
        public string Name;
        public string Address;
        public int Players;
        public int Capacity;
        public float LastHeardAt;

        public string Describe() => $"{Name}  {Players}/{Capacity}  {Address}";
    }

    /// <summary>
    /// Shouting on the local network, and listening for other people shouting.
    ///
    /// One class for both ends because they are one feature and the halves are meaningless apart:
    /// a beacon nobody listens for and a listener with nothing to hear are equally useless, and
    /// keeping them together means the port and the tag cannot drift.
    ///
    /// Everything here fails quietly. Broadcast is refused on some networks, the port may be taken
    /// by another copy of the game on the same machine, and a laptop may have no usable interface
    /// at all - none of which should stop somebody playing. The worst honest outcome is an empty
    /// server list and a box to type an address into, which is exactly where the game was before
    /// any of this existed.
    /// </summary>
    public sealed class LanDiscovery : IDisposable
    {
        /// <summary>
        /// How long a server stays in the list after its last beacon.
        ///
        /// Three beacons' worth. One missed datagram is ordinary on a busy wireless network and
        /// should not make a server flicker out of the list under somebody's cursor; three missed
        /// in a row means it has genuinely gone.
        /// </summary>
        public const float ForgetAfterSeconds = 3.5f;

        /// <summary>How often a server announces itself.</summary>
        public const float AnnounceIntervalSeconds = 1f;

        private readonly UdpClient _socket;
        private readonly NetWriter _writer = new NetWriter(128);
        private readonly NetReader _reader = new NetReader();
        private readonly Dictionary<string, FoundServer> _found = new Dictionary<string, FoundServer>();

        private float _now;
        private float _nextAnnounceAt;

        /// <summary>
        /// This process's mark on its own beacons, so it can ignore them coming back.
        ///
        /// Broadcast returns to the machine that sent it, so without this a listen server browsing
        /// for games finds itself at the top of the list. Per process, not per machine: two copies
        /// on one machine are two servers and ought to see each other, which is how anybody tests
        /// this alone.
        /// </summary>
        private readonly int _origin = Guid.NewGuid().GetHashCode();

        /// <summary>Whether the socket came up at all. False is survivable, not fatal.</summary>
        public bool IsUsable { get; }

        /// <summary>Why it is not usable, for a line in the menu rather than a silent empty list.</summary>
        public string Problem { get; private set; }

        public LanDiscovery(bool listening)
        {
            try
            {
                _socket = new UdpClient();
                _socket.EnableBroadcast = true;
                _socket.Client.SetSocketOption(SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress, true);

                // A listener binds the well-known port; a server that only shouts does not, so two
                // copies on one machine can both advertise without fighting over it.
                if (listening)
                    _socket.Client.Bind(new IPEndPoint(IPAddress.Any, LanBeacon.BeaconPort));

                _socket.Client.Blocking = false;
                IsUsable = true;
            }
            catch (SocketException e)
            {
                // Port taken, broadcast refused, no interface. All ordinary on somebody's laptop,
                // and none of them a reason to stop them playing.
                Problem = e.Message;
                IsUsable = false;
            }
        }

        /// <summary>Servers heard recently, newest information first seen.</summary>
        public IEnumerable<FoundServer> Servers => _found.Values;

        public int Count => _found.Count;

        /// <summary>
        /// Announces this server, if it is time, and takes in whatever has arrived.
        ///
        /// <paramref name="name"/> and the counts are passed every call rather than stored, because
        /// they change while the server runs and a beacon carrying a stale player count is worse
        /// than one carrying none: somebody joins what the list called empty and finds a full match.
        /// </summary>
        public void Poll(float deltaTime, string announceName, int gamePort, int players, int capacity)
        {
            if (!IsUsable) return;

            _now += deltaTime;

            if (!string.IsNullOrEmpty(announceName) && _now >= _nextAnnounceAt)
            {
                _nextAnnounceAt = _now + AnnounceIntervalSeconds;
                Announce(announceName, gamePort, players, capacity);
            }

            Receive();
            Forget();
        }

        private void Announce(string name, int gamePort, int players, int capacity)
        {
            _writer.Reset();
            LanBeacon.Write(_writer, name, gamePort, players, capacity, _origin);

            try
            {
                var everybody = new IPEndPoint(IPAddress.Broadcast, LanBeacon.BeaconPort);
                _socket.Send(_writer.Buffer, _writer.Length, everybody);
            }
            catch (SocketException)
            {
                // Some networks refuse broadcast outright. Nothing to be done, and nothing worth
                // stopping for.
            }
        }

        private void Receive()
        {
            for (int i = 0; i < 16; i++)
            {
                try
                {
                    if (_socket.Available <= 0) return;

                    IPEndPoint from = null;
                    byte[] datagram = _socket.Receive(ref from);

                    _reader.Attach(datagram, datagram.Length);
                    if (!LanBeacon.Read(_reader, out LanBeacon beacon)) continue;

                    // Our own voice, come back off the network.
                    if (beacon.Origin == _origin) continue;

                    // The address comes from where the datagram arrived from, and the port from
                    // inside it. A server cannot be trusted to know its own external address, and
                    // a beacon that claimed one would be a way to point a whole room at somebody
                    // else's machine.
                    string address = $"{from.Address}:{beacon.GamePort}";

                    _found[address] = new FoundServer
                    {
                        Name = beacon.Name,
                        Address = address,
                        Players = beacon.Players,
                        Capacity = beacon.Capacity,
                        LastHeardAt = _now
                    };
                }
                catch (SocketException)
                {
                    return;
                }
            }
        }

        private readonly List<string> _stale = new List<string>();

        private void Forget()
        {
            _stale.Clear();

            foreach (KeyValuePair<string, FoundServer> kv in _found)
                if (_now - kv.Value.LastHeardAt > ForgetAfterSeconds) _stale.Add(kv.Key);

            for (int i = 0; i < _stale.Count; i++) _found.Remove(_stale[i]);
        }

        public void Dispose()
        {
            try { _socket?.Close(); }
            catch (SocketException) { }
        }
    }
}

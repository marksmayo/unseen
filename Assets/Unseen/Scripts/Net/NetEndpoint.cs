using System;

namespace Unseen.Net
{
    /// <summary>
    /// A peer's address and port, as a value.
    ///
    /// Deliberately not System.Net.IPEndPoint: this is compared and hashed on every inbound packet,
    /// including the flood of unauthenticated ones a public server has to survive, and a struct
    /// costs nothing to pass or key a dictionary by.
    /// </summary>
    public readonly struct NetEndpoint : IEquatable<NetEndpoint>
    {
        public readonly uint Address;
        public readonly ushort Port;

        public NetEndpoint(uint address, ushort port)
        {
            Address = address;
            Port = port;
        }

        public bool Equals(NetEndpoint other) => Address == other.Address && Port == other.Port;

        public override bool Equals(object obj) => obj is NetEndpoint other && Equals(other);

        public override int GetHashCode() => unchecked((int)(Address * 397u) ^ Port);

        public override string ToString() =>
            $"{Address >> 24}.{(Address >> 16) & 0xFF}.{(Address >> 8) & 0xFF}.{Address & 0xFF}:{Port}";
    }
}

using System.Net;

namespace Unseen.Net
{
    /// <summary>
    /// Turns "host:port" into somewhere to send a packet.
    ///
    /// Small, but it is the first parsing the game does and it runs on text from a command line,
    /// which in practice means a shortcut somebody edited, a launcher, or a server browser. The
    /// contract is that bad input is refused, never thrown on: an exception here happens before
    /// there is a log to read, and "it closes instantly" is the least useful bug report there is.
    /// </summary>
    public static class ConnectTarget
    {
        /// <summary>Parses an address, or returns false and leaves the endpoint alone.</summary>
        public static bool TryParse(string text, out NetEndpoint endpoint)
        {
            endpoint = default;

            if (string.IsNullOrWhiteSpace(text)) return false;

            int colon = text.LastIndexOf(':');
            if (colon <= 0 || colon == text.Length - 1) return false;

            string host = text.Substring(0, colon);
            string portText = text.Substring(colon + 1);

            // TryParse rather than Parse throughout. Every one of these is reachable from a typo.
            if (!int.TryParse(portText, out int port)) return false;

            // Zero is a valid port number and a meaningless destination - it means "let the OS
            // choose" when binding, so a client accepting it would send packets nowhere and report
            // nothing wrong.
            if (port <= 0 || port > 65535) return false;

            if (!IPAddress.TryParse(host, out IPAddress address)) return false;

            byte[] bytes = address.MapToIPv4().GetAddressBytes();
            if (bytes.Length != 4) return false;

            uint packed = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                          ((uint)bytes[2] << 8) | bytes[3];

            endpoint = new NetEndpoint(packed, (ushort)port);
            return true;
        }
    }
}

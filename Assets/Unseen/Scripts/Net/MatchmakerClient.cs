using System;
using System.Net.Sockets;
using System.Text;

namespace Unseen.Net
{
    /// <summary>
    /// The game's side of the matchmaker conversation.
    ///
    /// Every call returns whether it worked rather than throwing. The matchmaker is a service on
    /// the internet reached by an address a human typed, so being unreachable is an ordinary state
    /// and not an exceptional one - and a client that throws on it puts the normal case into a
    /// catch block, where it gets logged and forgotten.
    ///
    /// Synchronous, with a short timeout. Asking is one short line each way every couple of
    /// seconds; the complexity of doing it asynchronously buys nothing at that rate, and a blocking
    /// call with a two-second ceiling is easier to reason about than a state machine that can be
    /// halfway through a request when the player quits.
    /// </summary>
    public sealed class MatchmakerClient
    {
        private readonly string _host;
        private readonly int _port;

        /// <summary>
        /// How long to wait for the service before calling it unreachable.
        ///
        /// Short on purpose. A matchmaker that is thinking for more than two seconds is one the
        /// player should be told about, not one worth waiting for in silence.
        /// </summary>
        public const int TimeoutMilliseconds = 2000;

        public MatchmakerClient(string endpoint)
        {
            int colon = endpoint != null ? endpoint.LastIndexOf(':') : -1;

            if (colon > 0 && int.TryParse(endpoint.Substring(colon + 1), out int port))
            {
                _host = endpoint.Substring(0, colon);
                _port = port;
            }
            else
            {
                _host = endpoint;
                _port = 80;
            }
        }

        /// <summary>Joins the queue. The ticket is how everything afterwards refers to this player.</summary>
        public bool TryQueue(out string ticket)
        {
            ticket = null;

            if (!TryRequest("POST /queue HTTP/1.1", out string body)) return false;
            if (!MatchmakerProtocol.ReadStatus(body, out MatchmakerReply reply)) return false;

            ticket = reply.Ticket;
            return !string.IsNullOrEmpty(ticket);
        }

        /// <summary>Asks what has become of a ticket.</summary>
        public bool TryStatus(string ticket, out MatchmakerReply reply)
        {
            reply = default;

            if (string.IsNullOrEmpty(ticket)) return false;
            if (!TryRequest($"GET /status?ticket={ticket} HTTP/1.1", out string body)) return false;

            return MatchmakerProtocol.ReadStatus(body, out reply);
        }

        /// <summary>Gives up a place in the queue, or a seat in a match.</summary>
        public bool TryLeave(string ticket)
        {
            if (string.IsNullOrEmpty(ticket)) return false;

            return TryRequest($"POST /leave?ticket={ticket} HTTP/1.1", out string _);
        }

        /// <summary>Sends a request line verbatim and hands back the body. For tests and probes.</summary>
        public bool TryRaw(string requestLine, out string body)
        {
            return TryRequest(requestLine, out body);
        }

        private bool TryRequest(string requestLine, out string body)
        {
            body = null;

            try
            {
                using (var connection = new TcpClient())
                {
                    connection.SendTimeout = TimeoutMilliseconds;
                    connection.ReceiveTimeout = TimeoutMilliseconds;
                    connection.Connect(_host, _port);

                    NetworkStream stream = connection.GetStream();

                    string request = requestLine + "\r\nHost: " + _host + "\r\nConnection: close\r\n\r\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(request);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();

                    body = ReadBody(stream);
                    return body != null;
                }
            }
            catch (SocketException)
            {
                // Unreachable, refused, or timed out. All ordinary, all the same answer.
                return false;
            }
            catch (System.IO.IOException)
            {
                return false;
            }
        }

        private static string ReadBody(NetworkStream stream)
        {
            var buffer = new byte[4096];
            var whole = new StringBuilder(256);
            int read;

            // Read until the far end closes, which it does after every reply. That is what makes
            // this safe without parsing Content-Length: the connection ending is the delimiter.
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                whole.Append(Encoding.UTF8.GetString(buffer, 0, read));

            string text = whole.ToString();

            int blank = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (blank < 0) blank = text.IndexOf("\n\n", StringComparison.Ordinal);

            // Headers and body, or nothing worth returning. A reply with no blank line in it is not
            // an HTTP response, whatever else it is.
            return blank < 0 ? null : text.Substring(blank).TrimStart('\r', '\n');
        }
    }
}

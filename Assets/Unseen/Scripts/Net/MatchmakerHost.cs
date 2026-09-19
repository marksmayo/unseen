using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Unseen.Net
{
    /// <summary>
    /// The matchmaker's front door: enough HTTP for a game client to queue and ask.
    ///
    /// Hand-rolled on a TcpListener rather than HttpListener, which on Windows wants a URL
    /// reservation and refuses with an access error for anyone who has not run a command as
    /// administrator. That is a dependency on the state of a machine rather than on the code, and
    /// the HTTP actually needed here is a request line, a couple of headers and a short body.
    ///
    /// The host owns its loop. A client asking is a blocking call - it connects and waits - so
    /// nothing can answer it from the thread that is waiting, and a host pumped by somebody else's
    /// loop only works for a caller with nothing else to do. Started, it takes a thread of its own
    /// and the matchmaker belongs to that thread: which is what lets the part with all the
    /// decisions in it stay plain single-threaded code with no locks anywhere near it.
    ///
    /// Every response closes its connection. Keep-alive is a performance feature for pages with
    /// forty assets on them, and this is one short reply every few seconds per waiting player.
    /// </summary>
    public sealed class MatchmakerHost : IDisposable
    {
        private readonly Matchmaker _matchmaker;
        private readonly TcpListener _listener;

        private System.Threading.Thread _thread;
        private volatile bool _running;

        /// <summary>Most requests answered in one Poll, so a flood cannot own the loop.</summary>
        public const int MaxRequestsPerPoll = 32;

        /// <summary>Longest request line and headers we will read before giving up.</summary>
        private const int MaxRequestBytes = 4096;

        public MatchmakerHost(Matchmaker matchmaker, int port)
        {
            _matchmaker = matchmaker;

            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        /// <summary>The port actually bound. Meaningful when constructed with zero.</summary>
        public int Port { get; }

        /// <summary>
        /// Runs the accept loop on a thread of its own until disposed.
        ///
        /// The host owns the loop rather than being pumped by somebody else's, because a client
        /// asking is a blocking call: it connects and waits for an answer, and nothing can answer
        /// it from the same thread that is waiting. Polling from outside works only for a caller
        /// that has nothing else to do, which is nobody.
        ///
        /// The matchmaker belongs to this thread once started. That is the whole reason it has no
        /// locks in it: the part with the decisions is plain single-threaded code, reachable only
        /// through the door.
        /// </summary>
        public void Start()
        {
            if (_running) return;

            _running = true;
            _thread = new System.Threading.Thread(Loop) { IsBackground = true, Name = "matchmaker" };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                // Shutting down races this thread by construction: Dispose stops the listener while
                // the loop may be inside it, and a stopped listener throws rather than returning
                // false. Ending quietly is the correct response - the socket being gone is exactly
                // the instruction to stop - and letting it escape would log an exception on every
                // clean shutdown, which teaches everybody to ignore exceptions in the log.
                try
                {
                    Poll();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    // Listener stopped between the check and the call.
                    return;
                }

                // Idle rather than spin. A matchmaker answers a handful of short requests a second
                // and has no business owning a core to do it.
                System.Threading.Thread.Sleep(2);
            }
        }

        /// <summary>Requests answered since it started. For a health line.</summary>
        public int Served { get; private set; }

        /// <summary>
        /// Answers whatever is waiting, then moves the queue on.
        ///
        /// Pumping after serving rather than before: a request that queued somebody should be able
        /// to assign them in the same breath, so a player who arrives when a server has room is
        /// told where to go by their very next poll rather than the one after.
        /// </summary>
        public void Poll()
        {
            for (int i = 0; i < MaxRequestsPerPoll && _listener.Pending(); i++)
            {
                try
                {
                    using (TcpClient connection = _listener.AcceptTcpClient())
                    {
                        Serve(connection);
                        Served++;
                    }
                }
                catch (SocketException)
                {
                    // A client that hung up mid-request. Ordinary on a public port, and not a
                    // reason to stop serving everybody else.
                }
                catch (System.IO.IOException)
                {
                }
            }

            _matchmaker.Pump();
        }

        private void Serve(TcpClient connection)
        {
            NetworkStream stream = connection.GetStream();
            stream.ReadTimeout = 2000;
            stream.WriteTimeout = 2000;

            string request = ReadRequestLine(stream);
            if (request == null) return;

            string body = Route(request, out int code);

            var reply = new StringBuilder(160);
            reply.Append("HTTP/1.1 ").Append(code).Append(code == 200 ? " OK" : " Not Found").Append("\r\n");
            reply.Append("Content-Type: ").Append(MatchmakerProtocol.ContentType).Append("\r\n");
            reply.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n");
            reply.Append("Connection: close\r\n\r\n");
            reply.Append(body);

            byte[] bytes = Encoding.UTF8.GetBytes(reply.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>
        /// Reads the request line and discards the headers.
        ///
        /// Bounded, because the first line of a request is attacker-controlled text arriving on a
        /// public port. An unbounded read here is a way to make the service hold a connection open
        /// and a buffer growing for as long as somebody cares to keep typing.
        /// </summary>
        private static string ReadRequestLine(NetworkStream stream)
        {
            var buffer = new byte[MaxRequestBytes];
            int read = 0;
            string firstLine = null;

            while (read < buffer.Length)
            {
                int got;

                try { got = stream.Read(buffer, read, buffer.Length - read); }
                catch (System.IO.IOException) { return firstLine; }

                if (got <= 0) break;
                read += got;

                string text = Encoding.UTF8.GetString(buffer, 0, read);

                if (firstLine == null)
                {
                    int endOfLine = text.IndexOf('\n');
                    if (endOfLine >= 0) firstLine = text.Substring(0, endOfLine).Trim();
                }

                // Headers finished. Nothing here reads a body, so there is no reason to wait for
                // one - and waiting for a body that is not coming is how a request hangs.
                if (text.Contains("\r\n\r\n") || text.Contains("\n\n")) break;
            }

            return firstLine;
        }

        private string Route(string requestLine, out int code)
        {
            code = 200;

            string path = PathOf(requestLine);

            if (path.StartsWith("/queue"))
            {
                string ticket = _matchmaker.Enqueue();

                // Pumped here so a player who arrives to an empty fleet is assigned by this very
                // request rather than having to ask again for something that was already true.
                _matchmaker.Pump();

                MatchmakerStatus status = _matchmaker.StatusOf(ticket, out string address);
                return MatchmakerProtocol.WriteStatus(status, ticket, address, _matchmaker.Waiting);
            }

            if (path.StartsWith("/status"))
            {
                string ticket = QueryValue(path, "ticket");
                MatchmakerStatus status = _matchmaker.StatusOf(ticket, out string address);
                return MatchmakerProtocol.WriteStatus(status, ticket, address, _matchmaker.Waiting);
            }

            if (path.StartsWith("/leave"))
            {
                string ticket = QueryValue(path, "ticket");
                _matchmaker.Leave(ticket);
                return MatchmakerProtocol.WriteStatus(MatchmakerStatus.Unknown, ticket, null,
                    _matchmaker.Waiting);
            }

            // Anything else. A public port gets scanners, health checkers and browsers, and none of
            // them should be able to queue somebody or make this throw.
            code = 404;
            return "status=Unknown\nticket=\nqueue=" + _matchmaker.Waiting + "\n";
        }

        private static string PathOf(string requestLine)
        {
            if (string.IsNullOrEmpty(requestLine)) return string.Empty;

            string[] parts = requestLine.Split(' ');
            return parts.Length >= 2 ? parts[1] : string.Empty;
        }

        private static string QueryValue(string path, string key)
        {
            int question = path.IndexOf('?');
            if (question < 0) return null;

            string[] pairs = path.Substring(question + 1).Split('&');

            for (int i = 0; i < pairs.Length; i++)
            {
                int equals = pairs[i].IndexOf('=');
                if (equals <= 0) continue;

                if (pairs[i].Substring(0, equals) == key) return pairs[i].Substring(equals + 1);
            }

            return null;
        }

        public void Dispose()
        {
            _running = false;

            try { _listener.Stop(); }
            catch (SocketException) { }

            // Joined rather than abandoned, so a test that disposes a host and then reads the
            // matchmaker is not racing a thread that is still serving a request against it.
            if (_thread != null && _thread.IsAlive) _thread.Join(500);
        }
    }
}

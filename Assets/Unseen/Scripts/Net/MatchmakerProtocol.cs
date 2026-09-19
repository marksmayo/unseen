using System;
using System.Text;

namespace Unseen.Net
{
    /// <summary>One reply from the matchmaker, parsed.</summary>
    public struct MatchmakerReply
    {
        public MatchmakerStatus Status;
        public string Ticket;
        public string Address;

        /// <summary>How many are waiting, so a client can show a number rather than a spinner.</summary>
        public int QueueLength;
    }

    /// <summary>
    /// What the matchmaker and a client say to each other.
    ///
    /// Written and read side by side, like the snapshot format and for the same reason: the wire is
    /// where two programs agree, and an agreement is the thing that drifts silently. A reply the
    /// client cannot read is indistinguishable from a matchmaker that is down, and the player is
    /// told "could not find a match" either way.
    ///
    /// Line-oriented text rather than JSON. It is three short fields, the project has no JSON
    /// dependency and does not want one for this, and a format you can read in a log or a packet
    /// capture without a tool is worth more here than one that scales to a schema nobody has.
    /// </summary>
    public static class MatchmakerProtocol
    {
        /// <summary>Content type for the HTTP reply. Plain text, because that is what it is.</summary>
        public const string ContentType = "text/plain; charset=utf-8";

        public static string WriteStatus(MatchmakerStatus status, string ticket, string address,
            int queueLength)
        {
            var text = new StringBuilder(96);

            text.Append("status=").Append(status).Append('\n');
            text.Append("ticket=").Append(ticket ?? string.Empty).Append('\n');
            text.Append("queue=").Append(queueLength).Append('\n');

            if (!string.IsNullOrEmpty(address)) text.Append("address=").Append(address).Append('\n');

            return text.ToString();
        }

        /// <summary>
        /// Reads a reply, or refuses it.
        ///
        /// False for anything that is not plainly one of ours. A client points at an address a
        /// human typed, so the likeliest bodies on the day something is wrong are a proxy error
        /// page, a truncated response, or an entirely different web server - and every one of those
        /// must come back as "no" rather than as a half-filled struct.
        /// </summary>
        public static bool ReadStatus(string body, out MatchmakerReply reply)
        {
            reply = default;

            if (string.IsNullOrEmpty(body)) return false;

            string statusWord = null;
            string[] lines = body.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;

                int split = line.IndexOf('=');
                if (split <= 0) continue;

                string key = line.Substring(0, split);
                string value = line.Substring(split + 1);

                switch (key)
                {
                    case "status": statusWord = value; break;
                    case "ticket": reply.Ticket = value; break;
                    case "address": reply.Address = value; break;

                    case "queue":
                        if (int.TryParse(value, out int waiting)) reply.QueueLength = waiting;
                        break;
                }
            }

            // Parsed by name, and refused when the name is not one this build knows.
            //
            // Not defaulted. A newer matchmaker saying something cautious that an older client
            // does not recognise must not be read as permission to connect - which is exactly
            // what falling back to a default would do.
            if (!Enum.TryParse(statusWord, out MatchmakerStatus status)) return false;
            if (!Enum.IsDefined(typeof(MatchmakerStatus), status)) return false;

            reply.Status = status;

            // A reply about no ticket is not about anybody.
            if (string.IsNullOrEmpty(reply.Ticket)) return false;

            // An assignment has to be somewhere a transport can actually dial. Caught here so the
            // failure is one clear refusal rather than a connection attempt that times out and
            // reads as the game server being down.
            if (status == MatchmakerStatus.Assigned && !LooksLikeAnEndpoint(reply.Address))
                return false;

            return true;
        }

        private static bool LooksLikeAnEndpoint(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;

            int colon = address.LastIndexOf(':');
            if (colon <= 0 || colon == address.Length - 1) return false;

            return int.TryParse(address.Substring(colon + 1), out int port) &&
                   port > 0 && port <= 65535;
        }
    }
}

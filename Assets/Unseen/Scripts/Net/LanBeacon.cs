namespace Unseen.Net
{
    /// <summary>
    /// A server shouting its name across the local network, and what a client hears.
    ///
    /// Typing an address is fine for whoever started the server and hostile to everybody else in
    /// the room. A beacon turns "ask Mark what his IP is" into a list you click, which is often the
    /// difference between a LAN game happening and not happening.
    ///
    /// Deliberately tiny. Every server on the subnet sends one of these every second to everybody,
    /// so it carries what a player needs to choose - a name, how full it is - and nothing else.
    /// </summary>
    public struct LanBeacon
    {
        /// <summary>
        /// The port servers shout on. Distinct from the game port so a beacon can never be mistaken
        /// for game traffic, or arrive at a socket that will try to parse it as a snapshot.
        /// </summary>
        public const int BeaconPort = 7778;

        /// <summary>
        /// Marks a datagram as one of ours.
        ///
        /// A broadcast port is shared with whatever else on the network feels like shouting -
        /// other games, discovery protocols, a printer announcing itself. Four known bytes make
        /// discarding all of that free, and stop a stray datagram becoming a server in the list
        /// that goes nowhere.
        /// </summary>
        public const int Tag = 0x554E5342; // "UNSB"

        /// <summary>Longest server name carried. Beyond this it is cut, not refused.</summary>
        public const int MaxNameLength = 24;

        public string Name;

        /// <summary>The port to actually connect to, which is not the port this arrived on.</summary>
        public ushort GamePort;

        public byte Players;
        public byte Capacity;

        /// <summary>
        /// Who sent it, so a listener can ignore its own shouting.
        ///
        /// Broadcast comes back to the machine that sent it, so a listen server browsing for games
        /// finds itself sitting at the top of the list. Harmless and obviously wrong, which is the
        /// worst combination: it is the first thing anybody notices and it makes the feature look
        /// untrustworthy before they have tried it.
        ///
        /// Per process rather than per machine. Two copies on one machine are two servers and
        /// should see each other, which is exactly how somebody tests this on their own.
        /// </summary>
        public int Origin;

        public static void Write(NetWriter writer, string name, int port, int players, int capacity,
            int origin)
        {
            writer.WriteInt(Tag);
            writer.WriteInt(origin);

            // Sanitised on the way out and cut to length.
            //
            // A server name is typed by whoever started it and displayed on everybody else's
            // screen, which makes it the same problem as a player name: a newline forges a second
            // entry in the list, zero-width padding makes one server look like another, and a bidi
            // override rewrites the line around it. PlayerName already knows all of that.
            string clean = PlayerName.Sanitise(name);
            if (clean.Length > MaxNameLength) clean = clean.Substring(0, MaxNameLength);

            writer.WriteString(clean);
            writer.WriteUShort((ushort)port);
            writer.WriteByte((byte)(players > 255 ? 255 : players));
            writer.WriteByte((byte)(capacity > 255 ? 255 : capacity));
        }

        public static bool Read(NetReader reader, out LanBeacon beacon)
        {
            beacon = default;

            if (reader.ReadInt() != Tag) return false;

            beacon.Origin = reader.ReadInt();
            beacon.Name = PlayerName.Sanitise(reader.ReadString());
            beacon.GamePort = reader.ReadUShort();
            beacon.Players = reader.ReadByte();
            beacon.Capacity = reader.ReadByte();

            // A server on port zero is not somewhere anybody can go.
            return beacon.GamePort != 0;
        }

        /// <summary>One line for a server list.</summary>
        public string Describe() => $"{Name}  {Players}/{Capacity}";
    }
}

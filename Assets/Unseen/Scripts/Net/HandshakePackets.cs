namespace Unseen.Net
{
    /// <summary>What a handshake packet is.</summary>
    public enum HandshakeMessage : byte
    {
        /// <summary>Unrecognisable. Anything that is not one of the three below.</summary>
        Unknown = 0,

        /// <summary>A stranger asking to connect. Padded; see HandshakePackets.</summary>
        ConnectRequest = 1,

        /// <summary>The server's cookie, sent to the address the request claimed to come from.</summary>
        Challenge = 2,

        /// <summary>The cookie echoed back, which is what proves the address was not forged.</summary>
        ChallengeResponse = 3,

        /// <summary>
        /// You are in. Carries the connection id and the name the server settled on.
        ///
        /// A distinct message rather than an echoed challenge: a client that cannot tell "here is a
        /// cookie" from "you are admitted" answers both the same way, and server and client trade
        /// the same two packets for ever.
        /// </summary>
        ConnectAccepted = 4,

        /// <summary>
        /// Game traffic - an input or a snapshot - wrapped so it is identifiable.
        ///
        /// Payloads are tagged rather than being "whatever failed to parse as a handshake". Treating
        /// unrecognised bytes as game data would hand the simulation anything that arrived from an
        /// established peer's address, including a stray datagram from something else entirely, and
        /// that is precisely the parsing surface the rest of this protocol is careful to deny.
        /// </summary>
        Payload = 5,

        /// <summary>
        /// A heartbeat carrying the sender's clock, to be echoed back unchanged.
        ///
        /// Two jobs in one packet. It measures the round trip, which the parry window needs, and it
        /// is traffic - so a player standing perfectly still and sending no input is not mistaken
        /// for one whose connection has died.
        /// </summary>
        Ping = 6,

        /// <summary>A heartbeat returned, carrying the timestamp it came with.</summary>
        Pong = 7
    }

    /// <summary>
    /// The three packets of the handshake, as bytes.
    ///
    /// Deliberately separate from <see cref="ConnectionGatekeeper"/>: that decides who may connect,
    /// this decides what the packets look like. Apart, the policy can be tested without a buffer
    /// and the format without a socket.
    /// </summary>
    public static class HandshakePackets
    {
        /// <summary>
        /// Marks a packet as belonging to this game and this version of the handshake.
        ///
        /// A public UDP port receives a great deal of traffic that was never meant for it - scans,
        /// stray packets, other people's protocols. Checking four known bytes before doing anything
        /// else makes discarding all of that nearly free.
        /// </summary>
        public const int ProtocolId = 0x554E5301; // "UNS" + version 1

        /// <summary>
        /// Writes a request to connect, padded so it cannot be used to amplify.
        ///
        /// Carries nothing but its identity and its padding. This is the one packet anybody on the
        /// internet can send from any address they care to claim, so the less that is read out of
        /// it before the address is proven, the smaller the surface. The player's name travels in
        /// the response instead, which only a peer that echoed the cookie can send.
        ///
        /// The padding is the defence expressed in the format itself: the gatekeeper refuses
        /// anything under <see cref="ConnectionGatekeeper.MinimumRequestBytes"/>, so an unpadded
        /// request would be turned away by our own server. The rule is enforced from both ends by
        /// one constant, which is what stops the two drifting apart.
        /// </summary>
        public static void WriteConnectRequest(NetWriter writer)
        {
            writer.WriteInt(ProtocolId);
            writer.WriteByte((byte)HandshakeMessage.ConnectRequest);

            while (writer.Length < ConnectionGatekeeper.MinimumRequestBytes) writer.WriteByte(0);
        }

        /// <summary>
        /// Writes the cookie back to whoever asked. Deliberately tiny - this is the packet the
        /// amplification argument is about, and every byte added here is leverage handed to an
        /// attacker aiming our server at somebody else.
        /// </summary>
        public static void WriteChallenge(NetWriter writer, ulong cookie)
        {
            writer.WriteInt(ProtocolId);
            writer.WriteByte((byte)HandshakeMessage.Challenge);
            writer.WriteULong(cookie);
        }

        /// <summary>
        /// Echoes the cookie back, and says what this player would like to be called.
        ///
        /// The name travels here rather than in the request because this is the first packet from a
        /// peer that has proved its address - the request is unauthenticated and anybody can send
        /// one claiming to be anybody, so the less that is parsed out of it the better. It also has
        /// to arrive at the moment the connection is admitted: a server that admits a player and
        /// only learns their name afterwards has to invent a placeholder and then visibly rename
        /// them a moment later, for everyone, at the start of every match.
        /// </summary>
        public static void WriteChallengeResponse(NetWriter writer, ulong cookie, string requestedName)
        {
            writer.WriteInt(ProtocolId);
            writer.WriteByte((byte)HandshakeMessage.ChallengeResponse);
            writer.WriteULong(cookie);
            writer.WriteString(requestedName);
        }

        /// <summary>
        /// Tells a client it is in, which id it holds, and what it ended up being called.
        ///
        /// The name is returned rather than assumed because the server may not have granted the one
        /// that was asked for - it is sanitised, and it may have collided with somebody already
        /// here. A client that displayed the name it requested would show its player one thing while
        /// everybody else saw another.
        /// </summary>
        public static void WriteConnectAccepted(NetWriter writer, int connectionId, string grantedName)
        {
            writer.WriteInt(ProtocolId);
            writer.WriteByte((byte)HandshakeMessage.ConnectAccepted);
            writer.WriteInt(connectionId);
            writer.WriteString(grantedName);
        }

        /// <summary>Bytes written before a game payload: protocol id and message type.</summary>
        public const int PayloadHeaderBytes = 5;

        /// <summary>Wraps a game payload so the receiver can tell it is ours and what it is.</summary>
        public static void WritePayload(NetWriter writer, byte[] payload, int length)
        {
            writer.WriteInt(ProtocolId);
            writer.WriteByte((byte)HandshakeMessage.Payload);

            for (int i = 0; i < length; i++) writer.WriteByte(payload[i]);
        }

        /// <summary>Writes a heartbeat stamped with the sender's clock.</summary>
        public static void WritePing(NetWriter writer, float sentAt)
        {
            writer.WriteInt(ProtocolId);
            writer.WriteByte((byte)HandshakeMessage.Ping);
            writer.WriteFloat(sentAt);
        }

        /// <summary>
        /// Returns a heartbeat with the timestamp it arrived with, unaltered.
        ///
        /// The sender's own clock goes back rather than the responder's. Neither machine knows the
        /// other's clock and neither needs to: the sender subtracts what it gets back from what it
        /// reads now, and the difference is a round trip measured entirely against one clock.
        /// </summary>
        public static void WritePong(NetWriter writer, float theirSentAt)
        {
            writer.WriteInt(ProtocolId);
            writer.WriteByte((byte)HandshakeMessage.Pong);
            writer.WriteFloat(theirSentAt);
        }

        /// <summary>Reads a cookie, from a challenge or from the response echoing it.</summary>
        public static ulong ReadCookie(NetReader reader)
        {
            return reader.ReadULong();
        }

        /// <summary>Reads the message type, having checked the packet is ours at all.</summary>
        public static HandshakeMessage ReadMessage(NetReader reader)
        {
            if (reader.ReadInt() != ProtocolId) return HandshakeMessage.Unknown;

            byte kind = reader.ReadByte();

            return kind >= (byte)HandshakeMessage.ConnectRequest &&
                   kind <= (byte)HandshakeMessage.Pong
                ? (HandshakeMessage)kind
                : HandshakeMessage.Unknown;
        }
    }
}

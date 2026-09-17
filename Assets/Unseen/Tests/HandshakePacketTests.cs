using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The three packets of the handshake, as bytes on the wire.
    ///
    /// Separate from <see cref="ConnectionGatekeeper"/>, which decides who may connect: this decides
    /// what the packets look like. Keeping them apart means the policy can be tested without a
    /// buffer and the format without a socket.
    /// </summary>
    public sealed class HandshakePacketTests
    {
        [Test]
        public void AConnectRequestIsRecognisableAndPadded()
        {
            var writer = new NetWriter();
            HandshakePackets.WriteConnectRequest(writer);

            // The padding is not decoration, it is the anti-amplification defence expressed in the
            // format itself: the gatekeeper refuses anything smaller, so a request that is not
            // padded here would be refused by our own server.
            Assert.GreaterOrEqual(writer.Length, ConnectionGatekeeper.MinimumRequestBytes,
                "a connect request must be at least as large as the reply it asks for");

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            Assert.AreEqual(HandshakeMessage.ConnectRequest, HandshakePackets.ReadMessage(reader));
        }

        [Test]
        public void AChallengeCarriesTheCookieIntact()
        {
            // Every bit matters and the top one especially: a cookie is compared for exact equality,
            // so a format that loses or mangles the high bits would refuse every honest player while
            // looking perfectly reasonable in a debugger on small test values.
            const ulong cookie = 0xF1E2D3C4B5A69788UL;

            var writer = new NetWriter();
            HandshakePackets.WriteChallenge(writer, cookie);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            Assert.AreEqual(HandshakeMessage.Challenge, HandshakePackets.ReadMessage(reader));
            Assert.AreEqual(cookie, HandshakePackets.ReadCookie(reader));
        }

        [Test]
        public void AChallengeIsSmallerThanTheRequestThatEarnedIt()
        {
            var request = new NetWriter();
            HandshakePackets.WriteConnectRequest(request);

            var challenge = new NetWriter();
            HandshakePackets.WriteChallenge(challenge, 0xDEADBEEFUL);

            // The whole amplification argument in one assertion. If this ever inverts, the server
            // becomes a reflector that multiplies an attacker's traffic at somebody else's address.
            Assert.Less(challenge.Length, request.Length,
                "the reply must never be larger than what provoked it");
        }

        [Test]
        public void AResponseCarriesTheCookieAndTheNameTogether()
        {
            const ulong cookie = 0x0123456789ABCDEFUL;

            var writer = new NetWriter();
            HandshakePackets.WriteChallengeResponse(writer, cookie, "Mark");

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            // Both in one packet on purpose. The name has to arrive at the moment the connection is
            // admitted, or the server has a connected player with nothing to call them and has to
            // invent a placeholder that some later packet then changes - a rename nobody asked for,
            // visible to everyone, for the first second of every match.
            Assert.AreEqual(HandshakeMessage.ChallengeResponse, HandshakePackets.ReadMessage(reader));
            Assert.AreEqual(cookie, HandshakePackets.ReadCookie(reader));
            Assert.AreEqual("Mark", reader.ReadString());
        }

        [Test]
        public void APayloadCarriesItsSequenceAndWhatTheSenderHasSeen()
        {
            var writer = new NetWriter();
            var body = new byte[] { 7, 7, 7 };

            HandshakePackets.WritePayload(writer, body, body.Length,
                sequence: 1000, ack: 950, ackBits: 0xDEADBEEF);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            Assert.AreEqual(HandshakeMessage.Payload, HandshakePackets.ReadMessage(reader));

            // Three facts on every packet: which one this is, the newest the sender has received,
            // and which of the thirty-two before that arrived. That is the whole acknowledgement
            // scheme - a receiver learns what was lost without a message per packet, and can throw
            // away anything older than what it already holds.
            PayloadHeader header = HandshakePackets.ReadPayloadHeader(reader);

            Assert.AreEqual(1000, header.Sequence);
            Assert.AreEqual(950, header.Ack);
            Assert.AreEqual(0xDEADBEEFu, header.AckBits);
        }
    }
}

using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// How a server on the same network announces itself.
    ///
    /// Typing an address is fine for the person who started the server and hostile to everybody
    /// else in the room. A beacon turns "ask Mark what his IP is" into a list you click, which is
    /// the difference between a LAN game happening and not happening.
    ///
    /// The format is tested as bytes because it is a wire format, and the failure mode is the usual
    /// one: a beacon a client cannot read is indistinguishable from no server at all, and the
    /// player is told the same either way.
    /// </summary>
    public sealed class LanBeaconTests
    {
        [Test]
        public void ABeaconSurvivesTheRoundTrip()
        {
            var writer = new NetWriter();
            LanBeacon.Write(writer, "Mark's game", port: 7770, players: 3, capacity: 64, origin: 77);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            Assert.IsTrue(LanBeacon.Read(reader, out LanBeacon beacon));

            Assert.AreEqual("Mark's game", beacon.Name);
            Assert.AreEqual(7770, beacon.GamePort);
            Assert.AreEqual(3, beacon.Players);
            Assert.AreEqual(64, beacon.Capacity);
            Assert.AreEqual(77, beacon.Origin, "and who sent it, so a host can ignore itself");
        }

        [Test]
        public void SomethingElseOnThePortIsNotAServer()
        {
            // A broadcast port is shared with whatever else on the network feels like shouting -
            // other games, discovery protocols, a printer. Reading one of those as a server puts a
            // line in the list that goes nowhere, and the player blames the game.
            var writer = new NetWriter();
            writer.WriteInt(0x0BADF00D);
            writer.WriteString("not us");

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            Assert.IsFalse(LanBeacon.Read(reader, out LanBeacon _));
        }

        [Test]
        public void AServerNameIsSanitisedLikeAnyOtherPlayerFacingText()
        {
            // The name is typed by whoever started the server and displayed on everybody else's
            // screen, which makes it exactly the same problem as a player name: control characters,
            // bidi overrides, a hundred zero-width spaces padding it into somebody else's entry.
            var writer = new NetWriter();
            LanBeacon.Write(writer, "evil\nserver", port: 7770, players: 0, capacity: 64, origin: 1);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            Assert.IsTrue(LanBeacon.Read(reader, out LanBeacon beacon));
            Assert.IsFalse(beacon.Name.Contains("\n"), "a server name cannot forge a second line");
        }

        [Test]
        public void ABeaconIsSmallEnoughToBroadcastOften()
        {
            var writer = new NetWriter();
            LanBeacon.Write(writer, new string('x', 200), port: 7770, players: 64, capacity: 64, origin: 1);

            // Sent every second to the whole subnet, by every server on it. A beacon that carried
            // an unbounded name would be a way for one machine to flood a network on a timer.
            Assert.Less(writer.Length, 128,
                "a beacon is an advertisement, not a payload");
        }
    }
}

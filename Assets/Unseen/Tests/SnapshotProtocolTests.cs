using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The front of every snapshot: what it is, what version, when, and which of this client's
    /// inputs the server has acted on.
    ///
    /// Tested here rather than through <c>EncodeSnapshot</c> because that needs a live scene - a
    /// registered agent with its components awake, and the interest manager's output to encode.
    /// The header is the part that can silently drift, since it is read back by offset: add a field
    /// to one side and every field after it decodes as garbage. One definition, used by both sides,
    /// is what stops that, and this is the test that holds it to it.
    /// </summary>
    public sealed class SnapshotHeaderTests
    {
        [Test]
        public void TheHeaderCarriesTheAcknowledgedInput()
        {
            var writer = new NetWriter();
            SnapshotProtocol.WriteHeader(writer, 900, 15.5f, 4242u);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            var snapshot = new SnapshotData();
            Assert.IsTrue(SnapshotProtocol.ReadHeader(reader, snapshot), "a header it just wrote");

            Assert.AreEqual(900, snapshot.Tick);
            Assert.AreEqual(15.5f, snapshot.ServerTime, 0.001f);

            // The number the whole of reconciliation rests on. Without it a client cannot know
            // which predictions to retire, so it either replays its entire backlog for ever or
            // abandons prediction and wears the round trip on every step it takes.
            Assert.AreEqual(4242u, snapshot.AcknowledgedInput);
        }

        [Test]
        public void AHeaderFromAnotherVersionIsRefused()
        {
            // Not politeness - a snapshot from a different build is read by offset against the
            // wrong layout, so it does not fail, it decodes. Positions land in health, the entity
            // count is read out of the middle of a float, and the client draws a world that was
            // never sent. Refusing on the version byte is the only cheap place to catch it.
            var writer = new NetWriter();
            writer.WriteByte((byte)NetMessage.Snapshot);
            writer.WriteByte((byte)(SnapshotProtocol.Version + 1));
            writer.WriteInt(900);
            writer.WriteFloat(15.5f);
            writer.WriteUInt(4242u);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            Assert.IsFalse(SnapshotProtocol.ReadHeader(reader, new SnapshotData()));
        }

        [Test]
        public void AnAcknowledgementSurvivesTheTopOfTheSequenceRange()
        {
            // Input sequences are unsigned and count up for the life of a connection. Routed
            // through a signed int anywhere on the way, the top of the range comes back negative
            // and the client reads it as "nothing has been acknowledged" - a backlog that only
            // grows, and movement that feels worse the longer somebody stays connected.
            var writer = new NetWriter();
            SnapshotProtocol.WriteHeader(writer, 1, 0f, uint.MaxValue - 1u);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            var snapshot = new SnapshotData();
            Assert.IsTrue(SnapshotProtocol.ReadHeader(reader, snapshot));
            Assert.AreEqual(uint.MaxValue - 1u, snapshot.AcknowledgedInput);
        }
    }
}

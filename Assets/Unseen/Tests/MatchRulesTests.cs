using NUnit.Framework;
using Unseen.Net;

namespace Unseen.Tests
{
    /// <summary>
    /// The rules a match runs under, and who decides them.
    ///
    /// The interesting property here is not that the numbers survive a round trip - it is that the
    /// server's copy wins. A client that keeps its own values is a client choosing its own zone
    /// damage and match length, which in a competitive match is simply cheating with extra steps.
    /// </summary>
    public sealed class MatchRulesTests
    {
        [Test]
        public void RulesSurviveTheWire()
        {
            var sent = new MatchRules
            {
                TargetEntityCount = 32,
                ZoneStages = 5,
                MistDamagePerSecond = 4.5f,
                FinalZoneRadius = 18f
            };

            var writer = new NetWriter(64);
            sent.Write(writer);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);
            MatchRules received = MatchRules.Read(reader);

            Assert.AreEqual(32, received.TargetEntityCount);
            Assert.AreEqual(5, received.ZoneStages);
            Assert.AreEqual(4.5f, received.MistDamagePerSecond, 1e-4f);
            Assert.AreEqual(18f, received.FinalZoneRadius, 1e-4f);
        }

        [Test]
        public void TheServersRulesReplaceWhateverTheClientHadLocally()
        {
            // A client that has edited its own config - which is a file on its own disk, so this is
            // not a hypothetical - arrives believing the mist is harmless and the match is tiny.
            var local = new MatchRules
            {
                TargetEntityCount = 2,
                ZoneStages = 1,
                MistDamagePerSecond = 0f,
                FinalZoneRadius = 9999f
            };

            var authoritative = new MatchRules
            {
                TargetEntityCount = 64,
                ZoneStages = 7,
                MistDamagePerSecond = 2.5f,
                FinalZoneRadius = 28f
            };

            MatchRules applied = MatchRules.Adopt(local, authoritative);

            // Written as "nothing local survived" rather than "the values equal what was sent",
            // because the second phrasing passes even when the apply is a no-op on a client that
            // happened to agree. Every field is checked: the realistic failure is not a wholesale
            // mistake but one field forgotten in a hand-written copy, which would look like a
            // desync and get diagnosed as anything but a client keeping its own rule.
            Assert.AreNotEqual(local.TargetEntityCount, applied.TargetEntityCount);
            Assert.AreNotEqual(local.ZoneStages, applied.ZoneStages);
            Assert.AreNotEqual(local.MistDamagePerSecond, applied.MistDamagePerSecond);
            Assert.AreNotEqual(local.FinalZoneRadius, applied.FinalZoneRadius);

            Assert.AreEqual(authoritative.TargetEntityCount, applied.TargetEntityCount);
            Assert.AreEqual(authoritative.ZoneStages, applied.ZoneStages);
            Assert.AreEqual(authoritative.MistDamagePerSecond, applied.MistDamagePerSecond, 1e-4f);
            Assert.AreEqual(authoritative.FinalZoneRadius, applied.FinalZoneRadius, 1e-4f);
        }
    }

    /// <summary>
    /// The number that makes reconciliation possible: which of a client's inputs the server has
    /// actually acted on.
    ///
    /// The server has always known it - ReplicationSystem keeps a last-sequence per connection to
    /// reject out-of-order inputs - and has never told anybody. Without it a client cannot know
    /// which predictions to retire, so it either replays everything for ever or trusts the server's
    /// position blindly and gives up on prediction.
    /// </summary>
    public sealed class InputAcknowledgementTests
    {
        [Test]
        public void TheAcknowledgedInputSurvivesTheWire()
        {
            var writer = new NetWriter();

            // Written where the self block lives, because it is a fact about you and nobody else:
            // every client needs a different number here, and it is meaningless to anyone but its
            // owner.
            SnapshotProtocol.WriteAcknowledgedInput(writer, 4242u);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            Assert.AreEqual(4242u, SnapshotProtocol.ReadAcknowledgedInput(reader));
        }

        [Test]
        public void AnAcknowledgementSurvivesTheSequenceWrapping()
        {
            var writer = new NetWriter();
            SnapshotProtocol.WriteAcknowledgedInput(writer, uint.MaxValue - 1u);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            // Input sequences are unsigned and count up for the life of a connection. Routed through
            // a signed int on the way out, the top of the range comes back negative and the client
            // decides the server has acknowledged nothing - retiring no predictions and replaying a
            // backlog that only grows.
            Assert.AreEqual(uint.MaxValue - 1u, SnapshotProtocol.ReadAcknowledgedInput(reader));
        }
    }
}

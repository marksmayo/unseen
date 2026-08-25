using NUnit.Framework;
using Unity.Mathematics;
using Unseen.Core;
using Unseen.Net;

namespace Unseen.Tests
{
    public sealed class NetStreamTests
    {
        [Test]
        public void PrimitivesRoundTrip()
        {
            var writer = new NetWriter(64);
            writer.WriteByte(200);
            writer.WriteBool(true);
            writer.WriteUShort(45000);
            writer.WriteInt(-123456789);
            writer.WriteFloat(3.14159f);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            Assert.AreEqual(200, reader.ReadByte());
            Assert.IsTrue(reader.ReadBool());
            Assert.AreEqual(45000, reader.ReadUShort());
            Assert.AreEqual(-123456789, reader.ReadInt());
            Assert.AreEqual(3.14159f, reader.ReadFloat(), 1e-6f);
        }

        [Test]
        public void PositionQuantisationStaysWithinOneStep()
        {
            const float quantum = 0.01f;
            var expected = new float3(123.456f, -8.912f, 1024.007f);

            var writer = new NetWriter();
            writer.WritePosition(expected, quantum);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);
            float3 actual = reader.ReadPosition(quantum);

            Assert.Less(math.distance(expected, actual), quantum * 2f);
        }

        [Test]
        public void AngleQuantisationIsWithinResolution()
        {
            // One byte over 360 degrees is about 1.41 degrees per step.
            const float tolerance = 1.5f;

            for (float angle = 0f; angle < 360f; angle += 17f)
            {
                var writer = new NetWriter(8);
                writer.WriteAngle(angle);

                var reader = new NetReader();
                reader.Attach(writer.Buffer, writer.Length);

                float decoded = reader.ReadAngle();
                Assert.Less(math.abs(UnseenMath.YawDelta(angle, decoded)), tolerance, $"angle {angle}");
            }
        }

        [Test]
        public void InputRoundTripsEveryButton()
        {
            var intent = new MoveIntent
            {
                Sequence = 4242,
                Move = new float2(-1f, 0.5f),
                Yaw = 217f,
                Pitch = -35f,
                Sprint = true,
                Crouch = false,
                Jump = true,
                Grapple = false,
                Interact = true,
                AttackLight = false,
                AttackHeavy = true,
                Guard = true,
                Zone = GuardZone.Low,
                UseUtility = 2
            };

            var writer = new NetWriter();
            SnapshotProtocol.EncodeInput(writer, intent);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);

            Assert.IsTrue(SnapshotProtocol.DecodeInput(reader, out MoveIntent decoded));
            Assert.AreEqual(intent.Sequence, decoded.Sequence);
            Assert.AreEqual(intent.Move.x, decoded.Move.x, 0.01f);
            Assert.AreEqual(intent.Move.y, decoded.Move.y, 0.01f);
            Assert.Less(math.abs(UnseenMath.YawDelta(intent.Yaw, decoded.Yaw)), 1.5f);
            Assert.AreEqual(intent.Pitch, decoded.Pitch, 1.5f);
            Assert.IsTrue(decoded.Sprint);
            Assert.IsFalse(decoded.Crouch);
            Assert.IsTrue(decoded.Jump);
            Assert.IsFalse(decoded.Grapple);
            Assert.IsTrue(decoded.Interact);
            Assert.IsFalse(decoded.AttackLight);
            Assert.IsTrue(decoded.AttackHeavy);
            Assert.IsTrue(decoded.Guard);
            Assert.AreEqual(GuardZone.Low, decoded.Zone);
            Assert.AreEqual(2, decoded.UseUtility);
        }

        [Test]
        public void DirectionRoundTripsApproximately()
        {
            float3 expected = math.normalize(new float3(0.4f, -0.6f, 0.7f));

            var writer = new NetWriter();
            writer.WriteDirection(expected);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);
            float3 actual = reader.ReadDirection();

            Assert.Greater(math.dot(expected, actual), 0.985f);
        }

        [Test]
        public void WriterGrowsBeyondItsInitialCapacity()
        {
            var writer = new NetWriter(8);
            for (int i = 0; i < 200; i++) writer.WriteInt(i);

            var reader = new NetReader();
            reader.Attach(writer.Buffer, writer.Length);
            for (int i = 0; i < 200; i++) Assert.AreEqual(i, reader.ReadInt());
        }
    }
}

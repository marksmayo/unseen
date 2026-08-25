using System;
using Unity.Mathematics;
using Unseen.Core;

namespace Unseen.Net
{
    public enum NetMessage : byte
    {
        Snapshot = 1,
        Input = 2,
        MatchState = 3,
        Hello = 4
    }

    /// <summary>
    /// Byte-oriented writer with the quantisation the snapshot format needs. Deliberately simple
    /// and readable: positions are centimetre-quantised ints, angles are single bytes, and
    /// normalised quantities are bytes. That is well under the bandwidth ceiling for 64 players
    /// once the interest manager has stripped everything a client cannot see.
    /// </summary>
    public sealed class NetWriter
    {
        private byte[] _buffer;
        private int _position;

        public NetWriter(int capacity = 2048)
        {
            _buffer = new byte[capacity];
        }

        public byte[] Buffer => _buffer;
        public int Length => _position;

        public void Reset()
        {
            _position = 0;
        }

        private void Ensure(int bytes)
        {
            if (_position + bytes <= _buffer.Length) return;
            int size = _buffer.Length * 2;
            while (size < _position + bytes) size *= 2;
            Array.Resize(ref _buffer, size);
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _buffer[_position++] = value;
        }

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUShort(ushort value)
        {
            Ensure(2);
            _buffer[_position++] = (byte)(value & 0xFF);
            _buffer[_position++] = (byte)(value >> 8);
        }

        public void WriteShort(short value) => WriteUShort(unchecked((ushort)value));

        public void WriteInt(int value)
        {
            Ensure(4);
            _buffer[_position++] = (byte)(value & 0xFF);
            _buffer[_position++] = (byte)((value >> 8) & 0xFF);
            _buffer[_position++] = (byte)((value >> 16) & 0xFF);
            _buffer[_position++] = (byte)((value >> 24) & 0xFF);
        }

        public void WriteFloat(float value)
        {
            WriteInt(BitConverter.SingleToInt32Bits(value));
        }

        /// <summary>Writes a 0..1 quantity in one byte.</summary>
        public void WriteNormalised(float value)
        {
            WriteByte((byte)math.clamp(math.round(math.saturate(value) * 255f), 0f, 255f));
        }

        /// <summary>Writes an angle in one byte at ~1.4 degree resolution.</summary>
        public void WriteAngle(float degrees)
        {
            float wrapped = degrees % 360f;
            if (wrapped < 0f) wrapped += 360f;
            WriteByte((byte)math.clamp(math.round(wrapped / 360f * 255f), 0f, 255f));
        }

        /// <summary>Writes a world position quantised to the configured step.</summary>
        public void WritePosition(float3 position, float quantum)
        {
            float inv = 1f / math.max(quantum, 0.0001f);
            WriteInt((int)math.round(position.x * inv));
            WriteInt((int)math.round(position.y * inv));
            WriteInt((int)math.round(position.z * inv));
        }

        /// <summary>Writes a unit vector as two bytes: yaw plus signed elevation.</summary>
        public void WriteDirection(float3 direction)
        {
            WriteAngle(UnseenMath.ForwardToYaw(direction));
            WriteByte((byte)math.clamp(math.round((math.clamp(direction.y, -1f, 1f) * 0.5f + 0.5f) * 255f), 0f, 255f));
        }
    }

    public sealed class NetReader
    {
        private byte[] _buffer;
        private int _position;
        private int _length;

        public void Attach(byte[] buffer, int length)
        {
            _buffer = buffer;
            _length = length;
            _position = 0;
        }

        public bool HasMore => _position < _length;
        public int Position => _position;

        public byte ReadByte() => _position < _length ? _buffer[_position++] : (byte)0;
        public bool ReadBool() => ReadByte() != 0;

        public ushort ReadUShort()
        {
            int lo = ReadByte();
            int hi = ReadByte();
            return (ushort)(lo | (hi << 8));
        }

        public short ReadShort() => unchecked((short)ReadUShort());

        public int ReadInt()
        {
            int b0 = ReadByte();
            int b1 = ReadByte();
            int b2 = ReadByte();
            int b3 = ReadByte();
            return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
        }

        public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt());

        public float ReadNormalised() => ReadByte() / 255f;

        public float ReadAngle() => ReadByte() / 255f * 360f;

        public float3 ReadPosition(float quantum)
        {
            float x = ReadInt() * quantum;
            float y = ReadInt() * quantum;
            float z = ReadInt() * quantum;
            return new float3(x, y, z);
        }

        public float3 ReadDirection()
        {
            float yaw = ReadAngle();
            float elevation = ReadByte() / 255f * 2f - 1f;
            float3 flat = UnseenMath.YawToForward(yaw);
            float horizontal = math.sqrt(math.max(0f, 1f - elevation * elevation));
            return new float3(flat.x * horizontal, elevation, flat.z * horizontal);
        }
    }
}

using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;

namespace silkgltriangle;

/// <summary>The message id on the wire (the first byte of every framed payload).</summary>
public enum NetMessage : byte
{
    ClientHello = 1,
    ServerHello = 2,
    KeepAlive = 3,
    ChatSend = 10,
    ChatMessage = 11,
    PlayerState = 20,
    PlayerList = 21,
    PlayerJoined = 22,
    PlayerLeft = 23,
    BlockEdit = 30,
    BlockChange = 31,
    TimeSync = 40,
    ChunkRequest = 50,
    ChunkData = 51,
    ColumnRequest = 52,
    ColumnHeights = 53,
    LodRegionRequest = 54,
    LodRegionData = 55,
    BlockChanges = 56,
    EntityList = 60,
    InventoryState = 70,
    InventoryRequest = 71,
}

/// <summary>
/// A client's inventory intent the server applies authoritatively to that player's own inventory
/// (see <see cref="InventoryIntent"/> for each one's fields and <see cref="InventoryIntentRules"/>
/// for what the server accepts).
/// </summary>
public enum NetInventoryAction : byte
{
    SelectSlot = 0,
    DropSelected = 1,
    DropAllSelected = 2,
    Equip = 3,
    Craft = 4,
    CreativeGrant = 5,
    CreativeClear = 6,
    PickBlock = 7,
}

/// <summary>
/// Length-prefixed framing over a byte stream: each message is a 4-byte little-endian length
/// followed by that many payload bytes (the first of which is the <see cref="NetMessage"/> id).
/// GL-free and content-free, shared by the client and the server.
/// </summary>
public static class NetFraming
{
    /// <summary>The protocol version both ends must agree on in their hellos. 2: per-player
    /// inventories and the extended <see cref="NetInventoryAction"/> intents.</summary>
    public const int ProtocolVersion = 2;

    /// <summary>Largest accepted payload, so a bad length can never allocate unbounded memory.</summary>
    public const int MaxPayload = 1 << 20;

    public static void WriteFrame(Stream stream, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
    }

    /// <summary>Reads one frame, or null at end of stream, on a bad length or on a short read.</summary>
    public static byte[]? ReadFrame(Stream stream)
    {
        Span<byte> length = stackalloc byte[4];
        if (!ReadFully(stream, length))
            return null;
        var count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count < 1 || count > MaxPayload)
            return null;
        var payload = new byte[count];
        return ReadFully(stream, payload) ? payload : null;
    }

    private static bool ReadFully(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }
}

/// <summary>Builds one framed payload: the <see cref="NetMessage"/> id first, then its fields.</summary>
public sealed class PacketWriter : IDisposable
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;

    public PacketWriter(NetMessage message)
    {
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
        _writer.Write((byte)message);
    }

    public PacketWriter Write(byte value) { _writer.Write(value); return this; }
    public PacketWriter Write(bool value) { _writer.Write(value); return this; }
    public PacketWriter Write(int value) { _writer.Write(value); return this; }
    public PacketWriter Write(float value) { _writer.Write(value); return this; }
    public PacketWriter Write(double value) { _writer.Write(value); return this; }
    public PacketWriter Write(string value) { _writer.Write(value); return this; }
    public PacketWriter Write(Vector3 value) { _writer.Write(value.X); _writer.Write(value.Y); _writer.Write(value.Z); return this; }

    /// <summary>Writes a length-prefixed byte array (the chunk/column payloads).</summary>
    public PacketWriter WriteBytes(byte[] value)
    {
        _writer.Write(value.Length);
        _writer.Write(value, 0, value.Length);
        return this;
    }

    public byte[] ToArray() => _stream.ToArray();

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}

/// <summary>Reads a <see cref="PacketWriter"/> payload back, starting at its message id.</summary>
public sealed class PacketReader : IDisposable
{
    private readonly MemoryStream _stream;
    private readonly BinaryReader _reader;

    public PacketReader(byte[] payload)
    {
        _stream = new MemoryStream(payload, writable: false);
        _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
    }

    public NetMessage Message => (NetMessage)_reader.ReadByte();

    public byte ReadByte() => _reader.ReadByte();
    public bool ReadBool() => _reader.ReadBoolean();
    public int ReadInt() => _reader.ReadInt32();
    public float ReadFloat() => _reader.ReadSingle();
    public double ReadDouble() => _reader.ReadDouble();
    public string ReadString() => _reader.ReadString();
    public Vector3 ReadVector3() => new(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());

    /// <summary>Reads a length-prefixed byte array written by <see cref="PacketWriter.WriteBytes"/>.</summary>
    public byte[] ReadBytes() => _reader.ReadBytes(_reader.ReadInt32());

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}

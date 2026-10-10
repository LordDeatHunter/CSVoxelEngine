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
    /// <summary>Client to server: <c>(Vector3 eye, float yaw, float pitch, byte flags, int
    /// correctionAck)</c>. The flags are a creative/flight request (<see cref="NetPlayerFlags"/>);
    /// the ack is the sequence id of the newest <see cref="PlayerCorrection"/> the client has
    /// applied (0 before any), and the server ignores the position until it acks the newest one.</summary>
    PlayerState = 20,
    PlayerList = 21,
    PlayerJoined = 22,
    PlayerLeft = 23,
    /// <summary>Server to one client: the player's server-owned status,
    /// <c>(byte flags, int health, bool dead, Vector3 knockbackImpulse, float lift)</c>
    /// (<see cref="PlayerStatusCodec"/>): the creative and flight modes the server granted, the
    /// health pool, whether the player is dead, and a mob hit's shove for the client's own physics
    /// to apply (zero when none). Sent when the status changed and when a mode request was refused.</summary>
    PlayerStatus = 24,
    /// <summary>Server to one client: <c>(int seq, Vector3 eye)</c>, an illegal move (or a respawn)
    /// put the player back at <c>eye</c>. The client snaps there and acks <c>seq</c> in every
    /// following <see cref="PlayerState"/>.</summary>
    PlayerCorrection = 25,
    /// <summary>Retired in protocol 3: a raw client-authored cell write. A server refuses it and
    /// answers with the cell's authoritative state; clients send <see cref="WorldIntent"/>s.</summary>
    BlockEdit = 30,
    /// <summary>Server to one client: the sequence id of the intent it answers, then the
    /// authoritative state of the cells around that intent's target (a batch of
    /// <see cref="NetCellChange"/>).</summary>
    BlockCorrection = 31,
    TimeSync = 40,
    ChunkRequest = 50,
    ChunkData = 51,
    ColumnRequest = 52,
    /// <summary>Server to one client: <c>(int cx, int cz, bytes ColumnTerrain payload)</c>, one
    /// chunk column's heights, water levels and corner climate (<see cref="ChunkNetCodec.EncodeColumn"/>).</summary>
    ColumnTerrain = 53,
    LodRegionRequest = 54,
    LodRegionData = 55,
    BlockChanges = 56,
    EntityList = 60,
    /// <summary>Server broadcast: the campfire, quern, kiln and cast-mold block entities the clients
    /// draw (<see cref="StationNetCodec"/>).</summary>
    StationState = 61,
    /// <summary>Server to one client: the sequence id of the last intent applied, then that
    /// player's whole inventory.</summary>
    InventoryState = 70,
    /// <summary>Client to server: a sequence id and an <see cref="InventoryIntent"/>.</summary>
    InventoryRequest = 71,
    /// <summary>Client to server: a <see cref="silkgltriangle.WorldIntent"/> (break, place, use,
    /// throw, attack) with its sequence id (<see cref="WorldIntentCodec"/>).</summary>
    WorldIntent = 80,
    /// <summary>Server to one client: the sequence id of an intent and whether it was accepted.
    /// Every intent gets exactly one, in order, after its correction and inventory snapshot.</summary>
    IntentResult = 81,
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
    /// inventories and the extended <see cref="NetInventoryAction"/> intents. 3: world intents
    /// replace raw block edits, every intent carries a sequence id answered by an
    /// <see cref="NetMessage.IntentResult"/>, corrections and inventory snapshots carry the id they
    /// answer, and station state and player status are broadcast. 4: the server owns each player's
    /// status, so <see cref="NetMessage.PlayerStatus"/> carries the health, the dead flag and a
    /// mob hit's knockback; the server corrects illegal moves with a
    /// <see cref="NetMessage.PlayerCorrection"/> that every following
    /// <see cref="NetMessage.PlayerState"/> acks; and a respawn is a
    /// <see cref="NetIntentKind.Respawn"/> world intent. 5: a column answer is the whole
    /// <see cref="silkgltriangle.ColumnTerrain"/> (heights, water levels, corner climate), so a
    /// client derives nothing from worldgen. 6: <see cref="NetMessage.EntityList"/> also carries
    /// primed TNT and falling blocks (<see cref="NetEntityKind.PrimedTnt"/>,
    /// <see cref="NetEntityKind.FallingBlock"/>), which a client draws as display-only mirrors.
    /// 7: each <see cref="NetMessage.PlayerList"/> entry ends with the player's last
    /// <see cref="NetPlayerAction"/> and its counter, and the player flags carry cosmetic movement bits.</summary>
    public const int ProtocolVersion = 7;

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

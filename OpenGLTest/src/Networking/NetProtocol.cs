using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using VoxelEngine.Networking.ServerSide;

namespace VoxelEngine.Networking;

/// <summary>The message id on the wire (the first byte of every framed payload).</summary>
public enum NetMessage : byte
{
    ClientHello = 1,
    ServerHello = 2,
    KeepAlive = 3,
    /// <summary>Client to server: <c>(string line)</c>, one line the player typed (a <c>/</c> command runs on the server
    /// for that player).</summary>
    ChatSend = 10,
    /// <summary>Server to clients: one chat line, its kind, sender, text and optional link
    /// (<see cref="ChatMessageCodec"/>).</summary>
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
    /// <c>(byte flags, int health, bool dead, Vector3 knockbackImpulse, float lift, byte air, int mana, int maxMana)</c>
    /// (<see cref="PlayerStatusCodec"/>): the creative and flight modes the server granted, the
    /// health pool, whether the player is dead, a mob hit's shove for the client's own physics
    /// to apply (zero when none), the air in whole steps and the mana pool in whole units. Sent when the status changed and when a mode request was refused.</summary>
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
    /// <summary>Client to server: <c>(int x, int y, int z)</c>, a request to open the chest at a cell
    /// (<see cref="ChestNetCodec"/>). Answered with a <see cref="ChestContents"/>.</summary>
    ChestOpen = 62,
    /// <summary>Client to server: one <see cref="VoxelEngine.Simulation.Chests.ChestMove"/> against the chest the
    /// player has open. Answered with the chest's contents to everyone who has it open, and the mover's inventory.</summary>
    ChestMove = 63,
    /// <summary>Client to server: the player closed its chest panel.</summary>
    ChestClose = 64,
    /// <summary>Server to one client: the chest the player has open and what it holds, or "closed" when it is
    /// refused, out of reach or gone (<see cref="ChestNetCodec"/>).</summary>
    ChestContents = 65,
    /// <summary>Client to server: <c>(int x, int y, int z, string name)</c>, a request to rename the waystone at a cell
    /// (<see cref="WaystoneNetCodec"/>), accepted only from its first discoverer, the name sanitized. Answered with fresh
    /// <see cref="WaystoneRows"/> either way.</summary>
    WaystoneRename = 66,
    /// <summary>Server to one client: the registry rows of the waystones that player discovered (name, lost flag, whether
    /// it may rename it; <see cref="WaystoneNetCodec"/>). Sent at join and whenever the registry or the player's discovered
    /// list changed.</summary>
    WaystoneRows = 67,
    /// <summary>Server to one client: what the player's activation of a waystone (a replayed <see cref="NetIntentKind.Use"/>)
    /// came to: the cell, its name, whether the player may rename it and whether it was new to the player, so the client
    /// opens the discovery popup or the teleport list (<see cref="WaystoneNetCodec"/>).</summary>
    WaystoneOpened = 68,
    /// <summary>Server to one client: the sequence id of the last intent applied, then that
    /// player's whole inventory.</summary>
    InventoryState = 70,
    /// <summary>Client to server: a sequence id and an <see cref="InventoryIntent"/>.</summary>
    InventoryRequest = 71,
    /// <summary>Client to server: a <see cref="VoxelEngine.Networking.WorldIntent"/> (break, place, use,
    /// throw, attack, cast, hammer, waystone trip) with its sequence id (<see cref="WorldIntentCodec"/>).</summary>
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
    SelectStaffSlot = 8,
    /// <summary>Binds spell part keys into a slot of the held staff (the spell table).</summary>
    BindStaffSlot = 9,
    /// <summary>Unbinds a slot of the held staff (the spell table).</summary>
    ClearStaffSlot = 10,
    /// <summary>Wears an owned gear instance in an equipment slot: <c>Slot</c> and <c>Id</c> (the instance id).</summary>
    EquipGear = 11,
    /// <summary>Takes off the gear worn in an equipment slot: <c>Slot</c>.</summary>
    UnequipGear = 12,
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
    /// <see cref="VoxelEngine.Chunks.ColumnTerrain"/> (heights, water levels, corner climate), so a
    /// client derives nothing from worldgen. 6: <see cref="NetMessage.EntityList"/> also carries
    /// primed TNT and falling blocks (<see cref="NetEntityKind.PrimedTnt"/>,
    /// <see cref="NetEntityKind.FallingBlock"/>), which a client draws as display-only mirrors.
    /// 7: each <see cref="NetMessage.PlayerList"/> entry ends with the player's last
    /// <see cref="NetPlayerAction"/> and its counter, and the player flags carry cosmetic movement bits.
    /// 8: <see cref="NetMessage.EntityList"/> also carries icebergs (<see cref="NetEntityKind.Iceberg"/>);
    /// an older client would neither draw nor collide with them while the server judges a rider's
    /// hover against them. 9: <see cref="NetMessage.PlayerStatus"/> ends with the player's air.
    /// 10: an <see cref="NetIntentKind.Attack"/> intent may carry the sword's swings
    /// (<see cref="NetAttackKind.SwordCut"/>, <see cref="NetAttackKind.SwordBackCut"/>,
    /// <see cref="NetAttackKind.SwordChop"/>) and the player list their <see cref="NetPlayerAction"/>s; an
    /// older peer would refuse the intent as malformed and drop the connection.
    /// 11: <see cref="NetMessage.PlayerStatus"/> ends with the player's mana and its max.
    /// 12: a <see cref="NetIntentKind.Cast"/> world intent (with its slot) and the
    /// <see cref="NetInventoryAction.SelectStaffSlot"/> inventory action; an older peer would refuse
    /// either as malformed.
    /// 13: every <see cref="NetMessage.EntityList"/> entry ends with a status-effect byte mask and
    /// <see cref="NetMessage.PlayerStatus"/> ends with the player's effect mask; an older peer would
    /// misread the frames.
    /// 14: the <see cref="NetInventoryAction.BindStaffSlot"/> and
    /// <see cref="NetInventoryAction.ClearStaffSlot"/> inventory actions (the MG-7 spell table); an
    /// older peer would refuse them as malformed.
    /// 15: the <c>InventoryState</c> snapshot carries the player's known spell parts (MG-8), which an
    /// older client would ignore and so bind against nothing. <see cref="NetPlayerAction.Cast"/> (8) was added
    /// later without a bump: the player list's action byte is read unchecked and an older client simply plays
    /// no gesture for an action it does not know.
    /// 16: a crucible carries one entry per metal (the alloy kiln): the <see cref="NetMessage.StationState"/>
    /// kiln entry writes a metal count and each metal's molten and pending ml, and the inventory snapshot's
    /// crucible a <c>Metals</c> list; an older peer would misread the station frames.
    /// 17: <see cref="NetMessage.EntityList"/> also carries the hell mobs (<see cref="NetEntityKind.Hellbrute"/>,
    /// <see cref="NetEntityKind.Cinderwitch"/>); an older client would not draw them.
    /// 18: <see cref="NetMessage.EntityList"/> also carries the sky mobs (<see cref="NetEntityKind.Galeraptor"/>,
    /// <see cref="NetEntityKind.Cloudgolem"/>); a flyer's whole position rides the entry as for every mob, and its
    /// flight follows from its kind; an older client would not draw them. A hell or sky mob's attack count was put
    /// in its entry's ammo field later without a bump: the layout is unchanged, an older client ignores the field
    /// and an older server's 0 simply plays no attack clip.
    /// 19: the <see cref="NetInventoryAction.EquipGear"/> and <see cref="NetInventoryAction.UnequipGear"/> inventory
    /// actions, and the inventory snapshot carries the worn gear (<c>Equipped</c>) and the rolled modifiers of each
    /// gear instance; an older peer would refuse either action as malformed.
    /// 20: the chest messages (<see cref="NetMessage.ChestOpen"/>, <see cref="NetMessage.ChestMove"/>,
    /// <see cref="NetMessage.ChestClose"/>, <see cref="NetMessage.ChestContents"/>); chest contents are per player and never
    /// ride the station broadcast. An older peer would ignore them, so a chest would never open.
    /// 21: each <see cref="NetMessage.PlayerList"/> entry ends with one byte per armor slot, the item id of the visible armor
    /// piece that player wears (0 for none), so others see it on the body; an older peer would misread every entry.
    /// 22: the iron works. <see cref="NetMessage.StationState"/> ends with the bloomeries and the anvils (after the
    /// cast molds), and <see cref="NetIntentKind.Hammer"/> works a bloom on an anvil; an older peer would misread the
    /// station frames and refuse the intent as malformed.
    /// 23: <see cref="NetEntityKind.Sandworm"/> (the boss: a mob entry for its head, its phase in the ammo field) and
    /// <see cref="NetEntityKind.SandwormTarget"/> (the point it is about to surface at); an older client would not
    /// draw it.
    /// 24: waystones. A <see cref="NetIntentKind.Waystone"/> world intent (the destination's cell) asks for a trip from the
    /// waystone the player stands at; the server discovers a waystone on a replayed <see cref="NetIntentKind.Use"/> and
    /// answers with <see cref="NetMessage.WaystoneOpened"/>, keeps each player's <see cref="NetMessage.WaystoneRows"/> (the
    /// registry rows of its discovered waystones) current, and takes a <see cref="NetMessage.WaystoneRename"/> from a
    /// waystone's first discoverer. An older peer would refuse the intent as malformed and never open a waystone.
    /// 25: <see cref="NetEntityKind.Skeleton"/> (the battle tower's skeleton): its tower tier in the item byte, its attack
    /// count (melee even, shot odd) in the ammo field; an older client would not draw it.
    /// 26: the battle tower locks appended at the end of <see cref="NetMessage.StationState"/> (each tower's cell, center,
    /// radius and per floor its band, piles left, skeletons left and door bit); an older peer would misread the station
    /// frames.
    /// 27: each tower's altar state byte (<see cref="Simulation.Tower.TowerAltarState"/>) appended after its floors in
    /// <see cref="NetMessage.StationState"/>; an older peer would misread the station frames. Also
    /// <see cref="NetEntityKind.SkeletonKing"/> (the tower's boss): its attack count in the ammo field (slam, volley, summon
    /// by remainder modulo 3), its fight phase in the item byte; an older client would not draw it.
    /// 28: the eye's cave biome (<see cref="WorldGen.Caves.CaveBiome"/>) appended to <see cref="NetMessage.PlayerStatus"/>;
    /// an older peer would misread the status.
    /// 29: the cave mobs (<see cref="NetEntityKind.Shardling"/>, <see cref="NetEntityKind.Sporeling"/>,
    /// <see cref="NetEntityKind.Magmaborn"/>) in <see cref="NetMessage.EntityList"/>, their depth tier in the item byte and
    /// attack count in the ammo field; an older client would not draw them.
    /// 30: every <see cref="NetMessage.ChatMessage"/> ends with an optional link (<see cref="ChatMessageCodec"/>): its
    /// suggested command, empty for none, then its start and length in the text; the server's <c>/locate</c> answers with
    /// one. An older peer would misread the frame.
    /// 31: a <see cref="NetEntityKind.Skeleton"/>'s item byte packs its role and outfit above its tier
    /// (<see cref="Entities.TowerMobs.Skeleton.PackLook"/>); an older client would read a warrior or an armored skeleton as a
    /// different tier.
    /// 32: the anvil forge. Each anvil in <see cref="NetMessage.StationState"/> ends with its stacked metal's id and count,
    /// and a <see cref="NetIntentKind.Forge"/> world intent (the anvil's cell and the recipe's result id) forges from that
    /// stack; an older peer would misread the station frames and refuse the intent as malformed.
    /// 33: block and item ids are two bytes (<see cref="PacketWriter.WriteId"/>) everywhere an id travels: the chunk
    /// payload (one little-endian ushort per cell), cell changes and corrections, entity item ids, worn armor, inventory
    /// and world intents and the station frames; an older peer would misread every frame carrying an id.
    /// 34: the chunk payload is the palette form (a mode byte, then one id, or a palette of up to 256 ids with one index
    /// byte per cell, or one little-endian ushort per cell past 256 distinct ids), then the state as before; an older peer
    /// would misread every ChunkData frame.
    /// 35: the <see cref="NetMessage.ServerHello"/> ends with the server's name for every block and item, molten
    /// material, status effect and atlas tile id (<see cref="WireIds"/>), and a client translates every id it reads or
    /// sends through them, so a client whose content is numbered differently still agrees with the server; an equip of
    /// a stackable and a chest deposit carry their id at id width. An older peer would misread the hello.
    /// 36: <see cref="NetMessage.EntityList"/> also carries the wild animals (<see cref="NetEntityKind.Deer"/> onward,
    /// one kind per <c>WildlifeKind</c>); their <see cref="NetEntity.Ammo"/> is the count of their action clips like any
    /// realm mob's. An older client would not draw them.
    /// 37: two more wild animals (<see cref="NetEntityKind.Crow"/>, <see cref="NetEntityKind.Dragonfly"/>), and a wild
    /// animal's <see cref="NetEntity.ItemId"/> carries its posture (<c>Animal.Posture</c>); an older client would not
    /// draw the new kinds.
    /// 38: <see cref="NetMessage.StationState"/> ends with the cells of the chests at least one player has open
    /// (<c>ChestLids.WriteNet</c>), so every client swings those lids open and shut; an older peer would misread the
    /// station frames.</summary>
    public const int ProtocolVersion = 38;

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
    private readonly WireIds? _wire;

    /// <summary>A client passes its <see cref="WireIds"/>, so every id it writes goes out in the server's numbering;
    /// the server passes none, since its ids are the wire's.</summary>
    public PacketWriter(NetMessage message, WireIds? wire = null)
    {
        _wire = wire;
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
        _writer.Write((byte)message);
    }

    public PacketWriter Write(byte value) { _writer.Write(value); return this; }
    public PacketWriter Write(ushort value) { _writer.Write(value); return this; }
    public PacketWriter Write(bool value) { _writer.Write(value); return this; }
    public PacketWriter Write(int value) { _writer.Write(value); return this; }
    public PacketWriter Write(float value) { _writer.Write(value); return this; }
    public PacketWriter Write(double value) { _writer.Write(value); return this; }
    public PacketWriter Write(string value) { _writer.Write(value); return this; }
    public PacketWriter Write(Vector3 value) { _writer.Write(value.X); _writer.Write(value.Y); _writer.Write(value.Z); return this; }

    /// <summary>Writes a block or item id. The one place that fixes an id's wire width: every id that travels goes
    /// through here and comes back through <see cref="PacketReader.ReadId"/>, translated to the server's numbering
    /// on a client.</summary>
    public PacketWriter WriteId(ushort id) { _writer.Write(_wire?.ContentToServer(id) ?? id); return this; }

    /// <summary>Writes a molten material id (0 = none), translated to the server's numbering on a client.</summary>
    public PacketWriter WriteMaterial(byte id) { _writer.Write(_wire?.MaterialToServer(id) ?? id); return this; }

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
    private readonly WireIds? _wire;

    /// <summary>A client passes its <see cref="WireIds"/>, so every id it reads comes back in its own numbering;
    /// the server passes none.</summary>
    public PacketReader(byte[] payload, WireIds? wire = null)
    {
        _wire = wire;
        _stream = new MemoryStream(payload, writable: false);
        _reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
    }

    public NetMessage Message => (NetMessage)_reader.ReadByte();

    public byte ReadByte() => _reader.ReadByte();
    public ushort ReadUInt16() => _reader.ReadUInt16();
    public bool ReadBool() => _reader.ReadBoolean();
    public int ReadInt() => _reader.ReadInt32();
    public float ReadFloat() => _reader.ReadSingle();
    public double ReadDouble() => _reader.ReadDouble();
    public string ReadString() => _reader.ReadString();
    public Vector3 ReadVector3() => new(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());

    /// <summary>Reads a block or item id written by <see cref="PacketWriter.WriteId"/>, at the same width, translated
    /// to this run's numbering on a client.</summary>
    public ushort ReadId()
    {
        var id = _reader.ReadUInt16();
        return _wire?.ContentToLocal(id) ?? id;
    }

    /// <summary>Reads a molten material id written by <see cref="PacketWriter.WriteMaterial"/>.</summary>
    public byte ReadMaterial()
    {
        var id = _reader.ReadByte();
        return _wire?.MaterialToLocal(id) ?? id;
    }

    /// <summary>Reads a status effect mask (one bit per effect), with each bit moved to this run's bit for the same
    /// effect on a client.</summary>
    public byte ReadEffectMask()
    {
        var mask = _reader.ReadByte();
        return _wire?.EffectMaskToLocal(mask) ?? mask;
    }

    /// <summary>Reads a length-prefixed byte array written by <see cref="PacketWriter.WriteBytes"/>.</summary>
    public byte[] ReadBytes() => _reader.ReadBytes(_reader.ReadInt32());

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}

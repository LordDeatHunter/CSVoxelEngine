using System;
using System.Collections.Generic;
using VoxelEngine.Content;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Graphics;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking;

/// <summary>
/// The client's translation between the server's numeric ids and its own. Every numbered content space is handed out
/// in file-name order at startup (blocks and items, molten materials, status effects, atlas tiles), so a client whose
/// content differs from the server's by a single file numbers everything after it differently. The server's ids are
/// the wire's: <see cref="WriteTables"/> appends the server's name for every id of each space to the
/// <see cref="NetMessage.ServerHello"/>, and <see cref="Read"/> resolves each name to this run's id, so a frame from a
/// server whose content order differs still means the same blocks, items, metals, effects and textures.
///
/// <para>Content (blocks and items, by <see cref="SaveNames"/> name) and molten materials the client does not have
/// refuse the connection (<see cref="Missing"/>): the client could not represent the server's world. A status effect
/// or a texture it lacks is only cosmetic and drops to "none" (the effect bit) or layer 0 (the texture). A local id
/// the server lacks goes out as <see cref="NotOnServer"/>, which no server id ever equals, so the server refuses
/// the intent that carried it.</para>
///
/// <para>The server never translates: its <see cref="PacketWriter"/>s and <see cref="PacketReader"/>s carry no
/// table. GL-free; the tables are built once on the connect thread and only read after.</para>
/// </summary>
public sealed class WireIds
{
    /// <summary>The wire id of a local block or item the server does not have; never a real id (the registry caps
    /// the id count below it).</summary>
    public const ushort NotOnServer = ushort.MaxValue;

    /// <summary>The wire material id of a local molten material the server does not have (0 is "no material").</summary>
    private const byte MaterialNotOnServer = 0;

    private readonly ushort[] _contentToLocal;
    private readonly ushort[] _contentToServer;
    private readonly byte[] _materialToLocal;
    private readonly byte[] _materialToServer;
    private readonly byte[] _effectBitToLocal;
    private readonly ushort[] _tileToLocal;

    private WireIds(ushort[] contentToLocal, ushort[] contentToServer, byte[] materialToLocal,
        byte[] materialToServer, byte[] effectBitToLocal, ushort[] tileToLocal, IReadOnlyList<string> missing)
    {
        _contentToLocal = contentToLocal;
        _contentToServer = contentToServer;
        _materialToLocal = materialToLocal;
        _materialToServer = materialToServer;
        _effectBitToLocal = effectBitToLocal;
        _tileToLocal = tileToLocal;
        Missing = missing;
    }

    /// <summary>Server content and materials this client does not have, by name; non-empty refuses the connection.</summary>
    public IReadOnlyList<string> Missing { get; }

    /// <summary>
    /// Writes the server's name for every id of each numbered space, in id order: block and item names
    /// (<see cref="SaveNames.NameOf"/>, "" for air), molten material names (1-based), status effect names
    /// (by bit) and atlas tile names (by layer).
    /// </summary>
    public static void WriteTables(PacketWriter writer)
    {
        var idCount = BlockTables.IdCount;
        writer.Write(idCount);
        for (var id = 0; id < idCount; id++)
            writer.Write(SaveNames.NameOf((ushort)id) ?? "");

        var materials = ContentRegistry.MoltenMaterials;
        writer.Write(materials.Count);
        foreach (var material in materials)
            writer.Write(material.Name);

        var effects = StatusEffectRegistry.All;
        writer.Write(effects.Count);
        foreach (var effect in effects)
            writer.Write(effect.Name);

        var tiles = TextureAtlas.TileNames;
        writer.Write(tiles.Count);
        foreach (var tile in tiles)
            writer.Write(tile);
    }

    /// <summary>
    /// Reads what <see cref="WriteTables"/> wrote and resolves each name to this run's id. A count past what the
    /// space can hold throws <see cref="System.IO.InvalidDataException"/>; a name this client lacks is collected
    /// in <see cref="Missing"/> (content and materials) or dropped (effects and tiles, with a warning).
    /// </summary>
    public static WireIds Read(PacketReader reader)
    {
        var missing = new List<string>();

        var serverIds = ReadCount(reader, NotOnServer, "block and item");
        var contentToLocal = new ushort[serverIds];
        var contentToServer = new ushort[BlockTables.IdCount];
        Array.Fill(contentToServer, NotOnServer);
        contentToServer[BlockType.Air] = BlockType.Air;
        for (var serverId = 0; serverId < serverIds; serverId++)
        {
            var name = reader.ReadString();
            if (name.Length == 0) continue; // air, and an id the server cannot name, read as air
            if (!SaveNames.TryResolve(name, out var local))
            {
                missing.Add(name);
                continue;
            }
            contentToLocal[serverId] = local;
            contentToServer[local] = (ushort)serverId;
        }

        var serverMaterials = ReadCount(reader, byte.MaxValue, "molten material");
        var materialToLocal = new byte[serverMaterials + 1];
        var materialToServer = new byte[ContentRegistry.MoltenMaterials.Count + 1];
        for (var serverId = 1; serverId <= serverMaterials; serverId++)
        {
            var name = reader.ReadString();
            if (!SaveNames.TryResolveMaterial(name, out var local))
            {
                missing.Add($"material/{name}");
                continue;
            }
            materialToLocal[serverId] = local;
            materialToServer[local] = (byte)serverId;
        }

        // An effect mask is one byte, so at most eight effects can be named by bit
        var serverEffects = ReadCount(reader, 8, "status effect");
        var effectBitToLocal = new byte[serverEffects];
        var unknown = new List<string>();
        for (var bit = 0; bit < serverEffects; bit++)
        {
            var name = reader.ReadString();
            if (StatusEffectRegistry.TryGet(name, out var effect)) effectBitToLocal[bit] = effect.Bit;
            else unknown.Add($"effect/{name}");
        }

        var serverTiles = ReadCount(reader, NotOnServer, "atlas tile");
        var tileToLocal = new ushort[serverTiles];
        var localTiles = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var tileNames = TextureAtlas.TileNames;
        for (var layer = 0; layer < tileNames.Count; layer++)
            localTiles.TryAdd(tileNames[layer], (ushort)layer);
        for (var layer = 0; layer < serverTiles; layer++)
        {
            var name = reader.ReadString();
            if (localTiles.TryGetValue(name, out var local)) tileToLocal[layer] = local;
            else unknown.Add($"texture/{name}");
        }

        if (unknown.Count > 0)
            Log.Warn($"net: the server has {unknown.Count} effect(s)/texture(s) this client lacks, shown as none: " +
                     string.Join(", ", unknown.GetRange(0, Math.Min(unknown.Count, 8))));

        return new WireIds(contentToLocal, contentToServer, materialToLocal, materialToServer, effectBitToLocal,
            tileToLocal, missing);
    }

    /// <summary>This run's id of a server block or item id; a server id past its own table is a malformed frame.</summary>
    public ushort ContentToLocal(ushort serverId) => serverId < _contentToLocal.Length
        ? _contentToLocal[serverId]
        : throw new System.IO.InvalidDataException($"the server sent id {serverId} past its {_contentToLocal.Length} ids");

    /// <summary>The server's id of a local block or item, or <see cref="NotOnServer"/> when the server lacks it.</summary>
    public ushort ContentToServer(ushort localId) =>
        localId < _contentToServer.Length ? _contentToServer[localId] : NotOnServer;

    /// <summary>This run's molten material id of a server one (0 stays "no material").</summary>
    public byte MaterialToLocal(byte serverId) => serverId < _materialToLocal.Length
        ? _materialToLocal[serverId]
        : throw new System.IO.InvalidDataException($"the server sent material {serverId} past its {_materialToLocal.Length - 1}");

    /// <summary>The server's molten material id of a local one, or 0 when the server lacks it.</summary>
    public byte MaterialToServer(byte localId) =>
        localId < _materialToServer.Length ? _materialToServer[localId] : MaterialNotOnServer;

    /// <summary>A server effect mask with each bit moved to this run's bit for the same effect; an effect this
    /// client lacks is dropped.</summary>
    public byte EffectMaskToLocal(byte serverMask)
    {
        byte local = 0;
        for (var bit = 0; bit < _effectBitToLocal.Length; bit++)
            if ((serverMask & (1 << bit)) != 0)
                local |= _effectBitToLocal[bit];
        return local;
    }

    /// <summary>This run's atlas layer of a server one; a texture this client lacks, or a layer past the server's
    /// table, reads as layer 0.</summary>
    public ushort TileToLocal(ushort serverLayer) =>
        serverLayer < _tileToLocal.Length ? _tileToLocal[serverLayer] : (ushort)0;

    private static int ReadCount(PacketReader reader, int max, string space)
    {
        var count = reader.ReadInt();
        if (count < 0 || count > max)
            throw new System.IO.InvalidDataException($"the server names {count} {space} ids");
        return count;
    }
}

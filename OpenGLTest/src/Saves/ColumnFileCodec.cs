using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace silkgltriangle;

/// <summary>
/// The on-disk format of one imported chunk column's <see cref="ColumnData"/>
/// (<see cref="ImportedMapSource"/>, written by <see cref="ImportedMapWriter"/>): everything a
/// supplied column must describe for the chunk mesher, the far LOD, the fluid wake and the
/// <see cref="ColumnTerrain"/> a server streams. Every array is indexed
/// (x &lt;&lt; Chunk.SizeShift) | z, like <see cref="ColumnData"/>'s.
///
/// Layout, all little-endian:
/// <code>
///   "CVXL"            4 ASCII bytes of magic
///   version           byte, currently 1
///   Deflate body:
///     256 x int32     Heights (ravines cut in)
///     256 x int32     Bed (the profile before the ravine cut)
///     256 x int32     WaterLevel (TerrainGenerator.NoWater where dry)
///     256 bytes       Biomes, each a defined Biome value
///     256 bytes       DirtDepth (0 = bare rock)
///     float32         CornerWarmth
///     float32         CornerWetness
/// </code>
/// <see cref="ColumnData.EntranceWeight"/> and <see cref="ColumnData.CaveIntensity"/> are not
/// stored: they are worldgen-only inputs of the cave carvers, which never run on a supplied
/// column (its blocks come from the map), and no other consumer reads them, so a decoded column
/// carries zeros there. GL-free and content-free: the format names no block.
/// </summary>
public static class ColumnFileCodec
{
    private static readonly byte[] Magic = "CVXL"u8.ToArray();

    /// <summary>The format version this build writes and the only one it reads.</summary>
    public const byte Version = 1;

    private const int ColumnArea = Chunk.Size * Chunk.Size;

    // Three int32 arrays, two byte arrays and the two climate floats
    private const int BodyLength = 3 * ColumnArea * sizeof(int) + 2 * ColumnArea + 2 * sizeof(float);

    /// <summary>
    /// Encodes <paramref name="column"/> into <paramref name="output"/> (left open). Throws
    /// <see cref="ArgumentException"/> when an array is not one chunk column's size.
    /// </summary>
    public static void Write(Stream output, ColumnData column)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(column);
        RequireColumnSize(column.Heights.Length, nameof(column.Heights));
        RequireColumnSize(column.Bed.Length, nameof(column.Bed));
        RequireColumnSize(column.WaterLevel.Length, nameof(column.WaterLevel));
        RequireColumnSize(column.Biomes.Length, nameof(column.Biomes));
        RequireColumnSize(column.DirtDepth.Length, nameof(column.DirtDepth));

        var body = new byte[BodyLength];
        var offset = 0;
        offset = WriteInts(body, offset, column.Heights);
        offset = WriteInts(body, offset, column.Bed);
        offset = WriteInts(body, offset, column.WaterLevel);
        for (var i = 0; i < ColumnArea; i++) body[offset + i] = (byte)column.Biomes[i];
        offset += ColumnArea;
        column.DirtDepth.AsSpan().CopyTo(body.AsSpan(offset, ColumnArea));
        offset += ColumnArea;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(offset), column.CornerWarmth);
        offset += sizeof(float);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(offset), column.CornerWetness);

        output.Write(Magic);
        output.WriteByte(Version);
        using var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true);
        deflate.Write(body);
    }

    /// <summary>
    /// Decodes a column written by <see cref="Write"/>. Bad magic, an unknown version, a truncated
    /// or damaged body and a biome byte that names no <see cref="Biome"/> all surface as one
    /// exception type, <see cref="InvalidDataException"/>, so callers catch that (plus
    /// <see cref="IOException"/> for the disk itself).
    /// </summary>
    public static ColumnData Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            return ReadCore(input);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("The column file ends early.", e);
        }
    }

    private static ColumnData ReadCore(Stream input)
    {
        Span<byte> magic = stackalloc byte[4];
        input.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
            throw new InvalidDataException("Not a column file (bad magic).");

        var version = input.ReadByte();
        if (version < 0)
            throw new EndOfStreamException();
        if (version != Version)
            throw new InvalidDataException($"Unsupported column file version {version} (this build reads {Version}).");

        var body = new byte[BodyLength];
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true))
            deflate.ReadExactly(body);

        var offset = 0;
        var heights = ReadInts(body, ref offset);
        var bed = ReadInts(body, ref offset);
        var waterLevel = ReadInts(body, ref offset);

        var biomes = new Biome[ColumnArea];
        for (var i = 0; i < ColumnArea; i++)
        {
            var biome = (Biome)body[offset + i];
            if (!Enum.IsDefined(biome))
                throw new InvalidDataException($"Column file cell {i} names biome {body[offset + i]}, which does not exist.");
            biomes[i] = biome;
        }
        offset += ColumnArea;

        var dirtDepth = body.AsSpan(offset, ColumnArea).ToArray();
        offset += ColumnArea;
        var warmth = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(offset));
        offset += sizeof(float);
        var wetness = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(offset));

        // The carvers never run on a supplied column, so its cave inputs stay zero
        return new ColumnData(heights, dirtDepth, new float[ColumnArea], new float[ColumnArea], biomes, bed, waterLevel,
            warmth, wetness);
    }

    private static void RequireColumnSize(int length, string name)
    {
        if (length != ColumnArea)
            throw new ArgumentException($"A column's {name} needs {ColumnArea} entries, got {length}.", "column");
    }

    private static int WriteInts(byte[] body, int offset, int[] values)
    {
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset), value);
            offset += sizeof(int);
        }
        return offset;
    }

    private static int[] ReadInts(byte[] body, ref int offset)
    {
        var values = new int[ColumnArea];
        for (var i = 0; i < ColumnArea; i++)
        {
            values[i] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(offset));
            offset += sizeof(int);
        }
        return values;
    }
}

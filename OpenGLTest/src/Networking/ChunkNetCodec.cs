using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using VoxelEngine.Chunks;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking;

/// <summary>
/// Compact wire form of one chunk's block data: its ids in the palette form of
/// <see cref="ChunkBlocks"/> and, when any cell carries one, the <see cref="Chunk.BlockCount"/> state
/// bytes, Deflate-compressed. The multiplayer client restores streamed chunks from this instead of
/// generating them; player-placed bookkeeping is not sent, since a client never fells, decays or
/// saves. It also carries one chunk column's terrain (<see cref="ColumnTerrain"/>: heights, water
/// levels and corner climate), which is all a client needs from the terrain beyond the blocks.
/// GL-free and content-free: the ids are not checked against the loaded content here, the client
/// checks them before it applies a chunk.
/// </summary>
/// <remarks>
/// <para>A chunk payload starts with one mode byte, then one of three layouts, all in
/// <see cref="Chunk.Idx"/> order: <b>Uniform</b> (0), the one id of every cell as a little-endian ushort;
/// <b>Indexed</b> (1), one byte holding the palette count minus one (1 to
/// <see cref="ChunkBlocks.PaletteCapacity"/> entries), the palette as little-endian ushorts, then
/// one index byte per cell; <b>Direct</b> (2), one little-endian ushort per cell. Then a state flag
/// byte: 0 for none, 1 followed by one state byte per cell. The sender's palette goes out as held,
/// stale entries included; the decoder rebuilds the storage with <see cref="ChunkBlocks.FromDense"/>,
/// so a decoded snapshot is always compact and duplicate-free whatever the peer sent.</para>
/// <para>Palettes and Direct cells are the arrays' own bytes
/// (<see cref="MemoryMarshal.AsBytes{T}(Span{T})"/>), which assumes a little-endian host, as
/// <see cref="LodNetCodec"/> does; every supported target is. Every malformed payload (an unknown
/// mode, an index past the palette, a bad state flag, a truncation anywhere) throws
/// <see cref="InvalidDataException"/>, the one malformed-frame type the client's message loop
/// handles.</para>
/// </remarks>
public static class ChunkNetCodec
{
    private const byte ModeUniform = 0;
    private const byte ModeIndexed = 1;
    private const byte ModeDirect = 2;

    /// <summary>Encodes a snapshot's blocks and state (its player-placed cells are dropped).</summary>
    public static byte[] Encode(ChunkSnapshot snapshot)
    {
        var storage = snapshot.Storage;
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            switch (storage.Mode)
            {
                case ChunkBlocks.StorageMode.Uniform:
                    deflate.WriteByte(ModeUniform);
                    Span<byte> id = stackalloc byte[sizeof(ushort)];
                    BinaryPrimitives.WriteUInt16LittleEndian(id, storage.IdSpan[0]);
                    deflate.Write(id);
                    break;
                case ChunkBlocks.StorageMode.Indexed:
                    var palette = storage.IdSpan;
                    deflate.WriteByte(ModeIndexed);
                    deflate.WriteByte((byte)(palette.Length - 1));
                    deflate.Write(MemoryMarshal.AsBytes(palette));
                    deflate.Write(storage.IndexSpan);
                    break;
                default:
                    deflate.WriteByte(ModeDirect);
                    deflate.Write(MemoryMarshal.AsBytes(storage.IdSpan));
                    break;
            }

            var state = snapshot.State;
            if (state is { Length: Chunk.BlockCount })
            {
                deflate.WriteByte(1);
                deflate.Write(state);
            }
            else
            {
                deflate.WriteByte(0);
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decodes what <see cref="Encode"/> wrote back into a compact snapshot. A malformed or
    /// truncated payload throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static ChunkSnapshot Decode(byte[] payload)
    {
        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);

            var dense = ChunkBlocks.DenseScratch;
            var mode = deflate.ReadByte();
            switch (mode)
            {
                case < 0:
                    throw new InvalidDataException("Chunk payload ended before its mode byte.");
                case ModeUniform:
                    Span<byte> id = stackalloc byte[sizeof(ushort)];
                    ReadFully(deflate, id);
                    dense.AsSpan().Fill(BinaryPrimitives.ReadUInt16LittleEndian(id));
                    break;
                case ModeIndexed:
                    ReadIndexed(deflate, dense);
                    break;
                case ModeDirect:
                    ReadFully(deflate, MemoryMarshal.AsBytes(dense.AsSpan()));
                    break;
                default:
                    throw new InvalidDataException($"Chunk payload has unknown block mode {mode}.");
            }

            // Built before the state is read, while nothing else can touch the scratch
            var storage = ChunkBlocks.FromDense(dense);

            byte[]? state;
            switch (deflate.ReadByte())
            {
                case 0:
                    state = null;
                    break;
                case 1:
                    state = new byte[Chunk.BlockCount];
                    ReadFully(deflate, state);
                    break;
                case < 0:
                    throw new InvalidDataException("Chunk payload ended before its state flag.");
                case var flag:
                    throw new InvalidDataException($"Chunk payload has state flag {flag}, not 0 or 1.");
            }

            return new ChunkSnapshot(storage, state, playerPlaced: null);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Chunk payload ended early.", e);
        }
    }

    // An Indexed body: the palette count, the palette and one index byte per cell, gathered into
    // the dense ids. An index at or past the palette count is refused.
    private static void ReadIndexed(Stream stream, ushort[] dense)
    {
        var countByte = stream.ReadByte();
        if (countByte < 0)
            throw new EndOfStreamException("Chunk payload ended before its palette count.");
        var count = countByte + 1;

        Span<ushort> palette = stackalloc ushort[ChunkBlocks.PaletteCapacity];
        palette = palette[..count];
        ReadFully(stream, MemoryMarshal.AsBytes(palette));

        Span<byte> indices = stackalloc byte[Chunk.BlockCount];
        ReadFully(stream, indices);
        if (count < ChunkBlocks.PaletteCapacity && indices.IndexOfAnyExceptInRange((byte)0, (byte)(count - 1)) >= 0)
            throw new InvalidDataException($"Chunk payload has a palette index past its {count} entries.");

        for (var i = 0; i < dense.Length; i++)
            dense[i] = palette[indices[i]];
    }

    /// <summary>
    /// Encodes one chunk column's <see cref="ColumnTerrain"/>: the 256 heights and the 256 water
    /// levels as int32, then the corner warmth and wetness as float32, Deflate-compressed. Throws
    /// <see cref="ArgumentException"/> when either array is not one value per column of the chunk.
    /// </summary>
    public static byte[] EncodeColumn(in ColumnTerrain terrain)
    {
        if (terrain.Heights is not { Length: ColumnLength })
            throw new ArgumentException($"A column carries {ColumnLength} heights, got {terrain.Heights?.Length ?? 0}.", nameof(terrain));
        if (terrain.WaterLevel is not { Length: ColumnLength })
            throw new ArgumentException($"A column carries {ColumnLength} water levels, got {terrain.WaterLevel?.Length ?? 0}.", nameof(terrain));

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(deflate, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var height in terrain.Heights)
                writer.Write(height);
            foreach (var level in terrain.WaterLevel)
                writer.Write(level);
            writer.Write(terrain.Warmth);
            writer.Write(terrain.Wetness);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decodes what <see cref="EncodeColumn"/> wrote. A truncated or corrupt payload throws
    /// <see cref="InvalidDataException"/>, the one malformed-frame type the client's message loop
    /// already handles. Reads nothing from worldgen.
    /// </summary>
    public static ColumnTerrain DecodeColumn(byte[] payload)
    {
        try
        {
            using var input = new MemoryStream(payload, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var reader = new BinaryReader(deflate, Encoding.UTF8, leaveOpen: true);
            var heights = ReadColumnInts(reader);
            var waterLevel = ReadColumnInts(reader);
            var warmth = reader.ReadSingle();
            var wetness = reader.ReadSingle();
            return new ColumnTerrain(heights, waterLevel, warmth, wetness);
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Column payload ended early.", e);
        }
    }

    // One int per column of a chunk (heights or water levels).
    private const int ColumnLength = Chunk.Size * Chunk.Size;

    private static int[] ReadColumnInts(BinaryReader reader)
    {
        var values = new int[ColumnLength];
        for (var i = 0; i < values.Length; i++)
            values[i] = reader.ReadInt32();
        return values;
    }

    private static void ReadFully(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n <= 0)
                throw new EndOfStreamException("Chunk payload ended early.");
            read += n;
        }
    }
}

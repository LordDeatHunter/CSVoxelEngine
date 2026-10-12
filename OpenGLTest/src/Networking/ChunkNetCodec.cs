using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using VoxelEngine.Chunks;
using VoxelEngine.Saves;

namespace VoxelEngine.Networking;

/// <summary>
/// Compact wire form of one chunk's block data: the <see cref="Chunk.BlockCount"/> block ids and,
/// when any cell carries one, the same number of state bytes, Deflate-compressed. The multiplayer
/// client restores streamed chunks from this instead of generating them; player-placed bookkeeping
/// is not sent, since a client never fells, decays or saves. It also carries one chunk column's
/// terrain (<see cref="ColumnTerrain"/>: heights, water levels and corner climate), which is all a
/// client needs from the terrain beyond the blocks. GL-free and content-free.
/// </summary>
public static class ChunkNetCodec
{
    /// <summary>Encodes a snapshot's blocks and state (its player-placed cells are dropped).</summary>
    public static byte[] Encode(ChunkSnapshot snapshot)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(snapshot.Blocks);
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

    /// <summary>Decodes what <see cref="Encode"/> wrote back into a snapshot.</summary>
    public static ChunkSnapshot Decode(byte[] payload)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);

        var blocks = new byte[Chunk.BlockCount];
        ReadFully(deflate, blocks);

        var hasState = deflate.ReadByte();
        byte[]? state = null;
        if (hasState == 1)
        {
            state = new byte[Chunk.BlockCount];
            ReadFully(deflate, state);
        }

        return new ChunkSnapshot(blocks, state, playerPlaced: null);
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

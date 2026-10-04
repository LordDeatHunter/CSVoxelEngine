using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace silkgltriangle;

/// <summary>
/// Compact wire form of one chunk's block data: the <see cref="Chunk.BlockCount"/> block ids and,
/// when any cell carries one, the same number of state bytes, Deflate-compressed. The multiplayer
/// client restores streamed chunks from this instead of generating them; player-placed bookkeeping
/// is not sent, since a client never fells, decays or saves. GL-free and content-free.
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

    /// <summary>Encodes one column's 16x16 terrain heights (256 ints) for the client's light seeding.</summary>
    public static byte[] EncodeHeights(int[] heights)
    {
        var bytes = new byte[heights.Length * sizeof(int)];
        for (var i = 0; i < heights.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * sizeof(int)), heights[i]);
        return bytes;
    }

    /// <summary>Decodes a <see cref="EncodeHeights"/> payload; throws on a wrong length.</summary>
    public static int[] DecodeHeights(byte[] payload)
    {
        if (payload.Length % sizeof(int) != 0)
            throw new ArgumentException($"Heights payload must be a multiple of 4, got {payload.Length}.", nameof(payload));
        var heights = new int[payload.Length / sizeof(int)];
        for (var i = 0; i < heights.Length; i++)
            heights[i] = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(i * sizeof(int)));
        return heights;
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

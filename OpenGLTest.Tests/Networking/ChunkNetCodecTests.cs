using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The chunk/column wire codec the multiplayer client restores streamed chunks from: blocks and
/// state must survive the round trip exactly in every <see cref="ChunkBlocks"/> storage mode and
/// decode to a compact storage, an edited chunk's stale palette entries never reach the client, a
/// malformed chunk payload is refused as <see cref="InvalidDataException"/> and nothing else, and a
/// column's terrain (heights, water levels and corner climate) must round-trip bit for bit, since
/// the client meshes from it instead of worldgen.
/// </summary>
public sealed class ChunkNetCodecTests
{
    [Fact]
    public void BlocksAndStateRoundTripExactly()
    {
        var blocks = new ushort[Chunk.BlockCount];
        var state = new byte[Chunk.BlockCount];
        for (var i = 0; i < blocks.Length; i++)
        {
            blocks[i] = (ushort)(i % 7);
            state[i] = (byte)(i % 5);
        }

        var decoded = ChunkNetCodec.Decode(ChunkNetCodec.Encode(new ChunkSnapshot(blocks, state, playerPlaced: null)));

        Assert.Equal(blocks, decoded.Storage.ToDense());
        Assert.Equal(state, decoded.State);
        Assert.Null(decoded.PlayerPlaced);
    }

    [Fact]
    public void AStatelessChunkRoundTripsWithNullState()
    {
        var blocks = new ushort[Chunk.BlockCount];
        blocks[123] = 5;

        var decoded = ChunkNetCodec.Decode(ChunkNetCodec.Encode(new ChunkSnapshot(blocks, null, playerPlaced: null)));

        Assert.Equal(blocks, decoded.Storage.ToDense());
        Assert.Null(decoded.State);
    }

    /// <summary>Ids spread over the whole id range, from air to the largest id, survive the wire
    /// exactly, with and without a state array beside them.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdsAcrossTheWholeRangeRoundTrip(bool withState)
    {
        var blocks = new ushort[Chunk.BlockCount];
        for (var i = 0; i < blocks.Length; i++)
            blocks[i] = (ushort)((long)i * ushort.MaxValue / (blocks.Length - 1));
        Assert.Equal(BlockType.Air, blocks[0]);
        Assert.Equal(ushort.MaxValue, blocks[^1]);

        byte[]? state = null;
        if (withState)
        {
            state = new byte[Chunk.BlockCount];
            new Random(41).NextBytes(state);
        }

        var decoded = ChunkNetCodec.Decode(ChunkNetCodec.Encode(new ChunkSnapshot(blocks, state, playerPlaced: null)));

        Assert.Equal(blocks, decoded.Storage.ToDense());
        Assert.Equal(state, decoded.State);
    }

    /// <summary>
    /// Whatever storage a chunk's cells compact to (one id, a palette up to its capacity, more
    /// distinct ids than a palette holds), the payload carries every cell and the state, and the
    /// decoded storage is the smallest that holds its ids and is compact. Negative control: a
    /// decoder that kept the sent palette as is, or an encoder that sent a mode's body wrong, fails
    /// the cell or mode check.
    /// </summary>
    [Theory]
    [InlineData(1, (ushort)0, false)]
    [InlineData(1, (ushort)0, true)]
    [InlineData(1, (ushort)7, false)]
    [InlineData(1, (ushort)7, true)]
    [InlineData(5, (ushort)1, false)]
    [InlineData(5, (ushort)1, true)]
    [InlineData(ChunkBlocks.PaletteCapacity, (ushort)1, false)]
    [InlineData(ChunkBlocks.PaletteCapacity, (ushort)1, true)]
    [InlineData(ChunkBlocks.PaletteCapacity + 40, (ushort)1, false)]
    [InlineData(ChunkBlocks.PaletteCapacity + 40, (ushort)1, true)]
    public void EveryStorageModeRoundTripsCellExactly(int distinct, ushort firstId, bool withState)
    {
        var cells = StorageCells.WithDistinctIds(distinct, firstId, seed: 61);
        byte[]? state = null;
        if (withState)
        {
            state = new byte[Chunk.BlockCount];
            new Random(62).NextBytes(state);
        }

        var snapshot = new ChunkSnapshot(cells, state, playerPlaced: null);
        // The sender holds the mode under test, so the payload carries that mode's layout
        Assert.Equal(StorageCells.SmallestModeFor(cells), snapshot.Storage.Mode);

        var decoded = ChunkNetCodec.Decode(ChunkNetCodec.Encode(snapshot));

        Assert.Equal(cells, decoded.Storage.ToDense());
        Assert.Equal(state, decoded.State);
        Assert.Equal(StorageCells.SmallestModeFor(cells), decoded.Storage.Mode);
        Assert.True(decoded.Storage.IsCompact);
    }

    /// <summary>A chunk of one id compresses to less than a chunk of many mixed ids (no byte
    /// count is pinned).</summary>
    [Fact]
    public void AUniformChunkEncodesSmallerThanAMixedOne()
    {
        var uniform = new ushort[Chunk.BlockCount];
        Array.Fill(uniform, (ushort)7);
        var mixed = StorageCells.WithDistinctIds(40, firstId: 1, seed: 63);

        var uniformPayload = ChunkNetCodec.Encode(new ChunkSnapshot(uniform, null, playerPlaced: null));
        var mixedPayload = ChunkNetCodec.Encode(new ChunkSnapshot(mixed, null, playerPlaced: null));

        Assert.True(uniformPayload.Length < mixedPayload.Length,
            $"uniform {uniformPayload.Length} bytes, mixed {mixedPayload.Length} bytes");
    }

    /// <summary>
    /// A live chunk's storage that once held many ids and was overwritten back to two keeps stale
    /// entries in its palette; the network copy of it (a clone, as <see cref="Chunk.CaptureForNetwork"/>
    /// takes) round-trips cell-exactly and decodes to a palette of only the ids its cells hold.
    /// Negative control: a decoder that built its storage straight from the sent palette keeps the
    /// stale entries and fails the palette check.
    /// </summary>
    [Fact]
    public void AnEditedChunksStalePaletteEntriesDecodeAway()
    {
        const int Written = 100;
        var storage = ChunkBlocks.Air;
        for (var i = 0; i < Written; i++)
            storage = storage.Set(i, (ushort)(i + 1), out _);
        for (var i = 0; i < Written; i++)
            storage = storage.Set(i, i < 10 ? (ushort)5 : BlockType.Air, out _);
        var oracle = storage.ToDense();
        var present = oracle.Distinct().Order().ToArray();
        // Anti-vacuity: the sender's palette really holds entries no cell uses any more
        Assert.True(storage.IdSpan.Length > present.Length);

        var decoded = ChunkNetCodec.Decode(ChunkNetCodec.Encode(new ChunkSnapshot(storage.Clone(), null, playerPlaced: null)));

        Assert.Equal(oracle, decoded.Storage.ToDense());
        Assert.Equal(present, decoded.Storage.IdSpan.ToArray().Order().ToArray());
    }

    /// <summary>
    /// Every malformed chunk payload, whichever section it breaks, throws
    /// <see cref="InvalidDataException"/> (the type the client's message loop turns into a clean
    /// disconnect) and no other exception. Negative control: a decoder that let an
    /// <see cref="EndOfStreamException"/> or an <see cref="IndexOutOfRangeException"/> escape, or
    /// read an out-of-palette index as some id, fails here.
    /// </summary>
    [Theory]
    [InlineData("empty payload")]
    [InlineData("no mode byte")]
    [InlineData("unknown mode")]
    [InlineData("truncated uniform id")]
    [InlineData("index at the palette count")]
    [InlineData("index past the palette count")]
    [InlineData("no palette count")]
    [InlineData("truncated palette")]
    [InlineData("truncated indices")]
    [InlineData("truncated direct cells")]
    [InlineData("no state flag")]
    [InlineData("state flag 2")]
    [InlineData("truncated state")]
    [InlineData("cut compressed stream")]
    public void AMalformedChunkPayloadIsRefusedAsInvalidData(string malformation)
    {
        var payload = MalformedPayload(malformation);

        Assert.Throws<InvalidDataException>(() => ChunkNetCodec.Decode(payload));
    }

    [Fact]
    public void AStreamedColumnRoundTripsExactly()
    {
        var terrain = MixedColumn();

        var decoded = ChunkNetCodec.DecodeColumn(ChunkNetCodec.EncodeColumn(terrain));

        Assert.Equal(terrain.Heights, decoded.Heights);
        Assert.Equal(terrain.WaterLevel, decoded.WaterLevel);
        Assert.Equal(BitConverter.SingleToInt32Bits(terrain.Warmth), BitConverter.SingleToInt32Bits(decoded.Warmth));
        Assert.Equal(BitConverter.SingleToInt32Bits(terrain.Wetness), BitConverter.SingleToInt32Bits(decoded.Wetness));
        Assert.True(decoded.ContentEquals(terrain));
    }

    [Fact]
    public void ATruncatedColumnPayloadIsRefused()
    {
        var payload = ChunkNetCodec.EncodeColumn(MixedColumn());

        // Every cut, down to an empty payload, is one malformed-frame exception type
        foreach (var length in new[] { payload.Length / 2, payload.Length / 4, 1, 0 })
            Assert.Throws<InvalidDataException>(() => ChunkNetCodec.DecodeColumn(payload[..length]));
    }

    [Fact]
    public void AColumnOfTheWrongSizeIsNotEncoded()
    {
        var full = MixedColumn();
        var shortHeights = new ColumnTerrain(new int[Chunk.Size], full.WaterLevel, full.Warmth, full.Wetness);
        var longWater = new ColumnTerrain(full.Heights, new int[Chunk.Size * Chunk.Size + 1], full.Warmth, full.Wetness);

        Assert.Throws<ArgumentException>(() => ChunkNetCodec.EncodeColumn(shortHeights));
        Assert.Throws<ArgumentException>(() => ChunkNetCodec.EncodeColumn(longWater));
        Assert.Throws<ArgumentException>(() => ChunkNetCodec.EncodeColumn(default));
    }

    // A chunk payload broken in one section; the rest of it is well formed.
    private static byte[] MalformedPayload(string malformation)
    {
        byte[] uniformSeven = [0, 7, 0];
        return malformation switch
        {
            "empty payload" => [],
            "no mode byte" => Deflated(),
            "unknown mode" => Deflated([3], [0]),
            "truncated uniform id" => Deflated([0, 7]),
            "index at the palette count" => Deflated(IndexedHeader(3), IndicesWith(3), [0]),
            "index past the palette count" => Deflated(IndexedHeader(3), IndicesWith(200), [0]),
            "no palette count" => Deflated([1]),
            "truncated palette" => Deflated([1, 3, 7, 0, 8]),
            "truncated indices" => Deflated(IndexedHeader(3), new byte[Chunk.BlockCount / 2]),
            "truncated direct cells" => Deflated([2], new byte[Chunk.BlockCount]),
            "no state flag" => Deflated(uniformSeven),
            "state flag 2" => Deflated(uniformSeven, [2]),
            "truncated state" => Deflated(uniformSeven, [1], new byte[Chunk.BlockCount - 1]),
            "cut compressed stream" => CutInHalf(ChunkNetCodec.Encode(new ChunkSnapshot(
                StorageCells.WithDistinctIds(40, firstId: 1, seed: 64), new byte[Chunk.BlockCount], playerPlaced: null))),
            _ => throw new ArgumentOutOfRangeException(nameof(malformation), malformation, null),
        };
    }

    // The Indexed mode byte, the count byte and a palette of `count` ids (1, 2, ...)
    private static byte[] IndexedHeader(int count)
    {
        var header = new byte[2 + count * sizeof(ushort)];
        header[0] = 1;
        header[1] = (byte)(count - 1);
        for (var k = 0; k < count; k++)
            header[2 + k * sizeof(ushort)] = (byte)(k + 1);
        return header;
    }

    // One index byte per cell, all 0 but the last, which is `index`
    private static byte[] IndicesWith(byte index)
    {
        var indices = new byte[Chunk.BlockCount];
        indices[^1] = index;
        return indices;
    }

    private static byte[] CutInHalf(byte[] payload) => payload[..(payload.Length / 2)];

    // The sections Deflate-compressed back to back, as the codec frames a payload
    private static byte[] Deflated(params byte[][] sections)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            foreach (var section in sections)
                deflate.Write(section);
        }

        return output.ToArray();
    }

    // Heights with negatives, water levels mixing real levels and the no-water sentinel, and
    // climate values that are not round numbers.
    private static ColumnTerrain MixedColumn()
    {
        var heights = new int[Chunk.Size * Chunk.Size];
        var water = new int[Chunk.Size * Chunk.Size];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = i * 3 - 400;
            water[i] = i % 3 == 0 ? TerrainGenerator.NoWater : i - 100;
        }

        return new ColumnTerrain(heights, water, 0.12345679f, 0.8764432f);
    }
}

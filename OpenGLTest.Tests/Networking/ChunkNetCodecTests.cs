using System;
using System.IO;
using VoxelEngine.Networking;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The chunk/column wire codec the multiplayer client restores streamed chunks from: blocks and
/// state must survive the round trip exactly, and a column's terrain (heights, water levels and
/// corner climate) must round-trip bit for bit, since the client meshes from it instead of worldgen.
/// </summary>
public sealed class ChunkNetCodecTests
{
    [Fact]
    public void BlocksAndStateRoundTripExactly()
    {
        var blocks = new byte[Chunk.BlockCount];
        var state = new byte[Chunk.BlockCount];
        for (var i = 0; i < blocks.Length; i++)
        {
            blocks[i] = (byte)(i % 7);
            state[i] = (byte)(i % 5);
        }

        var decoded = ChunkNetCodec.Decode(ChunkNetCodec.Encode(new ChunkSnapshot(blocks, state, null)));

        Assert.Equal(blocks, decoded.Blocks);
        Assert.Equal(state, decoded.State);
        Assert.Null(decoded.PlayerPlaced);
    }

    [Fact]
    public void AStatelessChunkRoundTripsWithNullState()
    {
        var blocks = new byte[Chunk.BlockCount];
        blocks[123] = 5;

        var decoded = ChunkNetCodec.Decode(ChunkNetCodec.Encode(new ChunkSnapshot(blocks, null, null)));

        Assert.Equal(blocks, decoded.Blocks);
        Assert.Null(decoded.State);
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

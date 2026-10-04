using System;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The chunk/column wire codec the multiplayer client restores streamed chunks from: blocks and
/// state must survive the round trip exactly, and heights must round-trip for light seeding.
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
    public void StreamedHeightsRoundTripExactly()
    {
        var heights = new int[Chunk.Size * Chunk.Size];
        for (var i = 0; i < heights.Length; i++)
            heights[i] = i * 3 - 40;

        var decoded = ChunkNetCodec.DecodeHeights(ChunkNetCodec.EncodeHeights(heights));

        Assert.Equal(heights, decoded);
    }

    [Fact]
    public void AHeightsPayloadOfTheWrongLengthIsRefused()
    {
        Assert.Throws<ArgumentException>(() => ChunkNetCodec.DecodeHeights([1, 2, 3]));
    }
}

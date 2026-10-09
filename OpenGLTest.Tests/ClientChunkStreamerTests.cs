using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The client's streamed-chunk path: an applied snapshot becomes readable blocks, the streamer
/// asks for the chunks and columns around the camera without duplicating a request, and a streamed
/// column feeds the lighting height source.
/// </summary>
public sealed class ClientChunkStreamerTests
{
    public ClientChunkStreamerTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ApplyingAStreamedChunkMakesItsBlocksReadable()
    {
        var manager = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var blocks = new byte[Chunk.BlockCount];
        blocks[Chunk.Idx(0, 0, 0)] = stone;

        manager.ApplyStreamedChunk(Vector3.Zero, new ChunkSnapshot(blocks, null, null));

        Assert.Equal(stone, manager.GetBlockGlobal(0, 0, 0));
    }

    [Fact]
    public void UpdateRequestsTheNearestChunksAndColumnsWithinABudget()
    {
        var manager = new ChunkManager(null!);
        var chunks = new List<(int X, int Y, int Z)>();
        var columns = new List<(int X, int Z)>();
        var streamer = new ClientChunkStreamer(manager, (x, y, z) => chunks.Add((x, y, z)), (x, z) => columns.Add((x, z)));

        streamer.Update(Vector3.Zero, 0.016);

        Assert.NotEmpty(chunks);
        Assert.NotEmpty(columns);
        Assert.True(chunks.Count <= 16, $"asked for {chunks.Count} chunks in one update");
        Assert.Equal((0, 0, 0), chunks[0]);
    }

    [Fact]
    public void RepeatedUpdatesNeverAskForTheSameChunkTwiceQuickly()
    {
        var manager = new ChunkManager(null!);
        var asked = new List<(int, int, int)>();
        var streamer = new ClientChunkStreamer(manager, (x, y, z) => asked.Add((x, y, z)), (_, _) => { });

        streamer.Update(Vector3.Zero, 0.016);
        streamer.Update(Vector3.Zero, 0.016);

        Assert.Equal(asked.Count, asked.Distinct().Count());
    }

    [Fact]
    public void AColumnPayloadFeedsTheLightingHeightSource()
    {
        var manager = new ChunkManager(null!);
        var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (_, _) => { });
        var heights = new int[Chunk.Size * Chunk.Size];
        Array.Fill(heights, 42);

        streamer.ApplyColumn(2, -3, ChunkNetCodec.EncodeHeights(heights));

        var source = manager.ColumnHeightSource;
        Assert.NotNull(source);
        Assert.Equal(heights, source!(2, -3));
        Assert.Null(source(0, 0));
    }

    [Fact]
    public void AReceivedColumnIsNeverAskedForAgain()
    {
        var manager = new ChunkManager(null!);
        var columns = new List<(int X, int Z)>();
        var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (x, z) => columns.Add((x, z)));
        streamer.Update(Vector3.Zero, 0.016);
        Assert.Contains((0, 0), columns);

        streamer.ApplyColumn(0, 0, ChunkNetCodec.EncodeHeights(new int[Chunk.Size * Chunk.Size]));
        columns.Clear();
        // Long past every resend interval, with the column's chunks still missing
        for (var i = 0; i < 20; i++)
            streamer.Update(Vector3.Zero, 1.0);

        Assert.DoesNotContain((0, 0), columns);
    }

    [Fact]
    public void OnlyAChangedColumnCountsAsNew()
    {
        var manager = new ChunkManager(null!);
        var streamer = new ClientChunkStreamer(manager, (_, _, _) => { }, (_, _) => { });
        var heights = new int[Chunk.Size * Chunk.Size];
        Array.Fill(heights, 12);

        Assert.True(streamer.ApplyColumn(1, 1, ChunkNetCodec.EncodeHeights(heights)));
        // The same heights again (a duplicate answer) change nothing and re-dirty nothing
        Assert.False(streamer.ApplyColumn(1, 1, ChunkNetCodec.EncodeHeights(heights)));

        var raised = (int[])heights.Clone();
        raised[5] += 3;
        Assert.True(streamer.ApplyColumn(1, 1, ChunkNetCodec.EncodeHeights(raised)));
        Assert.Equal(raised, manager.ColumnHeightSource!(1, 1));
    }

    [Fact]
    public void AStreamedChunkIsNotQueuedForTheWorldgenWaterWake()
    {
        // The wake queue is only drained by FluidManager.WakeLoadedChunks, which a client never
        // runs; a streamed chunk left in it would stay referenced for the whole session.
        var manager = new ChunkManager(null!);
        manager.ApplyStreamedChunk(Vector3.Zero, new ChunkSnapshot(new byte[Chunk.BlockCount], null, null));

        Assert.False(manager.TryDequeueLoaded(out _));
    }
}
